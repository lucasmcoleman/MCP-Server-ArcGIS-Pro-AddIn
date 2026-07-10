using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.GeoProcessing;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using APBridgeAddIn.ModelBuilder;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    // Partial: new capability families live in sibling ProBridgeService.*.cs files
    // (View, Editing, MapAdmin, Layout, Symbology, Catalog, Python, GpCatalog) so
    // this dispatcher file stays navigable.
    internal partial class ProBridgeService : IDisposable
    {
        private readonly string _pipeName;
        private CancellationTokenSource _cts;
        private Task _serverLoop;

        public ProBridgeService(string pipeName) => _pipeName = pipeName;

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _serverLoop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); _serverLoop?.Wait(2000); } catch { }
        }

        /// <summary>
        /// Accept loop. Spins up a fresh listener instance immediately after each
        /// connection is accepted, and serves every accepted connection on its own
        /// task. Concurrency matters even for a single MCP client: BridgeClient
        /// opens one connection PER REQUEST, so with a single-instance server a
        /// long-running op (multi-minute run_gp_tool) would leave no listener
        /// behind — even pro.ping and pro.getRunStatus polls would burn their
        /// connect timeout and fail until the long op finished. Actual ArcGIS Pro
        /// work is still serialized by QueuedTask.Run on Pro's MCT; concurrency
        /// here only overlaps IPC handling and pure file ops.
        /// </summary>
        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    // CurrentUserOnly: restricts the pipe ACL to the user running
                    // Pro. Without it the default DACL lets other local users
                    // connect and drive geoprocessing as this user.
                    server = new NamedPipeServerStream(_pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);

                    var conn = server;
                    server = null; // ownership transferred to the serving task
                    _ = Task.Run(() => ServeConnectionAsync(conn, ct), CancellationToken.None);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    server?.Dispose();
                    break; // Clean shutdown
                }
                catch (Exception)
                {
                    // Pipe broke or other transient error — restart the listener.
                    // Small delay prevents tight spin if errors repeat.
                    server?.Dispose();
                    try { await Task.Delay(100, ct); } catch { break; }
                }
            }
        }

        /// <summary>
        /// Serves one accepted pipe connection: reads line-delimited JSON requests
        /// until the client disconnects, dispatching each through HandleAsync.
        /// Multiple instances of this method run concurrently (one per client
        /// connection); QueuedTask serializes the actual Pro SDK work.
        /// </summary>
        private static async Task ServeConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            try
            {
                using var _ = server;
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true };

                while (server.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    IpcRequest req;
                    try
                    {
                        req = JsonSerializer.Deserialize<IpcRequest>(line);
                    }
                    catch (Exception ex)
                    {
                        await SendAsync(writer, new IpcResponse(false, $"parse:{ex.Message}", null));
                        continue;
                    }

                    try
                    {
                        var resp = await HandleAsync(req, ct);
                        if (!resp.Ok)
                            LogNonSuccess(req, resp.Error);
                        await SendAsync(writer, resp);
                    }
                    catch (Exception ex)
                    {
                        LogException(req, ex);
                        await SendAsync(writer, new IpcResponse(false,
                            $"{ex.GetType().Name}: {ex.Message ?? "<no message>"}", null));
                    }
                }
            }
            catch
            {
                // Connection-level failure (client vanished mid-write, pipe broke).
                // The accept loop keeps listening; nothing to do here.
            }
        }

        // AllowNamedFloatingPointLiterals is important because ArcGIS Pro SDK
        // occasionally returns NaN / ±Infinity in double-valued properties
        // (Camera.Pitch in 2D mode, Envelope dimensions on uninitialized views).
        // Default STJ throws ArgumentException; named-literals serializes as
        // "NaN" / "Infinity" strings so the bridge can still return a response.
        private static readonly JsonSerializerOptions _sendOpts = new()
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private static Task SendAsync(StreamWriter w, IpcResponse resp)
            => w.WriteLineAsync(JsonSerializer.Serialize(resp, _sendOpts));

        /// <summary>
        /// Main dispatcher: routes <see cref="IpcRequest.Op"/> strings to per-op handlers.
        /// Errors from handlers are caught two layers up in <see cref="RunAsync"/> — this
        /// method is intentionally thin so each case is one line of routing. New ops:
        /// add a case here AND a wrapper in <c>McpServer/ArcGisMcpServer/Tools/ProTools.cs</c>.
        /// </summary>
        private static async Task<IpcResponse> HandleAsync(IpcRequest req, CancellationToken ct)
        {
            switch (req.Op)
            {
                // ─── Existing Map Operations ────────────────────────────────
                case "pro.getActiveMapName":
                    var name = MapView.Active?.Map?.Name ?? "<none>";
                    return new(true, null, new { name });

                case "pro.listLayers":
                {
                    string? mapName = null;
                    req.Args?.TryGetValue("map", out mapName);

                    var allNames = await QueuedTask.Run(() =>
                    {
                        var map = ResolveMap(mapName);
                        var names = new List<string>();
                        // Layers first (flattened tree, group layers + their children),
                        // then standalone tables. Both contribute to the response so
                        // the agent sees the full set of addressable map members.
                        names.AddRange(map.GetLayersAsFlattenedList().Select(l => l.Name));
                        names.AddRange(map.StandaloneTables.Select(t => t.Name));
                        return names;
                    });
                    return new(true, null, allNames);
                }

                case "pro.countFeatures":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName))
                        return new(false, "arg 'layer' required", null);
                    req.Args.TryGetValue("map", out string? mapName);

                    int count = await QueuedTask.Run(() =>
                    {
                        var map = ResolveMap(mapName);
                        var member = RequireMapMember(map, layerName);
                        // Both FeatureClass (for FeatureLayer) and Table (for
                        // StandaloneTable) expose GetCount(); FeatureClass inherits
                        // from Table, so the count_features semantics extend
                        // naturally to standalone tables ("count rows").
                        using var table = GetTableFromMember(member)
                            ?? throw new InvalidOperationException(
                                $"'{member.Name}' is a {member.GetType().Name} with no attribute table — count_features works on feature layers and standalone tables.");
                        return (int)table.GetCount();
                    });
                    return new(true, null, new { count });
                }

                case "pro.zoomToLayer":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName))
                        return new(false, "arg 'layer' required", null);

                    await QueuedTask.Run(async () =>
                    {
                        var map = MapView.Active?.Map
                            ?? throw new InvalidOperationException("No active map view");
                        // Any layer type zooms (raster, group, service...) — not
                        // just FeatureLayer; ZoomToAsync accepts the Layer base.
                        var target = map.GetLayersAsFlattenedList()
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException(
                                $"Layer not found: {layerName}. Available: " +
                                string.Join(", ", map.GetLayersAsFlattenedList().Select(l => l.Name)));
                        await MapView.Active!.ZoomToAsync(target);
                    });
                    return new(true, null, new { done = true });
                }

                case "pro.selectByAttribute":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName) ||
                        !req.Args.TryGetValue("where", out string? where) ||
                        string.IsNullOrWhiteSpace(where))
                        return new(false, "args 'layer' & 'where' required", null);
                    req.Args.TryGetValue("map", out string? sbaMapName);

                    var selectionInfo = await QueuedTask.Run<object>(() =>
                    {
                        var map = ResolveMap(sbaMapName);
                        var member = RequireMapMember(map, layerName);
                        // Both FeatureLayer and StandaloneTable expose Select(QueryFilter),
                        // but the methods are declared on the subclasses (not on MapMember),
                        // so dispatch explicitly. Returns a Selection on either path.
                        var qf = new ArcGIS.Core.Data.QueryFilter { WhereClause = where };
                        var sel = member switch
                        {
                            FeatureLayer flSba => flSba.Select(qf),
                            ArcGIS.Desktop.Mapping.StandaloneTable stSba => stSba.Select(qf),
                            _ => throw new InvalidOperationException(
                                $"'{member.Name}' is a {member.GetType().Name} which doesn't support selection — select_by_attribute works on feature layers and standalone tables.")
                        };
                        return (object)new { layer = member.Name, selectedCount = sel?.GetCount() ?? 0 };
                    });
                    return new(true, null, selectionInfo);
                }

                case "pro.listFields":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? lfLayerName) ||
                        string.IsNullOrWhiteSpace(lfLayerName))
                        return new(false, "arg 'layer' required", null);
                    req.Args.TryGetValue("map", out string? lfMapName);

                    var data = await QueuedTask.Run<object>(() =>
                    {
                        var map = ResolveMap(lfMapName);
                        var member = RequireMapMember(map, lfLayerName);

                        // FeatureClass.GetDefinition() returns FeatureClassDefinition;
                        // Table.GetDefinition() returns TableDefinition. The former
                        // inherits from the latter, so GetFields() works uniformly.
                        using var table = GetTableFromMember(member)
                            ?? throw new InvalidOperationException(
                                $"'{member.Name}' is a {member.GetType().Name} with no attribute table — list_fields works on feature layers and standalone tables.");
                        var fields = table.GetDefinition().GetFields()
                            .Select(f => new
                            {
                                name = f.Name,
                                alias = f.AliasName,
                                type = f.FieldType.ToString(),
                                length = f.Length,
                                isNullable = f.IsNullable,
                                isEditable = f.IsEditable
                            })
                            .ToList();

                        return new { layer = member.Name, fields };
                    });
                    return new(true, null, data);
                }

                case "pro.getLayerProperties":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? lpLayerName) ||
                        string.IsNullOrWhiteSpace(lpLayerName))
                        return new(false, "arg 'layer' required", null);
                    req.Args.TryGetValue("map", out string? lpMapName);

                    var data = await QueuedTask.Run<object>(() =>
                    {
                        var map = ResolveMap(lpMapName);
                        var member = RequireMapMember(map, lpLayerName);

                        // Build properties dict incrementally — different member types
                        // expose different things; wrap each accessor in try/catch so
                        // a missing property (e.g., SR on a basemap, or extent on a
                        // standalone table) doesn't blow up the whole response.
                        var props = new Dictionary<string, object?>
                        {
                            ["name"] = member.Name,
                            ["type"] = member.GetType().Name
                        };

                        if (member is Layer layer)
                        {
                            // Spatial-member properties: visibility, SR, extent, and
                            // (for FeatureLayer) geometry type, feature count, source path.
                            props["isVisible"] = layer.IsVisible;

                            try { props["transparency"] = layer.Transparency; } catch { }
                            try
                            {
                                if (layer is BasicFeatureLayer bflDq &&
                                    !string.IsNullOrEmpty(bflDq.DefinitionQuery))
                                    props["definitionQuery"] = bflDq.DefinitionQuery;
                            }
                            catch { }

                            try
                            {
                                var sr = layer.GetSpatialReference();
                                if (sr != null)
                                    props["spatialReference"] = new { wkid = sr.Wkid, name = sr.Name };
                            }
                            catch { }

                            try
                            {
                                var extent = layer.QueryExtent();
                                if (extent != null)
                                    props["extent"] = new
                                    {
                                        xmin = extent.XMin,
                                        ymin = extent.YMin,
                                        xmax = extent.XMax,
                                        ymax = extent.YMax
                                    };
                            }
                            catch { }

                            if (layer is FeatureLayer flProps)
                            {
                                try
                                {
                                    props["geometryType"] = flProps.ShapeType.ToString();
                                    using var fc = flProps.GetFeatureClass();
                                    if (fc != null)
                                    {
                                        props["featureCount"] = (int)fc.GetCount();
                                        props["dataSource"] = fc.GetPath()?.ToString();
                                    }
                                }
                                catch { }
                            }
                        }
                        else if (member is ArcGIS.Desktop.Mapping.StandaloneTable st)
                        {
                            // Standalone tables have no geometry or spatial reference;
                            // surface what they DO have: row count + source path.
                            try
                            {
                                using var table = st.GetTable();
                                if (table != null)
                                {
                                    props["rowCount"] = (int)table.GetCount();
                                    props["dataSource"] = table.GetPath()?.ToString();
                                }
                            }
                            catch { }
                        }

                        return (object)props;
                    });
                    return new(true, null, data);
                }

                case "pro.readLayerAttributes":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? raLayerName) ||
                        string.IsNullOrWhiteSpace(raLayerName))
                        return new(false, "arg 'layer' required", null);
                    req.Args.TryGetValue("map", out string? raMapName);

                    req.Args.TryGetValue("fields", out string? fieldsStr);
                    req.Args.TryGetValue("where", out string? whereClause);
                    req.Args.TryGetValue("orderBy", out string? orderBy);
                    int limit = 50;
                    if (req.Args.TryGetValue("limit", out string? limitStr) &&
                        int.TryParse(limitStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
                    {
                        // Clamp to [1, 1000] to keep responses tractable. Agents
                        // hitting the upper bound get `limited: true` and can
                        // narrow with `where` or paginate by ORDER BY + OID range.
                        limit = Math.Max(1, Math.Min(1000, parsedLimit));
                    }

                    var requestedFields = string.IsNullOrWhiteSpace(fieldsStr)
                        ? null
                        : fieldsStr.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();

                    var data = await QueuedTask.Run<object>(() =>
                    {
                        var map = ResolveMap(raMapName);
                        var member = RequireMapMember(map, raLayerName);

                        // FeatureClass for feature layers, Table for standalone tables.
                        // Both share GetDefinition()/GetFields() via TableDefinition;
                        // GetShapeField is on FeatureClassDefinition only, so we narrow.
                        using var table = GetTableFromMember(member)
                            ?? throw new InvalidOperationException(
                                $"'{member.Name}' is a {member.GetType().Name} with no attribute table — read_layer_attributes works on feature layers and standalone tables.");
                        var tableDef = table.GetDefinition();
                        var allFields = tableDef.GetFields();
                        var shapeFieldName = (tableDef is ArcGIS.Core.Data.FeatureClassDefinition fcd)
                            ? fcd.GetShapeField()
                            : null;

                        // Output field set: requested fields verbatim (validate
                        // each exists), or all non-geometry/blob/raster fields.
                        List<ArcGIS.Core.Data.Field> outputFields;
                        if (requestedFields == null)
                        {
                            outputFields = allFields
                                .Where(f => !string.Equals(f.Name, shapeFieldName, StringComparison.OrdinalIgnoreCase))
                                .Where(f => f.FieldType != ArcGIS.Core.Data.FieldType.Blob &&
                                            f.FieldType != ArcGIS.Core.Data.FieldType.Raster &&
                                            f.FieldType != ArcGIS.Core.Data.FieldType.Geometry)
                                .ToList();
                        }
                        else
                        {
                            outputFields = new List<ArcGIS.Core.Data.Field>();
                            foreach (var name in requestedFields)
                            {
                                var match = allFields.FirstOrDefault(f =>
                                    f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                                if (match == null)
                                    throw new InvalidOperationException($"Field not found: {name}");
                                outputFields.Add(match);
                            }
                        }

                        var queryFilter = new ArcGIS.Core.Data.QueryFilter
                        {
                            WhereClause = whereClause ?? string.Empty,
                            PostfixClause = string.IsNullOrWhiteSpace(orderBy) ? string.Empty : $"ORDER BY {orderBy}"
                        };

                        var rows = new List<Dictionary<string, object?>>();
                        bool limited = false;
                        using (var cursor = table.Search(queryFilter, false))
                        {
                            while (cursor.MoveNext())
                            {
                                if (rows.Count >= limit) { limited = true; break; }
                                using var row = cursor.Current;
                                var rowDict = new Dictionary<string, object?>();
                                foreach (var field in outputFields)
                                {
                                    var val = row[field.Name];
                                    // Coerce types that aren't JSON-native into
                                    // strings so the bridge's reflection-based
                                    // serializer doesn't choke.
                                    rowDict[field.Name] = val switch
                                    {
                                        null => null,
                                        DateTime dt => (object)dt.ToString("o", CultureInfo.InvariantCulture),
                                        Guid g => (object)g.ToString(),
                                        _ => val
                                    };
                                }
                                rows.Add(rowDict);
                            }
                        }

                        return (object)new
                        {
                            layer = member.Name,
                            fieldNames = outputFields.Select(f => f.Name).ToList(),
                            rows,
                            returned = rows.Count,
                            limited
                        };
                    });
                    return new(true, null, data);
                }

                case "pro.getSelectedFeatures":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? gsfLayerName) ||
                        string.IsNullOrWhiteSpace(gsfLayerName))
                        return new(false, "arg 'layer' required", null);
                    req.Args.TryGetValue("map", out string? gsfMapName);

                    req.Args.TryGetValue("fields", out string? gsfFieldsStr);
                    int gsfLimit = 50;
                    if (req.Args.TryGetValue("limit", out string? gsfLimitStr) &&
                        int.TryParse(gsfLimitStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gsfParsedLimit))
                    {
                        gsfLimit = Math.Max(1, Math.Min(1000, gsfParsedLimit));
                    }

                    var gsfRequestedFields = string.IsNullOrWhiteSpace(gsfFieldsStr)
                        ? null
                        : gsfFieldsStr.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();

                    var data = await QueuedTask.Run<object>(() =>
                    {
                        var map = ResolveMap(gsfMapName);
                        var member = RequireMapMember(map, gsfLayerName);

                        using var table = GetTableFromMember(member)
                            ?? throw new InvalidOperationException(
                                $"'{member.Name}' is a {member.GetType().Name} with no attribute table — get_selected_features works on feature layers and standalone tables.");
                        var tableDef = table.GetDefinition();
                        var allFields = tableDef.GetFields();
                        var shapeFieldName = (tableDef is ArcGIS.Core.Data.FeatureClassDefinition fcdGsf)
                            ? fcdGsf.GetShapeField()
                            : null;

                        // Output field resolution — same logic as read_layer_attributes
                        List<ArcGIS.Core.Data.Field> outputFields;
                        if (gsfRequestedFields == null)
                        {
                            outputFields = allFields
                                .Where(f => !string.Equals(f.Name, shapeFieldName, StringComparison.OrdinalIgnoreCase))
                                .Where(f => f.FieldType != ArcGIS.Core.Data.FieldType.Blob &&
                                            f.FieldType != ArcGIS.Core.Data.FieldType.Raster &&
                                            f.FieldType != ArcGIS.Core.Data.FieldType.Geometry)
                                .ToList();
                        }
                        else
                        {
                            outputFields = new List<ArcGIS.Core.Data.Field>();
                            foreach (var name in gsfRequestedFields)
                            {
                                var match = allFields.FirstOrDefault(f =>
                                    f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                                if (match == null)
                                    throw new InvalidOperationException($"Field not found: {name}");
                                outputFields.Add(match);
                            }
                        }

                        // Read from the member's current Selection (not the underlying
                        // table). Empty selection returns an empty rows list and
                        // selectedTotal=0 rather than an error. FeatureLayer and
                        // StandaloneTable both expose GetSelection() returning a
                        // common Selection type — but the methods themselves are
                        // declared on the subclasses, not on MapMember, so we dispatch.
                        using var selection = member switch
                        {
                            FeatureLayer flGsf => flGsf.GetSelection(),
                            ArcGIS.Desktop.Mapping.StandaloneTable stGsf => stGsf.GetSelection(),
                            _ => throw new InvalidOperationException(
                                $"'{member.Name}' is a {member.GetType().Name} which doesn't support selection.")
                        };
                        long selectedTotal = selection.GetCount();

                        var rows = new List<Dictionary<string, object?>>();
                        bool limited = false;
                        if (selectedTotal > 0)
                        {
                            using var cursor = selection.Search(null, false);
                            while (cursor.MoveNext())
                            {
                                if (rows.Count >= gsfLimit) { limited = true; break; }
                                using var row = cursor.Current;
                                var rowDict = new Dictionary<string, object?>();
                                foreach (var field in outputFields)
                                {
                                    var val = row[field.Name];
                                    rowDict[field.Name] = val switch
                                    {
                                        null => null,
                                        DateTime dt => (object)dt.ToString("o", CultureInfo.InvariantCulture),
                                        Guid g => (object)g.ToString(),
                                        _ => val
                                    };
                                }
                                rows.Add(rowDict);
                            }
                        }

                        return (object)new
                        {
                            layer = member.Name,
                            fieldNames = outputFields.Select(f => f.Name).ToList(),
                            rows,
                            returned = rows.Count,
                            selectedTotal,
                            limited
                        };
                    });
                    return new(true, null, data);
                }

                case "pro.getCurrentExtent":
                    return await HandleGetCurrentExtent();

                case "pro.getViewDiagnostics":
                    return await HandleGetViewDiagnostics();

                case "pro.clearSelection":
                    return await HandleClearSelection(req.Args);

                case "pro.removeLayer":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName))
                        return new(false, "arg 'layer' required", null);

                    // Search Map.Layers (not OfType<FeatureLayer>) so we can
                    // remove any layer type — raster, web, group, basemap, etc.
                    var result = await QueuedTask.Run<object?>(() =>
                    {
                        var map = MapView.Active?.Map;
                        if (map == null) return null;
                        var layer = map.GetLayersAsFlattenedList()
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                        if (layer == null) return null;
                        var actualName = layer.Name;
                        map.RemoveLayer(layer);
                        return (object)new { removed = actualName };
                    });

                    if (result == null)
                        return new(false, $"Layer not found: {layerName}", null);
                    return new(true, null, result);
                }

                case "pro.renameLayer":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName) ||
                        !req.Args.TryGetValue("newName", out string? newName) ||
                        string.IsNullOrWhiteSpace(newName))
                        return new(false, "args 'layer' and 'newName' required", null);

                    var result = await QueuedTask.Run<object?>(() =>
                    {
                        var map = MapView.Active?.Map;
                        if (map == null) return null;
                        var layer = map.GetLayersAsFlattenedList()
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                        if (layer == null) return null;
                        var oldName = layer.Name;
                        layer.SetName(newName);
                        // Pro may auto-uniquify if newName conflicts (e.g. 'Foo' → 'Foo (2)');
                        // surface the actual post-rename name so the agent sees ground truth.
                        return (object)new { renamed = new { from = oldName, to = layer.Name } };
                    });

                    if (result == null)
                        return new(false, $"Layer not found: {layerName}", null);
                    return new(true, null, result);
                }

                case "pro.setLayerVisibility":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName) ||
                        !req.Args.TryGetValue("visible", out string? visStr))
                        return new(false, "args 'layer' and 'visible' required", null);
                    if (!bool.TryParse(visStr, out bool visible))
                        return new(false, $"arg 'visible' must be 'true' or 'false', got '{visStr}'", null);

                    var result = await QueuedTask.Run<object?>(() =>
                    {
                        var map = MapView.Active?.Map;
                        if (map == null) return null;
                        var layer = map.GetLayersAsFlattenedList()
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                        if (layer == null) return null;
                        layer.SetVisibility(visible);
                        return (object)new { layer = layer.Name, visible };
                    });

                    if (result == null)
                        return new(false, $"Layer not found: {layerName}", null);
                    return new(true, null, result);
                }

                case "pro.moveLayer":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName) ||
                        !req.Args.TryGetValue("position", out string? posStr))
                        return new(false, "args 'layer' and 'position' required", null);
                    if (!int.TryParse(posStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int position))
                        return new(false, $"arg 'position' must be an integer, got '{posStr}'", null);

                    var result = await QueuedTask.Run<object?>(() =>
                    {
                        var map = MapView.Active?.Map;
                        if (map == null) return null;
                        // move_layer operates on the top-level TOC ordering only —
                        // reordering within or out of a group is a different op
                        // semantically, so this handler uses map.Layers (top-level)
                        // rather than the flattened tree the other handlers use.
                        var topLayers = map.Layers;
                        var layer = topLayers
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                        if (layer == null) return null;
                        // 0 = topmost. Clamp out-of-range silently rather than erroring;
                        // an LLM saying "move it to the top" might pass 0 reliably but
                        // "to the bottom" might miscount and pass Count or Count-1.
                        int clamped = Math.Max(0, Math.Min(position, topLayers.Count - 1));
                        map.MoveLayer(layer, clamped);
                        return (object)new { moved = new { layer = layer.Name, position = clamped } };
                    });

                    if (result == null)
                        return new(false, $"Layer not found: {layerName}", null);
                    return new(true, null, result);
                }

                case "pro.getProjectInfo":
                    return await HandleGetProjectInfo();

                case "pro.listMaps":
                    return await HandleListMaps();

                case "pro.exportLayer":
                    return await HandleExportLayer(req.Args);

                // ─── Project Operations ─────────────────────────────────────
                case "pro.createProject":
                    return await HandleCreateProject(req.Args);

                case "pro.openProject":
                    return await HandleOpenProject(req.Args);

                case "pro.saveProject":
                    return await HandleSaveProject();

                // ─── Layer-from-URL / File ──────────────────────────────────
                case "pro.addLayerFromUrl":
                    return await HandleAddLayerFromUrl(req.Args);

                case "pro.addLayerFromFile":
                    return await HandleAddLayerFromFile(req.Args);

                // ─── Layout Operations ──────────────────────────────────────
                case "pro.listLayouts":
                    return await HandleListLayouts();

                case "pro.createLayout":
                    return await HandleCreateLayout(req.Args);

                case "pro.openLayout":
                    return await HandleOpenLayout(req.Args);

                case "pro.listLayoutElements":
                    return await HandleListLayoutElements(req.Args);

                case "pro.setLayoutText":
                    return await HandleSetLayoutText(req.Args);

                case "pro.addMapFrameToLayout":
                    return await HandleAddMapFrameToLayout(req.Args);

                case "pro.exportLayout":
                    return await HandleExportLayout(req.Args);

                // ─── ModelBuilder Operations ────────────────────────────────
                case "pro.listToolboxes":
                    return await HandleListToolboxes();

                case "pro.listModels":
                    return HandleListModels(req.Args);

                case "pro.describeModel":
                    return HandleDescribeModel(req.Args);

                case "pro.createToolbox":
                    return await HandleCreateToolbox(req.Args);

                case "pro.createModel":
                    return HandleCreateModel(req.Args);

                case "pro.updateModel":
                    return HandleUpdateModel(req.Args);

                case "pro.setParameterDefault":
                    return HandleSetParameterDefault(req.Args);

                case "pro.setStepParameter":
                    return HandleSetStepParameter(req.Args);

                case "pro.runModel":
                    return await HandleRunModel(req.Args);

                case "pro.runModelAsync":
                    return HandleRunModelAsync(req.Args);

                case "pro.getRunStatus":
                    return HandleGetRunStatus(req.Args);

                case "pro.runGPTool":
                    return await HandleRunGPTool(req.Args);

                case "pro.addPointFeatures":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? apfLayerName) ||
                        string.IsNullOrWhiteSpace(apfLayerName) ||
                        !req.Args.TryGetValue("features", out string? apfFeaturesJson) ||
                        string.IsNullOrWhiteSpace(apfFeaturesJson))
                        return new(false, "args 'layer' and 'features' required", null);

                    // Parse features JSON outside QueuedTask — no Pro APIs needed
                    // for parsing, and bad JSON should surface as a friendly error
                    // rather than crashing the QueuedTask.
                    JsonArray apfFeaturesArray;
                    try
                    {
                        var node = JsonNode.Parse(apfFeaturesJson)
                            ?? throw new InvalidOperationException("features must be a JSON array (was null)");
                        if (node is not JsonArray arr)
                            return new(false, "features must be a JSON array", null);
                        apfFeaturesArray = arr;
                    }
                    catch (JsonException ex)
                    {
                        return new(false, $"Invalid features JSON: {ex.Message}", null);
                    }

                    var apfAddedOids = new List<long>();
                    string apfActualName = string.Empty;

                    await QueuedTask.Run(async () =>
                    {
                        var map = MapView.Active?.Map
                            ?? throw new InvalidOperationException("No active map");
                        var fl = map.GetLayersAsFlattenedList()
                            .OfType<FeatureLayer>()
                            .FirstOrDefault(l => l.Name.Equals(apfLayerName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException($"Layer not found: {apfLayerName}");
                        apfActualName = fl.Name;

                        if (fl.ShapeType != esriGeometryType.esriGeometryPoint)
                            throw new InvalidOperationException(
                                $"Layer '{fl.Name}' is not a point layer (geometry type: {fl.ShapeType}). Use add_polygon_features for polygons.");

                        using var fc = fl.GetFeatureClass()
                            ?? throw new InvalidOperationException(
                                $"Layer '{fl.Name}' has no resolved feature class — its data source may be missing or unloaded.");
                        var fcDef = fc.GetDefinition();
                        var sr = fcDef.GetSpatialReference();
                        var shapeFieldName = fcDef.GetShapeField();
                        var allFields = fcDef.GetFields();

                        // EditOperation wraps inserts in a proper edit session with
                        // undo/redo support and transactional commit/rollback. If any
                        // single feature fails, the whole operation rolls back.
                        var editOp = new EditOperation
                        {
                            Name = $"Add {apfFeaturesArray.Count} point feature(s) to {fl.Name}",
                            ShowProgressor = false,
                            // Programmatic edits should never show user-facing modal
                            // dialogs — Pro's default true blocks automation flows
                            // on benign post-edit messages. Errors still surface via
                            // editOp.ErrorMessage, which the catch arm below already
                            // captures and propagates to the agent.
                            ShowModalMessageAfterFailure = false
                        };

                        editOp.Callback(context =>
                        {
                            for (int i = 0; i < apfFeaturesArray.Count; i++)
                            {
                                if (apfFeaturesArray[i] is not JsonObject obj)
                                    throw new InvalidOperationException($"feature[{i}] is not a JSON object");

                                if (!obj.TryGetPropertyValue("x", out var xNode) || xNode is null ||
                                    !obj.TryGetPropertyValue("y", out var yNode) || yNode is null)
                                    throw new InvalidOperationException($"feature[{i}] missing required 'x' and/or 'y' coordinates");

                                double x = xNode.GetValue<double>();
                                double y = yNode.GetValue<double>();

                                using var rowBuffer = fc.CreateRowBuffer();
                                rowBuffer[shapeFieldName] = MapPointBuilderEx.CreateMapPoint(x, y, sr);

                                if (obj.TryGetPropertyValue("attributes", out var attrsNode) && attrsNode is JsonObject attrs)
                                {
                                    SetAttributesOnBuffer(rowBuffer, attrs, allFields, i);
                                }

                                using var feature = fc.CreateRow(rowBuffer);
                                apfAddedOids.Add(feature.GetObjectID());
                                context.Invalidate(feature);
                            }
                        }, fc);

                        if (!await editOp.ExecuteAsync())
                            throw new InvalidOperationException($"Edit operation failed: {editOp.ErrorMessage}");
                    });

                    return new(true, null, new
                    {
                        layer = apfActualName,
                        added = apfAddedOids.Count,
                        oids = apfAddedOids
                    });
                }

                case "pro.addPolygonFeatures":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? apgLayerName) ||
                        string.IsNullOrWhiteSpace(apgLayerName) ||
                        !req.Args.TryGetValue("features", out string? apgFeaturesJson) ||
                        string.IsNullOrWhiteSpace(apgFeaturesJson))
                        return new(false, "args 'layer' and 'features' required", null);

                    JsonArray apgFeaturesArray;
                    try
                    {
                        var node = JsonNode.Parse(apgFeaturesJson)
                            ?? throw new InvalidOperationException("features must be a JSON array (was null)");
                        if (node is not JsonArray arr)
                            return new(false, "features must be a JSON array", null);
                        apgFeaturesArray = arr;
                    }
                    catch (JsonException ex)
                    {
                        return new(false, $"Invalid features JSON: {ex.Message}", null);
                    }

                    var apgAddedOids = new List<long>();
                    string apgActualName = string.Empty;

                    await QueuedTask.Run(async () =>
                    {
                        var map = MapView.Active?.Map
                            ?? throw new InvalidOperationException("No active map");
                        var fl = map.GetLayersAsFlattenedList()
                            .OfType<FeatureLayer>()
                            .FirstOrDefault(l => l.Name.Equals(apgLayerName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException($"Layer not found: {apgLayerName}");
                        apgActualName = fl.Name;

                        if (fl.ShapeType != esriGeometryType.esriGeometryPolygon)
                            throw new InvalidOperationException(
                                $"Layer '{fl.Name}' is not a polygon layer (geometry type: {fl.ShapeType}). Use add_point_features for points.");

                        using var fc = fl.GetFeatureClass()
                            ?? throw new InvalidOperationException(
                                $"Layer '{fl.Name}' has no resolved feature class — its data source may be missing or unloaded.");
                        var fcDef = fc.GetDefinition();
                        var sr = fcDef.GetSpatialReference();
                        var shapeFieldName = fcDef.GetShapeField();
                        var allFields = fcDef.GetFields();

                        var editOp = new EditOperation
                        {
                            Name = $"Add {apgFeaturesArray.Count} polygon feature(s) to {fl.Name}",
                            ShowProgressor = false,
                            // Programmatic edits should never show user-facing modal
                            // dialogs — Pro's default true blocks automation flows
                            // on benign post-edit messages. Errors still surface via
                            // editOp.ErrorMessage, which the catch arm below already
                            // captures and propagates to the agent.
                            ShowModalMessageAfterFailure = false
                        };

                        editOp.Callback(context =>
                        {
                            for (int i = 0; i < apgFeaturesArray.Count; i++)
                            {
                                if (apgFeaturesArray[i] is not JsonObject obj)
                                    throw new InvalidOperationException($"feature[{i}] is not a JSON object");

                                if (!obj.TryGetPropertyValue("vertices", out var vertsNode) ||
                                    vertsNode is not JsonArray vertsArr || vertsArr.Count < 3)
                                    throw new InvalidOperationException(
                                        $"feature[{i}] requires 'vertices': a JSON array of at least 3 [x,y] coordinate pairs");

                                // Parse vertex pairs into MapPoints. PolygonBuilderEx
                                // auto-closes the ring if the first/last points differ,
                                // so callers don't need to repeat the first vertex.
                                var points = new List<MapPoint>(vertsArr.Count);
                                for (int v = 0; v < vertsArr.Count; v++)
                                {
                                    if (vertsArr[v] is not JsonArray pair || pair.Count < 2 || pair[0] is null || pair[1] is null)
                                        throw new InvalidOperationException(
                                            $"feature[{i}].vertices[{v}] must be a [x, y] number pair");
                                    points.Add(MapPointBuilderEx.CreateMapPoint(
                                        pair[0]!.GetValue<double>(),
                                        pair[1]!.GetValue<double>(),
                                        sr));
                                }

                                using var rowBuffer = fc.CreateRowBuffer();
                                rowBuffer[shapeFieldName] = PolygonBuilderEx.CreatePolygon(points, sr);

                                if (obj.TryGetPropertyValue("attributes", out var attrsNode) && attrsNode is JsonObject attrs)
                                {
                                    SetAttributesOnBuffer(rowBuffer, attrs, allFields, i);
                                }

                                using var feature = fc.CreateRow(rowBuffer);
                                apgAddedOids.Add(feature.GetObjectID());
                                context.Invalidate(feature);
                            }
                        }, fc);

                        if (!await editOp.ExecuteAsync())
                            throw new InvalidOperationException($"Edit operation failed: {editOp.ErrorMessage}");
                    });

                    return new(true, null, new
                    {
                        layer = apgActualName,
                        added = apgAddedOids.Count,
                        oids = apgAddedOids
                    });
                }

                // ─── GP Tool Discovery ──────────────────────────────────────
                case "pro.describeGpTool":
                    return HandleDescribeGpTool(req.Args);

                case "pro.searchGpTools":
                    return HandleSearchGpTools(req.Args);

                // ─── Python Escape Hatch ────────────────────────────────────
                case "pro.executePython":
                    return await HandleExecutePython(req.Args);

                // ─── View / Camera / Bookmarks ──────────────────────────────
                case "pro.captureMapView":
                    return await HandleCaptureMapView(req.Args);

                case "pro.zoomToExtent":
                    return await HandleZoomToExtent(req.Args);

                case "pro.zoomToScale":
                    return await HandleZoomToScale(req.Args);

                case "pro.zoomToSelected":
                    return await HandleZoomToSelected();

                case "pro.listBookmarks":
                    return await HandleListBookmarks(req.Args);

                case "pro.zoomToBookmark":
                    return await HandleZoomToBookmark(req.Args);

                case "pro.createBookmark":
                    return await HandleCreateBookmark(req.Args);

                // ─── Editing ────────────────────────────────────────────────
                case "pro.updateFeatures":
                    return await HandleUpdateFeatures(req.Args);

                case "pro.deleteFeatures":
                    return await HandleDeleteFeatures(req.Args);

                case "pro.addPolylineFeatures":
                    return await HandleAddPolylineFeatures(req.Args);

                case "pro.saveEdits":
                    return await HandleEditSession("save");

                case "pro.discardEdits":
                    return await HandleEditSession("discard");

                case "pro.hasEdits":
                    return await HandleEditSession("query");

                // ─── Map Administration ─────────────────────────────────────
                case "pro.createMap":
                    return await HandleCreateMap(req.Args);

                case "pro.openMapView":
                    return await HandleOpenMapView(req.Args);

                case "pro.setBasemap":
                    return await HandleSetBasemap(req.Args);

                case "pro.setDefinitionQuery":
                    return await HandleSetDefinitionQuery(req.Args);

                case "pro.setLayerTransparency":
                    return await HandleSetLayerTransparency(req.Args);

                case "pro.setLabeling":
                    return await HandleSetLabeling(req.Args);

                // ─── Layout Furniture ───────────────────────────────────────
                case "pro.addLegend":
                    return await HandleAddLegend(req.Args);

                case "pro.addNorthArrow":
                    return await HandleAddNorthArrow(req.Args);

                case "pro.addScaleBar":
                    return await HandleAddScaleBar(req.Args);

                case "pro.addLayoutText":
                    return await HandleAddLayoutText(req.Args);

                case "pro.setMapFrameExtent":
                    return await HandleSetMapFrameExtent(req.Args);

                // ─── Symbology ──────────────────────────────────────────────
                case "pro.setLayerRenderer":
                    return await HandleSetLayerRenderer(req.Args);

                case "pro.getLayerSymbology":
                    return await HandleGetLayerSymbology(req.Args);

                // ─── Analysis ───────────────────────────────────────────────
                case "pro.getFieldStatistics":
                    return await HandleGetFieldStatistics(req.Args);

                case "pro.selectByLocation":
                    return await HandleSelectByLocation(req.Args);

                // ─── Catalog / Data Discovery ───────────────────────────────
                case "pro.listGdbContents":
                    return await HandleListGdbContents(req.Args);

                case "pro.describeDataset":
                    return await HandleDescribeDataset(req.Args);

                default:
                    return new(false, $"op not found: {req.Op}", null);
            }
        }

        // ─── Map/Layer Handler Methods ───────────────────────────────────────

        /// <summary>
        /// Returns the active view's current extent. For geographic spatial references,
        /// clamps bounds to the SR's valid domain (±180/±90) since <c>MapView.Extent</c>
        /// returns the raw geometric viewport rectangle — which can exceed Earth's bounds
        /// when the camera is zoomed out far enough that the rectangle is bigger than the
        /// planet. <c>clampedToSrValidRange</c> indicates whether clamping fired.
        /// </summary>
        /// <summary>
        /// Resolve a Map by name from the current project, or the active MapView's
        /// map if mapName is null/whitespace. Throws InvalidOperationException with
        /// a clear message if the named map doesn't exist or no map is active.
        /// Wraps the Project.Current/MapView.Active access points so every handler
        /// can accept an optional 'map' parameter without duplicating boilerplate.
        /// Must be called from within a QueuedTask (Project.Current and MapProjectItem.GetMap
        /// have thread-affinity requirements).
        /// </summary>
        private static ArcGIS.Desktop.Mapping.Map ResolveMap(string? mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
            {
                return MapView.Active?.Map
                    ?? throw new InvalidOperationException(
                        "No active map and no 'map' parameter provided. Open a map view in Pro or specify 'map' explicitly.");
            }
            var project = Project.Current
                ?? throw new InvalidOperationException("No project currently open in ArcGIS Pro");
            var available = project.GetItems<MapProjectItem>().ToList();
            var mapItem = available
                .FirstOrDefault(m => m.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Map not found: {mapName}. Available maps: {string.Join(", ", available.Select(m => m.Name))}");
            return mapItem.GetMap();
        }

        /// <summary>
        /// Find a MapMember (Layer or StandaloneTable) by name in a Map. Searches
        /// flattened layer tree (descending into group layers) AND standalone
        /// tables. Returns null if not found. Case-insensitive name match.
        /// First match wins — for duplicate names across groups, layer order in
        /// the TOC determines priority.
        /// </summary>
        /// <summary>
        /// Like <see cref="FindMapMemberByName"/> but throws with the full list of
        /// available layer/table names on a miss — an agent that typo'd a name can
        /// self-correct from the error instead of needing a list_layers round trip.
        /// </summary>
        private static ArcGIS.Desktop.Mapping.MapMember RequireMapMember(
            ArcGIS.Desktop.Mapping.Map map, string name)
        {
            var member = FindMapMemberByName(map, name);
            if (member != null) return member;
            var available = map.GetLayersAsFlattenedList().Select(l => l.Name)
                .Concat(map.StandaloneTables.Select(t => t.Name))
                .ToList();
            throw new InvalidOperationException(
                $"Layer or table not found: {name}. Available in map '{map.Name}': " +
                (available.Count > 0 ? string.Join(", ", available) : "<none>"));
        }

        private static ArcGIS.Desktop.Mapping.MapMember? FindMapMemberByName(
            ArcGIS.Desktop.Mapping.Map map, string name)
        {
            foreach (var layer in map.GetLayersAsFlattenedList())
            {
                if (layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return layer;
            }
            foreach (var table in map.StandaloneTables)
            {
                if (table.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return table;
            }
            return null;
        }

        /// <summary>
        /// Resolve the underlying ArcGIS.Core.Data.Table for a MapMember that has
        /// row-providing data. Returns FeatureClass for FeatureLayers (FeatureClass
        /// inherits from Table) and Table for StandaloneTables. Returns null for
        /// member types without an attribute table (GroupLayer, RasterLayer, etc.).
        /// </summary>
        private static ArcGIS.Core.Data.Table? GetTableFromMember(
            ArcGIS.Desktop.Mapping.MapMember member)
        {
            return member switch
            {
                FeatureLayer fl => fl.GetFeatureClass(),
                ArcGIS.Desktop.Mapping.StandaloneTable st => st.GetTable(),
                _ => null
            };
        }

        /// <summary>
        /// Shared attribute-setter for the add_*_features handlers. Walks each
        /// key in the supplied JSON object, looks up the matching field (case-
        /// insensitive) in the feature class definition, coerces the JSON value
        /// to the field's type, and writes it to the row buffer. Throws with a
        /// feature-index-tagged message on unknown fields, non-settable fields
        /// (geometry/OID/blob/raster), or type-incompatible values.
        /// </summary>
        private static void SetAttributesOnBuffer(
            ArcGIS.Core.Data.RowBuffer rowBuffer,
            JsonObject attrs,
            IReadOnlyList<ArcGIS.Core.Data.Field> allFields,
            int featureIndex)
        {
            foreach (var kvp in attrs)
            {
                var fieldName = kvp.Key;
                var valueNode = kvp.Value;

                var field = allFields.FirstOrDefault(f =>
                    f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    throw new InvalidOperationException(
                        $"feature[{featureIndex}].attributes references field '{fieldName}' which does not exist in the layer");

                // Block fields whose values are managed by the row's geometry or
                // identity rather than by the caller's attribute payload.
                if (field.FieldType == ArcGIS.Core.Data.FieldType.OID ||
                    field.FieldType == ArcGIS.Core.Data.FieldType.Geometry ||
                    field.FieldType == ArcGIS.Core.Data.FieldType.Blob ||
                    field.FieldType == ArcGIS.Core.Data.FieldType.Raster)
                    throw new InvalidOperationException(
                        $"feature[{featureIndex}].attributes cannot set field '{field.Name}' (type {field.FieldType}) — it's managed by the row's identity or geometry");

                if (valueNode == null)
                {
                    rowBuffer[field.Name] = null;
                    continue;
                }

                try
                {
                    rowBuffer[field.Name] = field.FieldType switch
                    {
                        ArcGIS.Core.Data.FieldType.String => valueNode.GetValue<string>(),
                        ArcGIS.Core.Data.FieldType.Integer => valueNode.GetValue<int>(),
                        ArcGIS.Core.Data.FieldType.SmallInteger => (short)valueNode.GetValue<int>(),
                        ArcGIS.Core.Data.FieldType.Single => valueNode.GetValue<float>(),
                        ArcGIS.Core.Data.FieldType.Double => valueNode.GetValue<double>(),
                        ArcGIS.Core.Data.FieldType.Date => DateTime.Parse(
                            valueNode.GetValue<string>(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind),
                        ArcGIS.Core.Data.FieldType.GUID or ArcGIS.Core.Data.FieldType.GlobalID =>
                            Guid.Parse(valueNode.GetValue<string>()),
                        _ => throw new InvalidOperationException(
                            $"feature[{featureIndex}].attributes field '{field.Name}' has unsupported type {field.FieldType}")
                    };
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or InvalidCastException)
                {
                    if (ex is InvalidOperationException) throw;
                    throw new InvalidOperationException(
                        $"feature[{featureIndex}].attributes field '{field.Name}' (type {field.FieldType}) could not coerce value: {ex.Message}");
                }
            }
        }

        private static async Task<IpcResponse> HandleGetCurrentExtent()
        {
            var extent = await QueuedTask.Run<object?>(() =>
            {
                var view = MapView.Active;
                var ext = view?.Extent;
                if (ext == null) return null;

                // MapView.Extent is the raw geometric viewport rectangle centered on the
                // camera at the current scale. When zoomed out far enough for the rectangle
                // to exceed the Earth's valid geographic bounds (±180°, ±90° for WGS84),
                // the reported bounds run past physical limits — e.g., a continent-scale
                // view centered on Portland (x=-122.6, y=45.2) produces ymax > 120° because
                // the rectangle's half-height exceeds 45° of latitude. Pro doesn't clamp.
                // Clamp here for geographic SRs so agents get valid lat/lon. Projected SRs
                // pass through (no universal valid-domain for arbitrary projections).
                double xmin = ext.XMin, ymin = ext.YMin, xmax = ext.XMax, ymax = ext.YMax;
                var sr = ext.SpatialReference;
                bool clamped = false;
                if (sr != null && sr.IsGeographic &&
                    (xmin < -180.0 || ymin < -90.0 || xmax > 180.0 || ymax > 90.0))
                {
                    double cxmin = Math.Max(-180.0, xmin);
                    double cymin = Math.Max(-90.0, ymin);
                    double cxmax = Math.Min(180.0, xmax);
                    double cymax = Math.Min(90.0, ymax);
                    if (cxmax > cxmin && cymax > cymin)
                    {
                        // Only apply clamp if result is non-degenerate; otherwise the
                        // viewport is entirely off-Earth and raw values are more honest.
                        xmin = cxmin; ymin = cymin; xmax = cxmax; ymax = cymax;
                        clamped = true;
                    }
                }

                return new
                {
                    xmin,
                    ymin,
                    xmax,
                    ymax,
                    width = xmax - xmin,
                    height = ymax - ymin,
                    spatialReferenceWkid = sr?.Wkid ?? 0,
                    spatialReferenceName = sr?.Name,
                    clampedToSrValidRange = clamped
                };
            });

            if (extent == null)
                return new(false, "No active map view", null);
            return new(true, null, extent);
        }

        /// <summary>
        /// Exposes raw view + map + camera state for diagnosing extent/projection issues.
        /// Useful when get_current_extent returns values that don't match the reported SR.
        /// </summary>
        private static async Task<IpcResponse> HandleGetViewDiagnostics()
        {
            var diag = await QueuedTask.Run<object?>(() =>
            {
                var view = MapView.Active;
                if (view == null) return null;

                var map = view.Map;
                var ext = view.Extent;
                var camera = view.Camera;

                Envelope? mapFullExtent = null;
                try { mapFullExtent = map?.CalculateFullExtent(); } catch { /* some maps don't support */ }

                return new
                {
                    viewingMode = view.ViewingMode.ToString(),
                    map = map == null ? null : (object)new
                    {
                        name = map.Name,
                        srWkid = map.SpatialReference?.Wkid ?? 0,
                        srName = map.SpatialReference?.Name,
                        srIsProjected = map.SpatialReference?.IsProjected ?? false,
                    },
                    extent = ext == null ? null : (object)new
                    {
                        xmin = ext.XMin,
                        ymin = ext.YMin,
                        xmax = ext.XMax,
                        ymax = ext.YMax,
                        width = ext.Width,
                        height = ext.Height,
                        srWkid = ext.SpatialReference?.Wkid ?? 0,
                        srName = ext.SpatialReference?.Name,
                        srIsProjected = ext.SpatialReference?.IsProjected ?? false,
                    },
                    camera = camera == null ? null : (object)new
                    {
                        x = camera.X,
                        y = camera.Y,
                        z = camera.Z,
                        scale = camera.Scale,
                        heading = camera.Heading,
                        pitch = camera.Pitch,
                        roll = camera.Roll,
                    },
                    mapFullExtent = mapFullExtent == null ? null : (object)new
                    {
                        xmin = mapFullExtent.XMin,
                        ymin = mapFullExtent.YMin,
                        xmax = mapFullExtent.XMax,
                        ymax = mapFullExtent.YMax,
                        srWkid = mapFullExtent.SpatialReference?.Wkid ?? 0,
                        srName = mapFullExtent.SpatialReference?.Name,
                    }
                };
            });

            if (diag == null) return new(false, "No active map view", null);
            return new(true, null, diag);
        }

        /// <summary>
        /// Clears feature selections. With no layer arg, clears selection on every
        /// feature layer in the active map. With layer arg, clears just that one
        /// (throws if the layer isn't found — F4 pattern for silent-failure avoidance).
        /// Leftover selections from prior operations silently restrict subsequent GP
        /// tool inputs when those tools accept layer names, which is a common source
        /// of agent confusion; a first-class clear tool makes the pre-op reset explicit.
        /// </summary>
        private static async Task<IpcResponse> HandleClearSelection(Dictionary<string, string>? args)
        {
            string? layerName = null;
            string? mapName = null;
            args?.TryGetValue("layer", out layerName);
            args?.TryGetValue("map", out mapName);

            var result = await QueuedTask.Run<(bool ok, string? error, int cleared, string? layerCleared)>(() =>
            {
                ArcGIS.Desktop.Mapping.Map map;
                try { map = ResolveMap(mapName); }
                catch (InvalidOperationException ex) { return (false, ex.Message, 0, null); }

                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    // Single-target mode: clear selection on the named feature layer
                    // OR standalone table. Both expose ClearSelection() on their
                    // subclasses (not on MapMember), so dispatch.
                    var member = RequireMapMember(map, layerName);
                    switch (member)
                    {
                        case FeatureLayer flCs: flCs.ClearSelection(); break;
                        case ArcGIS.Desktop.Mapping.StandaloneTable stCs: stCs.ClearSelection(); break;
                        default:
                            throw new InvalidOperationException(
                                $"'{member.Name}' is a {member.GetType().Name} which doesn't support selection.");
                    }
                    return (true, null, 1, member.Name);
                }

                // All-targets mode: clear every feature layer AND every standalone
                // table. Both contribute selection state that would silently restrict
                // downstream GP tool inputs; clearing both makes the reset uniform.
                int clearedCount = 0;
                foreach (var fl in map.GetLayersAsFlattenedList().OfType<FeatureLayer>())
                {
                    fl.ClearSelection();
                    clearedCount++;
                }
                foreach (var st in map.StandaloneTables)
                {
                    st.ClearSelection();
                    clearedCount++;
                }
                return (true, null, clearedCount, null);
            });

            if (!result.ok) return new(false, result.error, null);
            return new(true, null, new { cleared = result.cleared, layer = result.layerCleared });
        }

        /// <summary>
        /// Returns project-level metadata — name, aprx path, home folder, default
        /// geodatabase + toolbox paths, map count, active map name and SR. Agents
        /// use this for orientation before operations that depend on project context
        /// (e.g., "am I in the right project? what's the map's SR?").
        /// </summary>
        private static async Task<IpcResponse> HandleGetProjectInfo()
        {
            var info = await QueuedTask.Run<object?>(() =>
            {
                var proj = Project.Current;
                if (proj == null) return null;

                var view = MapView.Active;
                object? activeMap = null;
                if (view?.Map != null)
                {
                    activeMap = new
                    {
                        name = view.Map.Name,
                        srWkid = view.Map.SpatialReference?.Wkid ?? 0,
                        srName = view.Map.SpatialReference?.Name
                    };
                }

                return new
                {
                    name = proj.Name,
                    aprxPath = proj.Path,
                    homeFolder = proj.HomeFolderPath,
                    defaultGeodatabase = proj.DefaultGeodatabasePath,
                    defaultToolbox = proj.DefaultToolboxPath,
                    mapCount = proj.GetItems<MapProjectItem>().Count(),
                    layoutCount = proj.GetItems<LayoutProjectItem>().Count(),
                    toolboxCount = proj.GetItems<GeoprocessingProjectItem>().Count(),
                    activeMap
                };
            });

            if (info == null)
                return new(false, "No project currently open", null);
            return new(true, null, info);
        }

        /// <summary>
        /// Lists all maps in the current project (name + item path). Complements
        /// get_active_map_name which returns only the currently-active map — this
        /// enumerates every map so agents can pick one by name before operations
        /// that take a map name (e.g., add_map_frame_to_layout).
        /// </summary>
        private static async Task<IpcResponse> HandleListMaps()
        {
            var maps = await QueuedTask.Run(() =>
                Project.Current?.GetItems<MapProjectItem>()
                    .Select(i => new Dictionary<string, string>
                    {
                        ["name"] = i.Name,
                        ["path"] = i.Path ?? ""
                    }).ToList()
                ?? new List<Dictionary<string, string>>());
            return new(true, null, maps);
        }

        /// <summary>
        /// Explicitly saves the current project. Most project-lifecycle ops already
        /// save-first to avoid modal "save changes?" dialogs, but an explicit save
        /// is useful as a pre-operation safety rail or after a batch of edits the
        /// agent wants to persist.
        /// </summary>
        private static async Task<IpcResponse> HandleSaveProject()
        {
            if (Project.Current == null)
                return new(false, "No project currently open", null);
            try
            {
                // Project.SaveAsync (like CreateAsync/OpenAsync — F1/F2) is GUI-thread-
                // only. Calling it from the IPC thread raises "calling thread cannot
                // access this object". Dispatch to the WPF UI thread and unwrap the
                // nested Task the same way HandleCreateProject does.
                var saveTask = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => Project.Current.SaveAsync());
                await saveTask;

                return new(true, null, new
                {
                    saved = true,
                    path = Project.Current.URI,
                    name = Project.Current.Name
                });
            }
            catch (Exception ex)
            {
                return new(false, $"Save failed: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Exports a layer (by name) to an output feature class/shapefile.
        /// Uses the conversion.ExportFeatures GP tool so an optional SQL
        /// WHERE clause can filter the output. Output path determines format
        /// (.shp → shapefile, otherwise treated as a geodatabase feature class).
        /// </summary>
        private static async Task<IpcResponse> HandleExportLayer(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("output", out string? output) ||
                string.IsNullOrWhiteSpace(output))
                return new(false, "args 'layer' & 'output' required", null);

            args.TryGetValue("where", out string? where);

            // Resolve the layer so we return a clear error before invoking GP.
            var resolved = await QueuedTask.Run(() =>
                MapView.Active?.Map?.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                    ?.Name);
            if (resolved == null)
                return new(false, $"Layer not found in active map: {layerName}", null);

            var valueArray = string.IsNullOrWhiteSpace(where)
                ? Geoprocessing.MakeValueArray(resolved, output)
                : Geoprocessing.MakeValueArray(resolved, output, where);

            var result = await Geoprocessing.ExecuteToolAsync(
                "conversion.ExportFeatures", valueArray, DefaultRunEnvironments());

            if (result.IsFailed)
            {
                var messages = string.Join("; ", result.Messages.Select(m => m.Text));
                return new(false, $"Export failed: {messages}", null);
            }

            return new(true, null, new
            {
                layer = resolved,
                output,
                where,
                success = true
            });
        }

        // ─── Project Handler Methods ─────────────────────────────────────────

        /// <summary>
        /// Creates a new ArcGIS Pro project. Saves the current project first
        /// so Pro doesn't raise a modal "save changes?" dialog that would
        /// hang the bridge (the IPC handler is blocked while the dialog
        /// awaits user interaction, causing the caller to see a timeout).
        /// </summary>
        private static async Task<IpcResponse> HandleCreateProject(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name) ||
                !args.TryGetValue("location", out string? location) ||
                string.IsNullOrWhiteSpace(location))
                return new(false, "args 'name' & 'location' required", null);

            args.TryGetValue("template", out string? template);
            bool overwrite = args.TryGetValue("overwrite", out string? ow)
                             && bool.TryParse(ow, out var b) && b;

            // Same GUI-thread requirement as the explicit pro.saveProject path.
            // Without the Dispatcher wrap this silently throws and the catch
            // swallows it, meaning save-first never actually fired and Pro's
            // modal "save changes?" dialog could appear during the project
            // switch below. See F1/F2 commit history.
            try
            {
                if (Project.Current != null)
                {
                    var saveTask = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => Project.Current.SaveAsync());
                    await saveTask;
                }
            }
            catch { }

            if (overwrite)
            {
                var outDir = Path.Combine(location, name);
                if (Directory.Exists(outDir))
                {
                    // Safety: only delete a directory that actually looks like the
                    // project we'd be replacing (contains <name>.aprx). Without
                    // this check, overwrite=true pointed at the wrong location
                    // recursively deletes an arbitrary folder.
                    var aprx = Path.Combine(outDir, $"{name}.aprx");
                    if (!File.Exists(aprx))
                        return new(false,
                            $"Refusing to overwrite '{outDir}' — it does not contain {name}.aprx, " +
                            "so it doesn't look like the project being replaced. Delete it manually " +
                            "if that's really what you want.", null);
                    try { Directory.Delete(outDir, recursive: true); }
                    catch (Exception ex)
                    {
                        return new(false,
                            $"Cannot overwrite — failed to remove '{outDir}': {ex.Message}", null);
                    }
                }
            }

            var settings = new CreateProjectSettings
            {
                Name = name,
                LocationPath = location
            };
            if (!string.IsNullOrWhiteSpace(template))
                settings.TemplatePath = template;

            var projectTask = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => Project.CreateAsync(settings));
            var project = await projectTask;
            if (project == null)
                return new(false, "Project.CreateAsync returned null", null);

            return new(true, null, new
            {
                name = project.Name,
                path = project.URI,
                homeFolder = project.HomeFolderPath
            });
        }

        /// <summary>
        /// Opens an existing .aprx. Same WPF-Dispatcher + nested-Task-unwrap pattern as
        /// <see cref="HandleCreateProject"/> — <c>Project.OpenAsync</c> requires the GUI
        /// thread, not the MCT, so QueuedTask.Run alone is insufficient. Saves the
        /// current project first to suppress the modal "save changes?" dialog.
        /// </summary>
        private static async Task<IpcResponse> HandleOpenProject(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("path", out string? path) ||
                string.IsNullOrWhiteSpace(path))
                return new(false, "arg 'path' required", null);

            if (!File.Exists(path))
                return new(false, $"Project file not found: {path}", null);

            // Same GUI-thread requirement as the explicit pro.saveProject path.
            // Without the Dispatcher wrap this silently throws and the catch
            // swallows it, meaning save-first never actually fired and Pro's
            // modal "save changes?" dialog could appear during the project
            // switch below. See F1/F2 commit history.
            try
            {
                if (Project.Current != null)
                {
                    var saveTask = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => Project.Current.SaveAsync());
                    await saveTask;
                }
            }
            catch { }

            var projectTask = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => Project.OpenAsync(path));
            var project = await projectTask;
            if (project == null)
                return new(false, $"Failed to open project: {path}", null);

            return new(true, null, new
            {
                name = project.Name,
                path = project.URI,
                homeFolder = project.HomeFolderPath
            });
        }

        // ─── Layer Handler Methods ───────────────────────────────────────────

        /// <summary>
        /// Adds a layer to the active map from a URL. Supports feature services,
        /// image services, tile services, WMS, and any other URI source that
        /// LayerFactory understands.
        /// </summary>
        private static async Task<IpcResponse> HandleAddLayerFromUrl(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("url", out string? url) ||
                string.IsNullOrWhiteSpace(url))
                return new(false, "arg 'url' required", null);

            args.TryGetValue("name", out string? layerName);
            args.TryGetValue("map", out string? mapName);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                ArcGIS.Desktop.Mapping.Map map;
                try { map = ResolveMap(mapName); }
                catch (InvalidOperationException ex) { return new(false, ex.Message, null); }

                Uri uri;
                try { uri = new Uri(url); }
                catch (Exception ex) { return new(false, $"Invalid URL: {ex.Message}", null); }

                var layer = LayerFactory.Instance.CreateLayer(uri, map);
                if (layer == null)
                    return new(false, "CreateLayer returned null (service unreachable or unsupported)", null);

                if (!string.IsNullOrWhiteSpace(layerName))
                    layer.SetName(layerName);

                return new(true, null, new
                {
                    name = layer.Name,
                    url = url,
                    layerType = layer.GetType().Name
                });
            });
        }

        /// <summary>
        /// Adds a layer to the active map from a file-system path. Supports shapefiles
        /// (.shp), file geodatabase feature classes (path/to.gdb/FeatureClass), rasters,
        /// and any other path LayerFactory can resolve. For .gdb feature classes the
        /// path is a composite (folder.gdb + feature-class-name), which the Uri class
        /// and Pro SDK handle natively.
        /// </summary>
        private static async Task<IpcResponse> HandleAddLayerFromFile(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("path", out string? path) ||
                string.IsNullOrWhiteSpace(path))
                return new(false, "arg 'path' required", null);

            args.TryGetValue("name", out string? layerName);
            args.TryGetValue("map", out string? mapName);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                ArcGIS.Desktop.Mapping.Map map;
                try { map = ResolveMap(mapName); }
                catch (InvalidOperationException ex) { return new(false, ex.Message, null); }

                Uri uri;
                try { uri = new Uri(path); }
                catch (Exception ex) { return new(false, $"Invalid path (cannot build URI): {ex.Message}", null); }

                try
                {
                    var layer = LayerFactory.Instance.CreateLayer(uri, map);
                    if (layer == null)
                        return new(false,
                            $"CreateLayer returned null for '{path}' — source not found, unsupported format, or inaccessible. " +
                            "For geodatabase feature classes, use path like 'C:/data/my.gdb/FeatureClassName'.",
                            null);

                    if (!string.IsNullOrWhiteSpace(layerName))
                        layer.SetName(layerName);

                    return new(true, null, new
                    {
                        name = layer.Name,
                        path,
                        layerType = layer.GetType().Name
                    });
                }
                catch (Exception ex)
                {
                    return new(false, $"Failed to add layer from '{path}': {ex.Message}", null);
                }
            });
        }

        // ─── Layout Handler Methods ──────────────────────────────────────────

        private static async Task<IpcResponse> HandleListLayouts()
        {
            var layouts = await QueuedTask.Run(() =>
                Project.Current?.GetItems<LayoutProjectItem>()
                    .Select(i => new Dictionary<string, string>
                    {
                        ["name"] = i.Name,
                        ["path"] = i.Path ?? ""
                    }).ToList()
                ?? new List<Dictionary<string, string>>());
            return new(true, null, layouts);
        }

        /// <summary>
        /// Creates a new blank layout with the given page size. Defaults to letter
        /// landscape (11×8.5 in). Orientation (portrait/landscape) rotates the page
        /// dims automatically if width/height disagree with the requested orientation.
        /// The layout is empty — use add_map_frame_to_layout to attach a map and
        /// set_layout_text to fill any text elements you add later.
        /// </summary>
        private static async Task<IpcResponse> HandleCreateLayout(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name))
                return new(false, "arg 'name' required", null);

            // InvariantCulture so "11.5" parses correctly on locales where the decimal
            // separator is ',' rather than '.'. Default TryParse silently fails to parse
            // and falls through to the default — wrong dimensions, no error visible.
            double width = 11.0, height = 8.5;
            if (args.TryGetValue("widthInches", out string? ws) && double.TryParse(ws, NumberStyles.Float, CultureInfo.InvariantCulture, out var wd) && wd > 0)
                width = wd;
            if (args.TryGetValue("heightInches", out string? hs) && double.TryParse(hs, NumberStyles.Float, CultureInfo.InvariantCulture, out var hd) && hd > 0)
                height = hd;

            string orientation = "landscape";
            if (args.TryGetValue("orientation", out string? o) && !string.IsNullOrWhiteSpace(o))
                orientation = o.ToLowerInvariant();

            // Coerce dims to match requested orientation so callers who pass
            // "portrait" with 11×8.5 still get a portrait layout.
            if (orientation == "portrait" && width > height) (width, height) = (height, width);
            else if (orientation == "landscape" && height > width) (width, height) = (height, width);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                try
                {
                    var layout = LayoutFactory.Instance.CreateLayout(width, height, LinearUnit.Inches);
                    if (layout == null)
                        return new(false, "LayoutFactory.CreateLayout returned null", null);
                    layout.SetName(name);
                    return new(true, null, new
                    {
                        name = layout.Name,
                        widthInches = width,
                        heightInches = height,
                        orientation
                    });
                }
                catch (Exception ex)
                {
                    return new(false, $"Failed to create layout: {ex.Message}", null);
                }
            });
        }

        /// <summary>
        /// Creates a map-frame element on an existing layout and binds it to a map.
        /// Default frame position/size is 1" from top-left, 9"×6.5" — fits inside a
        /// letter-landscape with 1" margins. Override via xInches/yInches/widthInches
        /// /heightInches. This is the crucial step that turns an empty create_layout
        /// output into a usable layout: without a map frame the layout renders blank.
        /// </summary>
        private static async Task<IpcResponse> HandleAddMapFrameToLayout(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layoutName", out string? layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !args.TryGetValue("mapName", out string? mapName) || string.IsNullOrWhiteSpace(mapName))
                return new(false, "args 'layoutName' & 'mapName' required", null);

            // InvariantCulture — see HandleCreateLayout for rationale.
            double x = 1.0, y = 1.0, w = 9.0, h = 6.5;
            if (args.TryGetValue("xInches", out string? xs) && double.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out var xd)) x = xd;
            if (args.TryGetValue("yInches", out string? ys) && double.TryParse(ys, NumberStyles.Float, CultureInfo.InvariantCulture, out var yd)) y = yd;
            if (args.TryGetValue("widthInches", out string? ws) && double.TryParse(ws, NumberStyles.Float, CultureInfo.InvariantCulture, out var wd) && wd > 0) w = wd;
            if (args.TryGetValue("heightInches", out string? hs) && double.TryParse(hs, NumberStyles.Float, CultureInfo.InvariantCulture, out var hd) && hd > 0) h = hd;

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var layoutItem = Project.Current?.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(i => i.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) return new(false, $"Layout not found: {layoutName}", null);
                var layout = layoutItem.GetLayout();
                if (layout == null) return new(false, $"Could not load layout: {layoutName}", null);

                var mapItem = Project.Current?.GetItems<MapProjectItem>()
                    .FirstOrDefault(i => i.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase));
                if (mapItem == null) return new(false, $"Map not found: {mapName}", null);
                var map = mapItem.GetMap();
                if (map == null) return new(false, $"Could not load map: {mapName}", null);

                try
                {
                    // The MCP tool description tells agents that x/y are measured from
                    // the page TOP-LEFT (the screen-coords convention everyone learns
                    // from web/UI work). Pro SDK layout coords are bottom-up — y=0 is
                    // the page bottom, increasing toward the top. Invert here so the
                    // documented convention matches the actual placement; otherwise
                    // any agent passing y>0 expecting "near the top" silently gets a
                    // frame near the bottom.
                    double pageHeight = layout.GetPage().Height;
                    double sdkYmin = pageHeight - y - h;  // bottom edge in SDK coords
                    double sdkYmax = pageHeight - y;       // top edge in SDK coords

                    var envelope = EnvelopeBuilderEx.CreateEnvelope(x, sdkYmin, x + w, sdkYmax);
                    var mapFrame = ElementFactory.Instance.CreateMapFrameElement(layout, envelope, map);
                    if (mapFrame == null)
                        return new(false, "CreateMapFrameElement returned null", null);

                    return new(true, null, new
                    {
                        layoutName,
                        mapName,
                        mapFrameName = mapFrame.Name,
                        xInches = x,
                        yInches = y,
                        widthInches = w,
                        heightInches = h
                    });
                }
                catch (Exception ex)
                {
                    return new(false, $"Failed to add map frame: {ex.Message}", null);
                }
            });
        }

        /// <summary>
        /// Opens a layout in a new pane. Layout-item lookup runs on the MCT via
        /// QueuedTask.Run, but pane creation (<c>FrameworkApplication.Panes.Create
        /// LayoutPaneAsync</c>) is GUI-thread-only — invoked via the WPF Dispatcher.
        /// Mixing the two thread contexts in one method is the F3 fix pattern.
        /// </summary>
        private static async Task<IpcResponse> HandleOpenLayout(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name))
                return new(false, "arg 'name' required", null);

            var getResult = await QueuedTask.Run(() =>
            {
                var item = Project.Current?.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (item == null) return (ok: false, err: $"Layout not found: {name}", layout: (Layout?)null);
                var layout = item.GetLayout();
                if (layout == null) return (ok: false, err: $"Could not load layout: {name}", layout: (Layout?)null);
                return (ok: true, err: (string?)null, layout: layout);
            });

            if (!getResult.ok) return new(false, getResult.err, null);
            if (getResult.layout == null) return new(false, "Layout is null", null);

            try
            {
                var app = System.Windows.Application.Current;
                var dispatcher = app?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    await dispatcher.InvokeAsync(() =>
                        FrameworkApplication.Panes.CreateLayoutPaneAsync(getResult.layout!));
                else
                    await FrameworkApplication.Panes.CreateLayoutPaneAsync(getResult.layout!);
            }
            catch
            {
                await FrameworkApplication.Panes.CreateLayoutPaneAsync(getResult.layout!);
            }

            return new(true, null, new { name, opened = true });
        }

        /// <summary>
        /// Enumerates every Element on a layout — titles, scale bars, legends,
        /// north arrows, map frames, etc. Each entry includes a short preview
        /// of its text (for TextElements) so the caller can identify which
        /// element to edit without a visual round-trip.
        /// </summary>
        private static async Task<IpcResponse> HandleListLayoutElements(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name))
                return new(false, "arg 'name' required", null);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var item = Project.Current?.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (item == null) return new(false, $"Layout not found: {name}", null);
                var layout = item.GetLayout();
                if (layout == null) return new(false, $"Could not load layout: {name}", null);

                var elements = layout.GetElements().Select(e =>
                {
                    string? textPreview = null;
                    if (e is TextElement te)
                    {
                        textPreview = te.TextProperties?.Text;
                        if (textPreview != null && textPreview.Length > 80)
                            textPreview = textPreview[..80] + "…";
                    }
                    return new
                    {
                        name = e.Name,
                        type = e.GetType().Name,
                        visible = e.IsVisible,
                        text = textPreview
                    };
                }).ToList();

                return new(true, null, elements);
            });
        }

        private static async Task<IpcResponse> HandleSetLayoutText(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layoutName", out string? layoutName) ||
                string.IsNullOrWhiteSpace(layoutName) ||
                !args.TryGetValue("elementName", out string? elementName) ||
                string.IsNullOrWhiteSpace(elementName) ||
                !args.TryGetValue("text", out string? text))
                return new(false, "args 'layoutName', 'elementName' & 'text' required", null);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var item = Project.Current?.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(i => i.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (item == null) return new(false, $"Layout not found: {layoutName}", null);
                var layout = item.GetLayout();
                if (layout == null) return new(false, $"Could not load layout: {layoutName}", null);

                var element = layout.GetElements()
                    .FirstOrDefault(e => e.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase));
                if (element == null)
                    return new(false, $"Element not found on layout '{layoutName}': {elementName}", null);
                if (element is not TextElement te)
                    return new(false, $"Element '{elementName}' is {element.GetType().Name}, not a TextElement", null);

                // Preserve the element's existing font / size / style; only change the text.
                // TextProperties requires (text, font, size, fontStyle) — no single-arg ctor.
                var tp = te.TextProperties;
                var newTp = new TextProperties(text ?? "", tp.Font, tp.FontSize, tp.FontStyle);
                te.SetTextProperties(newTp);
                return new(true, null, new { layoutName, elementName, text });
            });
        }

        /// <summary>
        /// Exports a layout to PDF (default), PNG, JPG, TIFF, or SVG. The
        /// output file extension selects the format unless 'format' is
        /// explicit. Raster formats default to 300 DPI.
        /// </summary>
        private static async Task<IpcResponse> HandleExportLayout(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name) ||
                !args.TryGetValue("output", out string? output) ||
                string.IsNullOrWhiteSpace(output))
                return new(false, "args 'name' & 'output' required", null);

            args.TryGetValue("format", out string? format);
            int resolution = 300;
            if (args.TryGetValue("resolution", out string? res) &&
                int.TryParse(res, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r > 0)
                resolution = r;

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var item = Project.Current?.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (item == null) return new(false, $"Layout not found: {name}", null);
                var layout = item.GetLayout();
                if (layout == null) return new(false, $"Could not load layout: {name}", null);

                var fmt = !string.IsNullOrWhiteSpace(format)
                    ? format.ToLowerInvariant()
                    : Path.GetExtension(output).TrimStart('.').ToLowerInvariant();

                ExportFormat ef = fmt switch
                {
                    "png"           => new PNGFormat  { OutputFileName = output, Resolution = resolution },
                    "jpg" or "jpeg" => new JPEGFormat { OutputFileName = output, Resolution = resolution },
                    "tif" or "tiff" => new TIFFFormat { OutputFileName = output, Resolution = resolution },
                    "svg"           => new SVGFormat  { OutputFileName = output },
                    _               => new PDFFormat  { OutputFileName = output, Resolution = resolution } // default
                };

                if (!ef.ValidateOutputFilePath())
                    return new(false, $"Invalid output path: {output}", null);

                try
                {
                    layout.Export(ef);
                }
                catch (Exception ex)
                {
                    return new(false, $"Export failed: {ex.Message}", null);
                }

                if (!File.Exists(output))
                    return new(false,
                        $"Export returned no error but file was not written: {output} — likely a permission or path issue.",
                        null);

                return new(true, null, new
                {
                    layout = name,
                    output = Path.GetFullPath(output),
                    format = ef.GetType().Name,
                    resolution = fmt == "svg" ? (int?)null : resolution
                });
            });
        }

        // ─── ModelBuilder Handler Methods ────────────────────────────────────

        private static async Task<IpcResponse> HandleListToolboxes()
        {
            if (Project.Current == null)
                return new(false, "No project currently open in ArcGIS Pro", null);

            var toolboxes = await QueuedTask.Run(() =>
            {
                var items = Project.Current?.GetItems<GeoprocessingProjectItem>()
                    ?? Enumerable.Empty<GeoprocessingProjectItem>();
                return items.Select(item => new Dictionary<string, string>
                {
                    ["name"] = item.Name,
                    ["path"] = item.Path
                }).ToList();
            });

            return new(true, null, toolboxes);
        }

        private static IpcResponse HandleListModels(Dictionary<string, string>? args)
        {
            if (args == null || !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path))
                return new(false, "arg 'toolboxPath' required", null);

            if (!File.Exists(path))
                return new(false, $"Toolbox not found: {path}", null);

            var models = AtbxManager.ListModels(path);
            return new(true, null, models);
        }

        private static IpcResponse HandleDescribeModel(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName))
                return new(false, "args 'toolboxPath' & 'modelName' required", null);

            if (!File.Exists(path))
                return new(false, $"Toolbox not found: {path}", null);

            var description = AtbxManager.DescribeModel(path, modelName);
            // Return as a raw JSON string that gets parsed on the other side
            return new(true, null, JsonNode.Parse(description));
        }

        private static async Task<IpcResponse> HandleCreateToolbox(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? tbxName) ||
                string.IsNullOrWhiteSpace(tbxName))
                return new(false, "arg 'name' required", null);

            // Default path: project home folder
            string path;
            if (args.TryGetValue("path", out string? customPath) && !string.IsNullOrWhiteSpace(customPath))
            {
                path = customPath;
            }
            else
            {
                var projectHome = await QueuedTask.Run(() => Project.Current.HomeFolderPath);
                path = Path.Combine(projectHome, $"{tbxName}.atbx");
            }

            if (!path.EndsWith(".atbx", StringComparison.OrdinalIgnoreCase))
                path += ".atbx";

            bool overwrite = args.TryGetValue("overwrite", out string? ow)
                             && bool.TryParse(ow, out var owb) && owb;

            try
            {
                AtbxManager.CreateToolbox(path, tbxName, overwrite);
            }
            catch (Exception ex)
            {
                return new(false, ex.Message, null);
            }

            // Add to project
            await QueuedTask.Run(() =>
            {
                try { Project.Current.AddItem(ItemFactory.Instance.Create(path) as IProjectItem); }
                catch { /* May fail if already added */ }
            });

            return new(true, null, new { path, name = tbxName });
        }

        private static IpcResponse HandleCreateModel(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("definition", out string? definition) ||
                string.IsNullOrWhiteSpace(definition))
                return new(false, "args 'toolboxPath' & 'definition' required", null);

            if (!File.Exists(path))
                return new(false, $"Toolbox not found: {path}", null);

            AtbxManager.CreateModel(path, definition);

            var defNode = JsonNode.Parse(definition);
            var modelName = defNode?["name"]?.GetValue<string>() ?? "unknown";
            return new(true, null, new
            {
                modelName,
                toolboxPath = path,
                created = true,
                hint = "Refresh the toolbox in Pro's Catalog pane to see the new model."
            });
        }

        private static IpcResponse HandleUpdateModel(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName) ||
                !args.TryGetValue("definition", out string? definition) ||
                string.IsNullOrWhiteSpace(definition))
                return new(false, "args 'toolboxPath', 'modelName' & 'definition' required", null);

            if (!File.Exists(path))
                return new(false, $"Toolbox not found: {path}", null);

            AtbxManager.UpdateModel(path, modelName, definition);
            return new(true, null, new { modelName, toolboxPath = path, updated = true });
        }

        /// <summary>
        /// Surgical write — updates only one input parameter's default value
        /// inside an existing model. See <see cref="AtbxManager.SetParameterDefault"/>
        /// for the byte-identical-everything-else guarantee.
        /// </summary>
        private static IpcResponse HandleSetParameterDefault(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName) ||
                !args.TryGetValue("parameterName", out string? paramName) ||
                string.IsNullOrWhiteSpace(paramName))
                return new(false, "args 'toolboxPath', 'modelName' & 'parameterName' required", null);

            // Empty default is meaningful (clears existing default); accept absent
            // arg as empty rather than rejecting.
            args.TryGetValue("defaultValue", out string? defaultValue);

            if (!File.Exists(path))
                return new(false, $"Toolbox not found: {path}", null);

            try
            {
                AtbxManager.SetParameterDefault(path, modelName, paramName, defaultValue ?? "");
                return new(true, null, new { modelName, parameterName = paramName, defaultValue = defaultValue ?? "", modified = true });
            }
            catch (Exception ex)
            {
                return new(false, ex.Message, null);
            }
        }

        /// <summary>
        /// Surgical write — updates only one step's one parameter inside an
        /// existing model. See <see cref="AtbxManager.SetStepParameter"/>.
        /// </summary>
        private static IpcResponse HandleSetStepParameter(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName) ||
                !args.TryGetValue("stepName", out string? stepName) ||
                string.IsNullOrWhiteSpace(stepName) ||
                !args.TryGetValue("paramKey", out string? paramKey) ||
                string.IsNullOrWhiteSpace(paramKey) ||
                !args.TryGetValue("paramValue", out string? paramValueJson) ||
                string.IsNullOrWhiteSpace(paramValueJson))
                return new(false, "args 'toolboxPath', 'modelName', 'stepName', 'paramKey' & 'paramValue' required", null);

            if (!File.Exists(path))
                return new(false, $"Toolbox not found: {path}", null);

            try
            {
                AtbxManager.SetStepParameter(path, modelName, stepName, paramKey, paramValueJson);
                return new(true, null, new { modelName, stepName, paramKey, modified = true });
            }
            catch (Exception ex)
            {
                return new(false, ex.Message, null);
            }
        }

        // GP tool positional signatures live in ModelBuilder/GpToolCatalog.cs
        // so the writer (AtbxManager) can consult the same data to canonicalize
        // user-supplied parameter keys before they reach Pro's load-time
        // normalizer. For the rationale behind needing positional signatures at
        // all (Pro SDK exposes no introspection API; dense-packing
        // dict-insertion-order silently corrupts calls when a model omits an
        // optional slot before an included one), see the GpToolCatalog summary.

        /// <summary>
        /// Runs a ModelBuilder model with the given parameter dict. ModelBuilder models
        /// bind parameters by DECLARED ORDER (arcpy positional convention), but agents
        /// pass by NAME via the JSON dict. We read the model's parameter declaration
        /// order via <see cref="AtbxManager.DescribeModel"/> and remap the user's named
        /// values to the correct positional slots — without that, dict insertion order
        /// becomes the implicit positional order and any mismatch (especially if the
        /// model has parameters the user didn't supply, like <c>Output_Workspace</c>)
        /// shifts every subsequent value into the wrong slot. Symptom: an arcpy error
        /// referencing a parameter NAME the user never typed, with a value that was
        /// meant for a different parameter.
        ///
        /// On failure, builds a defensive error message — <c>result.Messages</c> can
        /// be empty when arcpy fails before emitting any messages, so the response
        /// includes a fallback "no messages" string instead of an empty
        /// <c>"Model execution failed: "</c> (the F5 pattern).
        /// </summary>
        private static async Task<IpcResponse> HandleRunModel(Dictionary<string, string>? args)
        {
            return await RunModelCore(args, null);
        }

        /// <summary>
        /// Kicks off a model run on a background task and returns a job id
        /// immediately. Caller polls <c>HandleGetRunStatus</c> for progress and
        /// completion. Designed to escape agent-side MCP tool-call ceilings
        /// (Claude Desktop caps at ~4 min; Aurora-class models can run longer
        /// because of hosted-service clips). Each status poll is a cheap
        /// snapshot read so polling overhead is minimal.
        /// </summary>
        private static IpcResponse HandleRunModelAsync(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName))
                return new(false, "args 'toolboxPath' & 'modelName' required", null);

            // Drop completed jobs older than 1 hour so the registry doesn't grow
            // unboundedly. Polling clients should fetch final status within that
            // window; long-finished jobs are no longer interesting.
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var kv in _runJobs.ToArray())
            {
                if (kv.Value.EndedUtc.HasValue && kv.Value.EndedUtc.Value < cutoff)
                    _runJobs.TryRemove(kv.Key, out _);
            }

            var job = new RunJob
            {
                JobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                ToolboxPath = path,
                ModelName = modelName,
                StartedUtc = DateTime.UtcNow,
                Status = "running"
            };
            _runJobs[job.JobId] = job;

            // Fire-and-forget background execution. Exceptions are captured into
            // the job record; nothing bubbles to an unobserved task fault.
            _ = Task.Run(async () =>
            {
                try
                {
                    var resp = await RunModelCore(args, job);
                    lock (job.Lock)
                    {
                        if (resp.Ok)
                        {
                            job.Status = "succeeded";
                        }
                        else
                        {
                            job.Status = "failed";
                            job.Error = resp.Error ?? "unknown failure";
                            // Async failures must reach mcp-bridge.log too — the
                            // in-memory job dies with Pro, and the sync path's
                            // LogNonSuccess hook never sees this response.
                            LogNonSuccess(new IpcRequest($"pro.runModelAsync[job {job.JobId}]", args), resp.Error);
                            // Pull failedStep/tool out of the response data shape
                            // that RunModelCore returns on a step failure.
                            if (resp.Data is { } d)
                            {
                                try
                                {
                                    var dynData = d.GetType().GetProperty("failedStep")?.GetValue(d) as string;
                                    if (dynData != null) job.FailedStep = dynData;
                                    var dynTool = d.GetType().GetProperty("tool")?.GetValue(d) as string;
                                    if (dynTool != null) job.FailedTool = dynTool;
                                }
                                catch { }
                            }
                        }
                        job.EndedUtc = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    LogException(new IpcRequest($"pro.runModelAsync[job {job.JobId}]", args), ex);
                    lock (job.Lock)
                    {
                        job.Status = "failed";
                        job.Error = $"{ex.GetType().Name}: {ex.Message}";
                        job.EndedUtc = DateTime.UtcNow;
                    }
                }
            });

            return new(true, null, new
            {
                jobId = job.JobId,
                started = job.StartedUtc,
                pollWith = "get_run_status"
            });
        }

        /// <summary>
        /// Returns a snapshot of a background run job's state. Cheap to call
        /// repeatedly; takes the job lock just long enough to copy the
        /// current state. Status values: <c>running</c>, <c>succeeded</c>,
        /// <c>failed</c>. Once <c>endedUtc</c> is populated, the run is done
        /// and the messages list is final.
        /// </summary>
        private static IpcResponse HandleGetRunStatus(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("jobId", out string? jobId) ||
                string.IsNullOrWhiteSpace(jobId))
                return new(false, "arg 'jobId' required", null);

            if (!_runJobs.TryGetValue(jobId, out var job))
                return new(false, $"Job '{jobId}' not found (expired or never existed)", null);

            // Incremental message reads: pass messagesFrom=<count from last poll>
            // to receive only new messages. Long model runs accumulate hundreds
            // of GP messages; re-transmitting the full list every poll bloats
            // the agent's context for no benefit.
            int messagesFrom = 0;
            if (args.TryGetValue("messagesFrom", out string? mfStr) &&
                int.TryParse(mfStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mf) &&
                mf > 0)
                messagesFrom = mf;

            lock (job.Lock)
            {
                var skip = Math.Min(messagesFrom, job.Messages.Count);
                return new(true, null, new
                {
                    jobId = job.JobId,
                    status = job.Status,
                    startedUtc = job.StartedUtc,
                    endedUtc = job.EndedUtc,
                    totalSteps = job.TotalSteps,
                    completedSteps = job.CompletedSteps,
                    currentStep = job.CurrentStep,
                    failedStep = job.FailedStep,
                    failedTool = job.FailedTool,
                    error = job.Error,
                    totalMessages = job.Messages.Count,
                    messagesFrom = skip,
                    messages = job.Messages.Skip(skip).ToList()
                });
            }
        }

        /// <summary>
        /// Background job state for an async model run. Updated by
        /// RunModelCore from a Task.Run thread; read by HandleGetRunStatus
        /// from the IPC handler thread. The <see cref="Lock"/> serializes
        /// concurrent writes from the executor and snapshot reads from the
        /// status handler.
        /// </summary>
        private sealed class RunJob
        {
            public string JobId { get; init; } = "";
            public string ToolboxPath { get; init; } = "";
            public string ModelName { get; init; } = "";
            public DateTime StartedUtc { get; init; }
            public DateTime? EndedUtc { get; set; }
            public string Status { get; set; } = "running";
            public int TotalSteps { get; set; }
            public int CompletedSteps { get; set; }
            public string? CurrentStep { get; set; }
            public string? FailedStep { get; set; }
            public string? FailedTool { get; set; }
            public string? Error { get; set; }
            public List<object> Messages { get; } = new();
            public object Lock { get; } = new();
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunJob>
            _runJobs = new();

        /// <summary>Outcome of one (possibly nested) model-graph execution.</summary>
        private sealed class GraphRunOutcome
        {
            public bool Ok;
            public string? Error;
            public object? ErrorData;
            public int StepsRun;
            /// <summary>Not-ready (valid=false) steps that failed and were skipped.</summary>
            public int SkippedSteps;
            /// <summary>.pyt steps (and their dependents) skipped under pytMode="skip"
            /// — a subset of SkippedSteps, broken out so callers can tell partial-mode
            /// omissions from valid=false dead chains.</summary>
            public int SkippedPytSteps;
            /// <summary>Final values of the model's Parameter variables, by name —
            /// how a parent maps a nested model's outputs back onto its own slots.</summary>
            public Dictionary<string, string> ParamOutputs = new(StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<IpcResponse> RunModelCore(
            Dictionary<string, string>? args, RunJob? job)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName))
                return new(false, "args 'toolboxPath' & 'modelName' required", null);

            // Collect user-supplied named values (case-insensitive matching).
            var namedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args.TryGetValue("parameters", out string? paramsJson) && !string.IsNullOrWhiteSpace(paramsJson))
            {
                var paramsNode = JsonNode.Parse(paramsJson)?.AsObject();
                if (paramsNode != null)
                {
                    // CoerceScalarToString: agents pass JSON numbers/bools for
                    // numeric model parameters; GetValue<string>() would throw.
                    foreach (var kv in paramsNode)
                        namedValues[kv.Key] = CoerceScalarToString(kv.Value);
                }
            }

            // Optional variable overrides: like 'parameters' but applies to ANY
            // model variable by name, not just exposed parameters. The repair
            // lever for models authored against project map layers — an agent
            // can resolve bare layer names ("Farmland_CVWD_HUC8") to dataset
            // paths and run the model without that project/map being open.
            Dictionary<string, string>? varOverrides = null;
            if (args.TryGetValue("variableOverrides", out string? ovJson) && !string.IsNullOrWhiteSpace(ovJson))
            {
                var ovNode = JsonNode.Parse(ovJson)?.AsObject();
                if (ovNode != null)
                {
                    varOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in ovNode)
                        varOverrides[kv.Key] = CoerceScalarToString(kv.Value);
                }
            }

            // .pyt-step handling: "execute" (default) dispatches them to a child
            // arcpy process; "skip" is partial mode — skip them (and anything
            // downstream of them) and run the pure-GP/nested remainder.
            var pytMode = "execute";
            if (args.TryGetValue("pytMode", out var pytModeRaw) && !string.IsNullOrWhiteSpace(pytModeRaw))
            {
                pytMode = pytModeRaw.Trim().ToLowerInvariant();
                if (pytMode != "execute" && pytMode != "skip")
                    return new(false, $"invalid pytMode '{pytModeRaw}' — use 'execute' or 'skip'", null);
            }
            int pytTimeoutSeconds = 3600;
            if (args.TryGetValue("pytTimeoutSeconds", out var pytTimeoutRaw)
                && int.TryParse(pytTimeoutRaw, out var pytTimeoutVal) && pytTimeoutVal > 0)
                pytTimeoutSeconds = pytTimeoutVal;

            // Isolated child scratch (default ON): .pyt children write their
            // env-derived outputs (arcpy.env.scratchGDB / env.workspace) into a
            // run-private GDB instead of Pro's live scratch. Pro's main process
            // holds SHARED locks on every file GDB it writes in-proc during the
            // run, so a child needing an EXCLUSIVE schema lock there dies with
            // ERROR 000464 — ClearWorkspaceCache alone proved insufficient in
            // the field. Downstream steps follow the child's ACTUAL output
            // paths via the getOutput mapping, so connected chains work; only
            // models whose stored LITERAL paths expect .pyt outputs inside the
            // parent scratch need pytIsolatedScratch=false.
            string? pytScratchGdb = null;
            bool isolatePyt = !(args.TryGetValue("pytIsolatedScratch", out var isoRaw)
                && string.Equals(isoRaw?.Trim(), "false", StringComparison.OrdinalIgnoreCase));
            if (isolatePyt && pytMode == "execute")
            {
                try
                {
                    var tbxDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                    var stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
                    pytScratchGdb = System.IO.Path.Combine(
                        string.IsNullOrEmpty(tbxDir) ? System.IO.Path.GetTempPath() : tbxDir!,
                        $"pyt_scratch_{stamp}.gdb");
                }
                catch { pytScratchGdb = null; }
            }

            var allMessages = new List<object>();
            var outcome = await ExecuteGraphAsync(
                path, modelName, namedValues, job,
                allMessages, stepPrefix: "",
                inFlight: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                depth: 0, varOverrides, pytMode, pytTimeoutSeconds, pytScratchGdb);

            if (!outcome.Ok)
                return new(false, outcome.Error, outcome.ErrorData);

            return new(true, null, new
            {
                success = true,
                stepsRun = outcome.StepsRun,
                skippedNotReadySteps = outcome.SkippedSteps,
                skippedPytSteps = outcome.SkippedPytSteps,
                messages = allMessages
            });
        }

        /// <summary>
        /// Executes one model graph step-by-step; recurses for nestedModel steps.
        /// Calling ExecuteToolAsync on a model as a whole tool triggers Pro's
        /// chain pre-validation, which rejects any intermediate INPUT whose
        /// producing tool has not yet created the FC on disk — fatal on first
        /// run. The ribbon Run dialog avoids this by running ModelBuilder's own
        /// engine: each process is validated JIT immediately before it executes,
        /// after its upstream outputs already exist. We mirror that by parsing
        /// the model graph, topologically sorting processes, and calling
        /// ExecuteToolAsync once per step with refs resolved against a runtime
        /// variable map — and we recurse the same way into nested models hosted
        /// in .atbx toolboxes (nested models in legacy binary .tbx can't be
        /// parsed, so those fall back to whole-tool dispatch by path, accepting
        /// the first-run pre-validation risk).
        /// </summary>
        private static async Task<GraphRunOutcome> ExecuteGraphAsync(
            string path, string modelName,
            Dictionary<string, string> namedValues,
            RunJob? job,
            List<object> allMessages,
            string stepPrefix,
            HashSet<string> inFlight,
            int depth,
            Dictionary<string, string>? varOverrides = null,
            string pytMode = "execute",
            int pytTimeoutSeconds = 3600,
            string? pytScratchGdb = null)
        {
            bool isRoot = depth == 0;
            if (depth > 8)
                return new GraphRunOutcome { Error = $"Nested model depth exceeded 8 at '{modelName}' — aborting (runaway nesting?)." };

            string cycleKey;
            try { cycleKey = $"{System.IO.Path.GetFullPath(path)}::{modelName}"; }
            catch { cycleKey = $"{path}::{modelName}"; }
            if (!inFlight.Add(cycleKey))
                return new GraphRunOutcome { Error = $"Nested model cycle detected: '{modelName}' ({path}) is already executing in this chain." };
            try
            {

            ModelGraph graph;
            try
            {
                graph = AtbxManager.WalkModel(path, modelName);
            }
            catch (Exception ex)
            {
                return new GraphRunOutcome { Error = $"Failed to read model from '{path}': {ex.Message}" };
            }

            if (job != null && isRoot)
            {
                lock (job.Lock) job.TotalSteps = graph.Processes.Count;
            }

            // Iterators (and unknown legacy encodings) still have no step-by-step
            // semantics — reject those. scriptTool / nestedModel steps are now
            // executed: nested models recurse through this method, script tools
            // dispatch by qualified toolbox path.
            var badKind = graph.Processes.FirstOrDefault(p =>
                p.Kind is APBridgeAddIn.ModelBuilder.ToolKind.Iterator
                       or APBridgeAddIn.ModelBuilder.ToolKind.Unknown);
            if (badKind != null)
            {
                return new GraphRunOutcome
                {
                    Error = $"Model '{modelName}' contains step '{badKind.Name}' of kind '{badKind.Kind}' " +
                            $"(tool '{badKind.Tool}'). Iterator/unknown steps aren't supported by " +
                            "step-by-step execution; run that model via Pro's ribbon or compose " +
                            "run_gp_tool calls."
                };
            }

            // Determine model input parameter names (exposed Parameter variables
            // that no process produces). Used to catch agent typos early.
            var producedIds = graph.Processes
                .SelectMany(p => p.Params.Values)
                .Where(pm => pm.OutputVariableId != null)
                .Select(pm => pm.OutputVariableId!)
                .ToHashSet();
            var inputParamNames = graph.Variables.Values
                .Where(v => v.IsParameter && !producedIds.Contains(v.Id))
                .Select(v => v.Name)
                .ToList();

            var unknownKeys = namedValues.Keys
                .Where(k => !inputParamNames.Any(n => n.Equals(k, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unknownKeys.Any())
            {
                if (isRoot)
                    return new GraphRunOutcome
                    {
                        Error = $"Unknown model parameter(s): {string.Join(", ", unknownKeys)}. " +
                                $"Model '{modelName}' expects: [{string.Join(", ", inputParamNames)}]"
                    };
                // Nested call: parent slot names that don't match a child input
                // param (e.g. the child's derived params) are dropped, not fatal —
                // the child falls back to its own declared defaults for them.
                foreach (var k in unknownKeys) namedValues.Remove(k);
            }

            // Base directory for resolving relative catalog paths stored in the
            // model ("Store relative path names" projects write ".\X.gdb\FC" /
            // "..\Other\Y.gdb\FC"). Pro resolves these against the toolbox's
            // home folder at run time; passing them through verbatim makes
            // arcpy resolve them against an arbitrary CWD → ERROR 000875 etc.
            string toolboxDir;
            try { toolboxDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ""; }
            catch { toolboxDir = ""; }

            string ResolveRelative(string? value)
            {
                if (string.IsNullOrEmpty(value) || toolboxDir.Length == 0) return value ?? "";
                var t = value.TrimStart();
                bool rel = t.StartsWith(@".\") || t.StartsWith("./")
                        || t.StartsWith(@"..\") || t.StartsWith("../");
                if (!rel) return value;
                try { return System.IO.Path.GetFullPath(System.IO.Path.Combine(toolboxDir, t)); }
                catch { return value; }
            }

            // Seed the runtime variable map: variable id → value (path or literal).
            // User-supplied input values win over the variable's stored default.
            // Intermediate variables that have an explicit stored path are pre-
            // seeded too, so explicit-path models honor the author's choice.
            var runtimeValues = new Dictionary<string, string>();
            foreach (var v in graph.Variables.Values)
            {
                if (v.IsParameter && namedValues.TryGetValue(v.Name, out var userVal))
                    runtimeValues[v.Id] = userVal;
                else if (!string.IsNullOrEmpty(v.StoredValue))
                    runtimeValues[v.Id] = ResolveRelative(v.StoredValue);
            }

            // Direct variable overrides (any variable, by name or display name)
            // beat both user parameter values and stored defaults. Root only.
            if (varOverrides is { Count: > 0 })
            {
                foreach (var v in graph.Variables.Values)
                {
                    if (varOverrides.TryGetValue(v.Name, out var ov)
                        || (v.DisplayName != null && varOverrides.TryGetValue(v.DisplayName, out ov)))
                        runtimeValues[v.Id] = ov;
                }
            }

            // Stored values themselves can embed %Var% references (intermediate
            // output variables routinely store "%Output Workspace%\Clipped_X").
            // Substitute them against the fully-seeded map — two sweeps so a
            // value that resolves through another %-bearing value still lands.
            for (int sweep = 0; sweep < 2; sweep++)
            {
                foreach (var key in runtimeValues.Keys.ToList())
                {
                    var val = runtimeValues[key];
                    if (val.IndexOf('%') < 0) continue;
                    var substituted = SubstituteModelVars(val, graph, runtimeValues);
                    if (!ReferenceEquals(substituted, val) && substituted != val)
                        runtimeValues[key] = ResolveRelative(substituted);
                }
            }

            // Workspace for generating derived-output paths. Same source as
            // DefaultRunEnvironments, but we need the path string directly to
            // build per-step output paths upfront (so downstream refs resolve).
            string scratchGdb;
            try { scratchGdb = Project.Current?.DefaultGeodatabasePath ?? ""; }
            catch { scratchGdb = ""; }

            var env = DefaultRunEnvironments();
            int completedSteps = 0;
            int skippedSteps = 0;
            int skippedPytSteps = 0;
            // Output-variable ids produced by steps skipped under pytMode="skip".
            // Steps that consume them are skipped too (cascade) — running them
            // would surface confusing GP errors on empty inputs.
            var skippedOutputVarIds = new HashSet<string>();

            // Per-step environment overrides merged over the run defaults —
            // same semantics as the inline merge in the GP-dispatch path below,
            // reused by the out-of-proc .pyt branch.
            IReadOnlyList<KeyValuePair<string, string>> MergeStepEnv(
                APBridgeAddIn.ModelBuilder.ModelProcess p)
            {
                if (p.Environments is not { Count: > 0 }) return env;
                var merged = new List<KeyValuePair<string, string>>(env);
                foreach (var (envKey, envParam) in p.Environments)
                {
                    string? envVal = null;
                    if (envParam.RefVariableId != null)
                        runtimeValues.TryGetValue(envParam.RefVariableId, out envVal);
                    else if (envParam.LiteralValue != null)
                        envVal = ResolveRelative(SubstituteModelVars(envParam.LiteralValue, graph, runtimeValues));
                    if (string.IsNullOrEmpty(envVal)) continue;
                    merged.RemoveAll(kv => kv.Key.Equals(envKey, StringComparison.OrdinalIgnoreCase));
                    merged.Add(new KeyValuePair<string, string>(envKey, envVal));
                }
                return merged;
            }
            // Derived-output names assigned this run. Distinct variable names can
            // sanitize to the same GDB name ("Clip Output" vs "Clip_Output");
            // with overwriteoutput pinned true the later step would silently
            // replace the earlier step's data. Uniquify on collision.
            var usedDerivedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in graph.Processes)
            {
                if (job != null)
                {
                    lock (job.Lock) job.CurrentStep = stepPrefix + proc.Name;
                }

                // ── Cascade: inputs come from a step skipped under pytMode="skip" ──
                if (skippedOutputVarIds.Count > 0)
                {
                    bool dependsOnSkipped =
                        proc.Params.Values.Any(pm =>
                            pm.OutputVariableId == null && (
                                (pm.RefVariableId != null && skippedOutputVarIds.Contains(pm.RefVariableId)) ||
                                (pm.RefVariableIds != null && pm.RefVariableIds.Any(skippedOutputVarIds.Contains))))
                        || proc.PreconditionVariableIds.Any(skippedOutputVarIds.Contains);
                    if (dependsOnSkipped)
                    {
                        skippedSteps++;
                        skippedPytSteps++;
                        foreach (var (_, pm) in proc.Params)
                            if (pm.OutputVariableId != null) skippedOutputVarIds.Add(pm.OutputVariableId);
                        var cascadeMsg = new { step = stepPrefix + proc.Name, type = "Warning",
                            text = "Skipped: depends on the output of a skipped .pyt step (pytMode=skip cascade)." };
                        allMessages.Add(cascadeMsg);
                        if (job != null) { lock (job.Lock) job.Messages.Add(cascadeMsg); }
                        continue;
                    }
                }

                // ── .pyt-hosted script tool: out-of-proc dispatch or partial-mode skip ──
                // In-proc ExecuteToolAsync on a .pyt path never returns (documented
                // hang), so these steps run in a CHILD arcpy process (propy.bat
                // spawned from Pro's own clean environment). pytMode="skip" turns
                // them into best-effort skips instead so the GP remainder runs.
                if (proc.Kind == APBridgeAddIn.ModelBuilder.ToolKind.PythonScriptTool)
                {
                    if (pytMode == "skip")
                    {
                        skippedSteps++;
                        skippedPytSteps++;
                        foreach (var (_, pm) in proc.Params)
                            if (pm.OutputVariableId != null) skippedOutputVarIds.Add(pm.OutputVariableId);
                        var skipMsg = new { step = stepPrefix + proc.Name, type = "Warning",
                            text = $"Skipped .pyt step (pytMode=skip): {proc.Tool}" };
                        allMessages.Add(skipMsg);
                        if (job != null) { lock (job.Lock) job.Messages.Add(skipMsg); }
                        continue;
                    }

                    var (pytBox, pytToolName) = AtbxManager.ResolveToolReference(path, proc.Tool);

                    // kwargs by slot name — .pyt tools expose no tool.content to read
                    // a positional signature from, and ImportToolbox tool functions
                    // accept parameter names as keywords. In-direction slots only;
                    // outputs come back via the result object.
                    var pytKwargs = new List<KeyValuePair<string, string>>();
                    foreach (var (slotName, pm) in proc.Params)
                    {
                        if (pm.OutputVariableId != null) continue;
                        string? val = null;
                        if (pm.RefVariableIds is { Count: > 1 })
                        {
                            var parts = new List<string>();
                            foreach (var rid in pm.RefVariableIds)
                                if (runtimeValues.TryGetValue(rid, out var rv) && !string.IsNullOrEmpty(rv))
                                    parts.Add(rv);
                            val = string.Join(";", parts);
                        }
                        else if (pm.RefVariableId != null)
                        {
                            runtimeValues.TryGetValue(pm.RefVariableId, out val);
                        }
                        else if (pm.LiteralValue != null)
                        {
                            val = ResolveRelative(SubstituteModelVars(pm.LiteralValue, graph, runtimeValues));
                        }
                        if (!string.IsNullOrEmpty(val)) pytKwargs.Add(new(slotName, val!));
                    }

                    // Isolated child scratch: create the run-private GDB on first
                    // use and point the child's workspace + scratchWorkspace at
                    // it, so env-derived writes (arcpy.env.scratchGDB fallbacks
                    // like NetNeed's Net_Unmet_Need) never contend with the
                    // shared locks Pro's main process holds on the live scratch.
                    // Inputs stay absolute parent paths — cross-process READS
                    // take shared locks and don't conflict.
                    if (pytScratchGdb != null && !Directory.Exists(pytScratchGdb))
                    {
                        var isoFolder = System.IO.Path.GetDirectoryName(pytScratchGdb);
                        var isoName = System.IO.Path.GetFileName(pytScratchGdb);
                        if (!string.IsNullOrEmpty(isoFolder) && !string.IsNullOrEmpty(isoName))
                        {
                            try { Directory.CreateDirectory(isoFolder); } catch { /* CreateFileGDB error will tell */ }
                            var mkIso = await Geoprocessing.ExecuteToolAsync("management.CreateFileGDB",
                                Geoprocessing.MakeValueArray(isoFolder, isoName), env);
                            var isoNote = new { step = stepPrefix + proc.Name, type = "Message",
                                text = mkIso.IsFailed
                                    ? $"Could not create isolated .pyt scratch GDB {pytScratchGdb}: " +
                                      string.Join("; ", mkIso.Messages.Select(m => m.Text))
                                    : $"Isolated .pyt scratch GDB: {pytScratchGdb}" };
                            allMessages.Add(isoNote);
                            if (job != null) { lock (job.Lock) job.Messages.Add(isoNote); }
                        }
                    }
                    IReadOnlyList<KeyValuePair<string, string>> childEnv = MergeStepEnv(proc);
                    if (pytScratchGdb != null && Directory.Exists(pytScratchGdb))
                    {
                        var redirected = new List<KeyValuePair<string, string>>(childEnv);
                        redirected.RemoveAll(kv =>
                            kv.Key.Equals("workspace", StringComparison.OrdinalIgnoreCase) ||
                            kv.Key.Equals("scratchWorkspace", StringComparison.OrdinalIgnoreCase));
                        redirected.Add(new KeyValuePair<string, string>("workspace", pytScratchGdb));
                        redirected.Add(new KeyValuePair<string, string>("scratchWorkspace", pytScratchGdb));
                        childEnv = redirected;
                    }

                    // Release GP-held schema locks in the parent before the child
                    // writes: Pro's GP session keeps shared locks on every file
                    // GDB it has touched this run, and a child needing an
                    // EXCLUSIVE schema lock (e.g. CopyFeatures over an existing
                    // FC in %scratchGDB%) dies with ERROR 000464 otherwise.
                    // Kept alongside scratch isolation as defense in depth for
                    // tools that write to explicit parent-GDB paths.
                    try
                    {
                        await Geoprocessing.ExecuteToolAsync("management.ClearWorkspaceCache",
                            Geoprocessing.MakeValueArray(), env);
                    }
                    catch { /* child's own error surfaces if locks persist */ }

                    var pytResult = File.Exists(pytBox)
                        ? await RunPytToolAsync(pytBox, pytToolName, pytKwargs, childEnv, pytTimeoutSeconds)
                        : new PytRunResult { Error = $".pyt file not found: {pytBox}" };

                    // One lock-aware retry: 000464 is transient when the parent's
                    // cache was repopulated between the clear and the child's
                    // write (or a prior child/attempt left a lock draining).
                    if (!pytResult.Ok && pytResult.Error?.Contains("000464") == true)
                    {
                        var retryNote = new { step = stepPrefix + proc.Name, type = "Warning",
                            text = "Schema-lock contention (ERROR 000464) — clearing parent workspace cache and retrying the .pyt child once." };
                        allMessages.Add(retryNote);
                        if (job != null) { lock (job.Lock) job.Messages.Add(retryNote); }
                        try
                        {
                            await Geoprocessing.ExecuteToolAsync("management.ClearWorkspaceCache",
                                Geoprocessing.MakeValueArray(), env);
                        }
                        catch { /* proceed to retry regardless */ }
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        pytResult = await RunPytToolAsync(pytBox, pytToolName, pytKwargs, childEnv, pytTimeoutSeconds);
                    }

                    if (!pytResult.Ok)
                    {
                        var pytMsgs = pytResult.Error ?? "child arcpy process failed with no message";
                        if (proc.MarkedInvalid)
                        {
                            // Mirror the GP valid=false semantics exactly: count the
                            // skip but do NOT seed skippedOutputVarIds — downstream
                            // consumers resolve the missing ref through the same
                            // unresolved-ref logic as after a GP valid=false skip.
                            // (Seeding here made the pytMode=skip cascade fire under
                            // pytMode=execute, inflating skippedPytSteps and printing
                            // a skip-mode message in the wrong mode.)
                            skippedSteps++;
                            var skipMsg = new { step = stepPrefix + proc.Name, type = "Warning",
                                text = $"Skipped not-ready (valid=false) .pyt step after failure: {pytMsgs}" };
                            allMessages.Add(skipMsg);
                            if (job != null) { lock (job.Lock) job.Messages.Add(skipMsg); }
                            continue;
                        }
                        return new GraphRunOutcome
                        {
                            Error = $"Step '{stepPrefix}{proc.Name}' (.pyt {proc.Tool}) failed: {pytMsgs}",
                            ErrorData = new { failedStep = stepPrefix + proc.Name, tool = proc.Tool, completedSteps }
                        };
                    }

                    // Map result outputs → out-direction slots in stored order;
                    // arcpy Result.getOutput(i) indexes output params in declared
                    // order, which empirically matches the stored slot order.
                    int outIdx = 0;
                    foreach (var (_, pm) in proc.Params)
                    {
                        if (pm.OutputVariableId == null) continue;
                        if (outIdx < pytResult.Outputs.Count && !string.IsNullOrEmpty(pytResult.Outputs[outIdx]))
                            runtimeValues[pm.OutputVariableId] = pytResult.Outputs[outIdx];
                        outIdx++;
                    }
                    // Fallback for any out slot the result didn't cover: first
                    // resolved input value (the in-place pre-pass pattern).
                    var firstInput = pytKwargs.FirstOrDefault().Value;
                    foreach (var (_, pm) in proc.Params)
                    {
                        if (pm.OutputVariableId == null || runtimeValues.ContainsKey(pm.OutputVariableId)) continue;
                        if (!string.IsNullOrEmpty(firstInput))
                            runtimeValues[pm.OutputVariableId] = firstInput;
                    }

                    completedSteps++;
                    var okMsg = new { step = stepPrefix + proc.Name, type = "Message",
                        text = $".pyt tool '{pytToolName}' executed out-of-proc; outputs: [{string.Join("; ", pytResult.Outputs)}]" };
                    allMessages.Add(okMsg);
                    if (!string.IsNullOrEmpty(pytResult.Messages))
                        allMessages.Add(new { step = stepPrefix + proc.Name, type = "Message", text = pytResult.Messages! });
                    if (job != null)
                    {
                        lock (job.Lock)
                        {
                            if (isRoot) job.CompletedSteps++;
                            job.Messages.Add(okMsg);
                        }
                    }
                    continue;
                }

                // ── Nested model in an .atbx: recurse through this executor ──
                // (keeps the JIT per-step validation that makes first runs work).
                if (proc.Kind == APBridgeAddIn.ModelBuilder.ToolKind.NestedModel)
                {
                    var (childBox, childTool) = AtbxManager.ResolveToolReference(path, proc.Tool);
                    bool childIsAtbx = childBox.EndsWith(".atbx", StringComparison.OrdinalIgnoreCase)
                                       && File.Exists(childBox);
                    if (childIsAtbx)
                    {
                        // Map this step's resolved input slot values onto the child's
                        // named parameters (slot keys ARE the child's param names).
                        var childNamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (slotName, pm) in proc.Params)
                        {
                            if (pm.OutputVariableId != null) continue;
                            string? val = null;
                            if (pm.RefVariableIds is { Count: > 1 })
                            {
                                var parts = new List<string>();
                                foreach (var rid in pm.RefVariableIds)
                                    if (runtimeValues.TryGetValue(rid, out var rv) && !string.IsNullOrEmpty(rv))
                                        parts.Add(rv);
                                val = string.Join(";", parts);
                            }
                            else if (pm.RefVariableId != null)
                            {
                                runtimeValues.TryGetValue(pm.RefVariableId, out val);
                            }
                            else if (pm.LiteralValue != null)
                            {
                                val = ResolveRelative(SubstituteModelVars(pm.LiteralValue, graph, runtimeValues));
                            }
                            if (!string.IsNullOrEmpty(val)) childNamed[slotName] = val!;
                        }

                        var childOutcome = await ExecuteGraphAsync(
                            childBox, childTool, childNamed, job, allMessages,
                            stepPrefix + proc.Name + " > ", inFlight, depth + 1,
                            pytMode: pytMode, pytTimeoutSeconds: pytTimeoutSeconds,
                            pytScratchGdb: pytScratchGdb);
                        if (!childOutcome.Ok)
                        {
                            if (proc.MarkedInvalid)
                            {
                                skippedSteps++;
                                var skipMsg = new { step = stepPrefix + proc.Name, type = "Warning",
                                    text = $"Skipped not-ready (valid=false) nested step after failure: {childOutcome.Error}" };
                                allMessages.Add(skipMsg);
                                if (job != null) { lock (job.Lock) job.Messages.Add(skipMsg); }
                                continue;
                            }
                            return new GraphRunOutcome
                            {
                                Error = $"Nested model step '{stepPrefix}{proc.Name}' ({childTool}) failed: {childOutcome.Error}",
                                ErrorData = childOutcome.ErrorData
                                    ?? new { failedStep = stepPrefix + proc.Name, tool = proc.Tool, completedSteps }
                            };
                        }

                        // Surface the child's skip counts on the parent outcome so
                        // the root result reflects the whole recursion, not just
                        // top-level steps.
                        skippedSteps += childOutcome.SkippedSteps;
                        skippedPytSteps += childOutcome.SkippedPytSteps;

                        // Child's output params (by name) → this step's output slots.
                        foreach (var (slotName, pm) in proc.Params)
                        {
                            if (pm.OutputVariableId == null) continue;
                            if (childOutcome.ParamOutputs.TryGetValue(slotName, out var ov)
                                && !string.IsNullOrEmpty(ov))
                                runtimeValues[pm.OutputVariableId] = ov;
                            else if (childOutcome.SkippedPytSteps > 0)
                                // The child skipped .pyt steps and this output never
                                // materialized — cascade the skip to whatever
                                // consumes it instead of feeding empty refs to GP.
                                skippedOutputVarIds.Add(pm.OutputVariableId);
                        }

                        completedSteps++;
                        if (job != null && isRoot)
                        {
                            lock (job.Lock) job.CompletedSteps++;
                        }
                        continue;
                    }
                    // Nested model hosted in a legacy binary .tbx — can't parse it
                    // for recursion; fall through to whole-tool dispatch by path
                    // below (accepts Pro's chain pre-validation risk on first runs).
                }

                // Resolve each slot in JSON-insertion order. Pro empirically writes
                // process params in tool-declared slot order (per Desktop's data on
                // SummarizeWithin: in_polygons, in_sum_features, out_feature_class,
                // keep_all_polygons, sum_fields, sum_shape, shape_unit). Trusting
                // that order produces the positional value array ExecuteToolAsync
                // expects.
                // Build positional value array. Two strategies:
                //
                //   1) Known tool: walk GpToolCatalog.Signatures[proc.Tool] in declared
                //      order and fill each position from proc.Params by slot NAME. For
                //      slots the model omitted (sparse storage), insert "#" so arcpy
                //      uses the tool's declared default. This is the correct contract
                //      for GP system tools.
                //
                //   2) Unknown tool: fall back to dense-packing by JSON insertion
                //      order. Same as the old behavior — wrong for any tool that
                //      omits optional slots before included ones, but the resulting
                //      misalignment surfaces as obvious slot-mismatch errors that
                //      point at which tool to add to the signature table.
                //
                // Script tools (and .tbx-hosted nested models) dispatch by QUALIFIED
                // PATH ("<toolbox>\<tool>"); their signature comes from the target's
                // own tool.content when the target toolbox is an .atbx. Derived
                // output params are EXCLUDED from the calling signature (arcpy
                // contract) — they're recorded via the in-place pre-pass below and
                // refined from the GP result's return value after execution.
                string executeTool = proc.Tool;
                IReadOnlyList<string>? sig;
                var derivedOutSlots = new List<string>();
                if (proc.Kind == APBridgeAddIn.ModelBuilder.ToolKind.GpTool)
                {
                    sig = GpToolCatalog.ResolveSignature(proc.Tool);
                }
                else
                {
                    var (toolBox, toolName) = AtbxManager.ResolveToolReference(path, proc.Tool);
                    executeTool = System.IO.Path.Combine(toolBox, toolName);
                    var slots = AtbxManager.GetToolSignature(toolBox, toolName);
                    if (slots != null)
                    {
                        sig = slots.Where(s => !s.IsDerivedOutput).Select(s => s.Name).ToList();
                        derivedOutSlots = slots.Where(s => s.IsDerivedOutput).Select(s => s.Name).ToList();
                    }
                    else
                    {
                        sig = null; // legacy .tbx target → dense-pack insertion order
                    }
                }
                // Dense-pack fallback for path-dispatched tools: output slots are
                // still excluded from the positional array (derived outputs are
                // not call arguments), recorded via the pre-pass instead.
                bool denseExcludeOutputs =
                    proc.Kind != APBridgeAddIn.ModelBuilder.ToolKind.GpTool && sig == null;
                if (denseExcludeOutputs)
                    derivedOutSlots = proc.Params
                        .Where(kv => kv.Value.OutputVariableId != null)
                        .Select(kv => kv.Key).ToList();
                var slotOrder =
                    sig != null ? sig.AsEnumerable()
                    : denseExcludeOutputs ? proc.Params.Where(kv => kv.Value.OutputVariableId == null).Select(kv => kv.Key)
                    : proc.Params.Keys;

                // Pre-pass: record outputs whose slot is NOT in the tool signature.
                // Some tools (notably selection tools — SelectLayerByLocation,
                // SelectLayerByAttribute) modify their in_layer in place and return
                // it; arcpy has no positional output param for the modified layer,
                // but ModelBuilder still names a logical output variable so
                // downstream steps can reference it. The signature walk below
                // skips any slot not in the signature, so without this pre-pass
                // the output variable never lands in the runtime map and
                // downstream refs resolve to empty → ERROR 000735.
                //
                // For each such output, record it as the first resolved input
                // value (typically in_layer). Outputs whose slot IS in the
                // signature are still recorded by the signature walk's existing
                // OutputVariableId branch.
                if (sig != null || denseExcludeOutputs)
                {
                    foreach (var (slotName, pm) in proc.Params)
                    {
                        if (pm.OutputVariableId == null) continue;
                        if (sig != null && sig.Contains(slotName, StringComparer.OrdinalIgnoreCase)) continue;
                        if (runtimeValues.ContainsKey(pm.OutputVariableId)) continue;

                        string? sourceValue = null;
                        foreach (var (_, sp) in proc.Params)
                        {
                            if (sp.OutputVariableId != null) continue;
                            if (sp.RefVariableId != null
                                && runtimeValues.TryGetValue(sp.RefVariableId, out var v)
                                && !string.IsNullOrEmpty(v))
                            {
                                sourceValue = v;
                                break;
                            }
                            if (sp.LiteralValue != null && !string.IsNullOrEmpty(sp.LiteralValue))
                            {
                                sourceValue = sp.LiteralValue;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(sourceValue))
                        {
                            runtimeValues[pm.OutputVariableId] = sourceValue;
                        }
                    }
                }

                var values = new List<object>();
                var missingOutputGdbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var slotName in slotOrder)
                {
                    if (!proc.Params.TryGetValue(slotName, out var pm))
                    {
                        // Slot exists in the tool signature but model didn't store it.
                        // "#" tells arcpy to use the tool's declared default.
                        values.Add("#");
                        continue;
                    }

                    if (pm.OutputVariableId != null)
                    {
                        if (!runtimeValues.TryGetValue(pm.OutputVariableId, out var outPath) ||
                            string.IsNullOrEmpty(outPath))
                        {
                            var varName = graph.Variables.TryGetValue(pm.OutputVariableId, out var ov)
                                ? SanitizeGdbName(ov.Name)
                                : $"output_{pm.OutputVariableId}";
                            // Collision-proof: "Clip Output" and "Clip_Output" both
                            // sanitize to Clip_Output; suffix until unique this run.
                            var baseName = varName;
                            int dupSuffix = 2;
                            while (!usedDerivedNames.Add(varName))
                                varName = $"{baseName}_{dupSuffix++}";
                            outPath = string.IsNullOrEmpty(scratchGdb)
                                ? varName
                                : $"{scratchGdb}\\{varName}";
                            runtimeValues[pm.OutputVariableId] = outPath;
                        }
                        // arcpy auto-creates env.scratchGDB the first time a tool
                        // writes into it; the step-by-step executor must mirror
                        // that or the first output routed into a not-yet-existing
                        // File GDB (e.g. a fresh toolbox folder's scratch.gdb)
                        // dies with ERROR 000210 before the step even runs.
                        var parentGdb = System.IO.Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(parentGdb)
                            && parentGdb.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase)
                            && !Directory.Exists(parentGdb))
                        {
                            missingOutputGdbs.Add(parentGdb);
                        }
                        values.Add(outPath);
                    }
                    else if (pm.RefVariableIds is { Count: > 1 })
                    {
                        // Multi-input slot (Merge.inputs, Union.in_features, ...):
                        // join all resolved values with ';' — arcpy's multivalue
                        // string syntax. Unresolved members are skipped (their GP
                        // error will surface at execution if they mattered).
                        var parts = new List<string>();
                        foreach (var rid in pm.RefVariableIds)
                        {
                            if (runtimeValues.TryGetValue(rid, out var rv) && !string.IsNullOrEmpty(rv))
                                parts.Add(rv);
                        }
                        values.Add(string.Join(";", parts));
                    }
                    else if (pm.RefVariableId != null)
                    {
                        if (runtimeValues.TryGetValue(pm.RefVariableId, out var refVal))
                        {
                            values.Add(refVal);
                        }
                        else
                        {
                            // Unresolved ref: distinguish "user didn't supply a model input"
                            // (use arcpy's "#" sentinel so the GP engine resolves from the
                            // variable's declared default) from "intermediate that the
                            // producer step should have populated but didn't" (pass empty
                            // so the error surfaces immediately).
                            var isUnsuppliedInput = graph.Variables.TryGetValue(pm.RefVariableId, out var v)
                                && v.IsParameter
                                && !producedIds.Contains(pm.RefVariableId);
                            values.Add(isUnsuppliedInput ? "#" : "");
                        }
                    }
                    else if (pm.LiteralValue != null)
                    {
                        values.Add(ResolveRelative(SubstituteModelVars(pm.LiteralValue, graph, runtimeValues)));
                    }
                    else
                    {
                        values.Add("");
                    }
                }

                // Per-step environment overrides (extent, cellSize, mask,
                // outputCoordinateSystem, ...) merged over the run defaults.
                // Pro's own engine honors these; ignoring them silently produces
                // different results than a ribbon run (e.g., un-clipped extents).
                var stepEnv = env;
                if (proc.Environments is { Count: > 0 })
                {
                    var merged = new List<KeyValuePair<string, string>>(env);
                    foreach (var (envKey, envParam) in proc.Environments)
                    {
                        string? envVal = null;
                        if (envParam.RefVariableId != null)
                            runtimeValues.TryGetValue(envParam.RefVariableId, out envVal);
                        else if (envParam.LiteralValue != null)
                            envVal = ResolveRelative(SubstituteModelVars(envParam.LiteralValue, graph, runtimeValues));
                        if (string.IsNullOrEmpty(envVal)) continue;

                        merged.RemoveAll(kv => kv.Key.Equals(envKey, StringComparison.OrdinalIgnoreCase));
                        merged.Add(new KeyValuePair<string, string>(envKey, envVal));
                    }
                    stepEnv = merged;
                }

                // Materialize any missing output File GDBs before the step runs
                // (mirrors arcpy's env.scratchGDB auto-create). Best-effort: if
                // creation fails, the step's own error surfaces the real cause.
                foreach (var gdb in missingOutputGdbs)
                {
                    var gdbFolder = System.IO.Path.GetDirectoryName(gdb);
                    var gdbName = System.IO.Path.GetFileName(gdb);
                    if (string.IsNullOrEmpty(gdbFolder) || string.IsNullOrEmpty(gdbName)) continue;
                    try { Directory.CreateDirectory(gdbFolder); } catch { /* step error will tell */ }
                    var mkGdb = await Geoprocessing.ExecuteToolAsync("management.CreateFileGDB",
                        Geoprocessing.MakeValueArray(gdbFolder, gdbName), env);
                    var mkNote = new { step = stepPrefix + proc.Name, type = "Message",
                        text = mkGdb.IsFailed
                            ? $"Could not auto-create missing output GDB {gdb}: " +
                              string.Join("; ", mkGdb.Messages.Select(m => m.Text))
                            : $"Auto-created missing output GDB {gdb}" };
                    allMessages.Add(mkNote);
                    if (job != null) { lock (job.Lock) job.Messages.Add(mkNote); }
                }

                var valueArray = Geoprocessing.MakeValueArray(values.ToArray());
                var stepResult = await Geoprocessing.ExecuteToolAsync(executeTool, valueArray, stepEnv);

                if (stepResult.IsFailed)
                {
                    var msgs = stepResult.Messages.Any()
                        ? string.Join("; ", stepResult.Messages.Select(m => $"{m.Type}: {m.Text}"))
                        : "arcpy reported failure with no messages";

                    // Hint when a layer-name reference fails because no map view
                    // is active. ERROR 000732 ("does not exist or is not supported")
                    // and ERROR 000840 ("not a Feature Layer") commonly fire when
                    // the user restarted Pro and no map tab is focused — layer-name
                    // refs in the model can't resolve. Adding a clear hint here
                    // saves the user from having to recognize the raw GP code.
                    if ((msgs.Contains("ERROR 000732") || msgs.Contains("ERROR 000840"))
                        && MapView.Active == null)
                    {
                        msgs += " [hint: no active map view — layer-name references "
                              + "in the model require a map tab to be focused. Open "
                              + "or click on a map view in Pro and retry.]";
                    }

                    // Not-ready steps (ModelBuilder valid="false") are best-effort:
                    // Pro's own canvas runs skip them, so a failure here warns and
                    // continues instead of killing the run. A step the model
                    // considers VALID failing is a real error — abort as before.
                    if (proc.MarkedInvalid)
                    {
                        skippedSteps++;
                        var skipMsg = new { step = stepPrefix + proc.Name, type = "Warning",
                            text = $"Skipped not-ready (valid=false) step after failure: {msgs}" };
                        allMessages.Add(skipMsg);
                        if (job != null) { lock (job.Lock) job.Messages.Add(skipMsg); }
                        continue;
                    }

                    return new GraphRunOutcome
                    {
                        Error = $"Step '{stepPrefix}{proc.Name}' ({executeTool}) failed: {msgs}",
                        ErrorData = new { failedStep = stepPrefix + proc.Name, tool = proc.Tool, completedSteps }
                    };
                }

                // Path-dispatched tools: refine derived-output values from the GP
                // result's return value (the pre-pass seeded them with the in-place
                // input as a fallback). Only unambiguous with a single derived out.
                if (derivedOutSlots.Count == 1)
                {
                    string? ret = null;
                    try { ret = stepResult.ReturnValue; } catch { /* host quirk — keep pre-pass value */ }
                    if (!string.IsNullOrEmpty(ret)
                        && proc.Params.TryGetValue(derivedOutSlots[0], out var dpm)
                        && dpm.OutputVariableId != null)
                    {
                        runtimeValues[dpm.OutputVariableId] = ret!;
                    }
                }

                completedSteps++;
                foreach (var m in stepResult.Messages)
                    allMessages.Add(new { step = stepPrefix + proc.Name, type = m.Type.ToString(), text = m.Text });

                if (job != null)
                {
                    lock (job.Lock)
                    {
                        if (isRoot) job.CompletedSteps++;
                        foreach (var m in stepResult.Messages)
                            job.Messages.Add(new { step = stepPrefix + proc.Name, type = m.Type.ToString(), text = m.Text });
                    }
                }
            }

            // Expose final Parameter-variable values so a parent model (or the
            // future) can map this graph's outputs by name.
            var outcome = new GraphRunOutcome
            {
                Ok = true,
                StepsRun = completedSteps,
                SkippedSteps = skippedSteps,
                SkippedPytSteps = skippedPytSteps
            };
            foreach (var v in graph.Variables.Values)
            {
                if (v.IsParameter && runtimeValues.TryGetValue(v.Id, out var pv) && !string.IsNullOrEmpty(pv))
                    outcome.ParamOutputs[v.Name] = pv;
            }
            return outcome;

            }
            finally
            {
                inFlight.Remove(cycleKey);
            }
        }

        /// <summary>Result of one out-of-proc .pyt tool execution.</summary>
        private sealed class PytRunResult
        {
            public bool Ok;
            public string? Error;
            public List<string> Outputs = new();
            public string? Messages;
        }

        // Python source for the out-of-proc .pyt step runner. Reads a JSON spec
        // (pyt path, tool name, kwargs, env), imports the toolbox, calls the
        // tool with keyword args, and prints ONE marked result line the bridge
        // parses from stdout. Single-quoted strings only — this is embedded in
        // a C# verbatim string.
        private const string PytRunnerScript = @"
import arcpy, json, sys, traceback

def main():
    with open(sys.argv[1], encoding='utf-8') as f:
        spec = json.load(f)
    env_attrs = {a.lower(): a for a in dir(arcpy.env)}
    for k, v in (spec.get('env') or {}).items():
        try:
            if isinstance(v, str) and v.lower() in ('true', 'false'):
                v = v.lower() == 'true'
            setattr(arcpy.env, env_attrs.get(k.lower(), k), v)
        except Exception:
            pass
    mod = arcpy.ImportToolbox(spec['pyt'])
    fn = getattr(mod, spec['tool'])
    res = fn(**(spec.get('kwargs') or {}))
    outs = []
    try:
        for i in range(res.outputCount):
            outs.append(str(res.getOutput(i)))
    except Exception:
        pass
    msgs = ''
    try:
        msgs = res.getMessages()[:8000]
    except Exception:
        pass
    print('::PYT_OK::' + json.dumps({'outputs': outs, 'messages': msgs}))

try:
    main()
except arcpy.ExecuteError:
    print('::PYT_ERR::' + json.dumps({'error': (arcpy.GetMessages(2) or 'ExecuteError')[:4000]}))
    sys.exit(1)
except Exception:
    print('::PYT_ERR::' + json.dumps({'error': traceback.format_exc()[:4000]}))
    sys.exit(1)
";

        /// <summary>
        /// Locates propy.bat under the running Pro install (the Add-In lives in
        /// ArcGISPro.exe, so its directory IS the install's bin folder).
        /// </summary>
        private static string? FindPropyBat()
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                var bin = string.IsNullOrEmpty(exe) ? null : System.IO.Path.GetDirectoryName(exe);
                if (bin != null)
                {
                    var p = System.IO.Path.Combine(bin, "Python", "Scripts", "propy.bat");
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* fall through to the default install path */ }
            var fallback = @"C:\Program Files\ArcGIS\Pro\bin\Python\Scripts\propy.bat";
            return File.Exists(fallback) ? fallback : null;
        }

        /// <summary>
        /// Executes one .pyt-hosted tool in a CHILD arcpy process via propy.bat.
        /// In-proc ExecuteToolAsync on a .pyt path never returns; a child
        /// process spawned from Pro inherits Pro's clean environment (conda
        /// activation works — the corrupt agent-shell PATH problem does not
        /// apply) and leaves Pro's Python lane untouched. Cross-process
        /// caveats: no selection propagation, no in_memory datasets — inputs
        /// must be concrete catalog paths, which is what the executor's
        /// runtime map holds for materialized intermediates.
        /// </summary>
        private static async Task<PytRunResult> RunPytToolAsync(
            string pytPath, string toolName,
            IReadOnlyList<KeyValuePair<string, string>> kwargs,
            IReadOnlyList<KeyValuePair<string, string>> envs,
            int timeoutSeconds)
        {
            var propy = FindPropyBat();
            if (propy == null)
                return new PytRunResult
                {
                    Error = "propy.bat not found under the Pro install — cannot dispatch .pyt " +
                            "steps out-of-proc. Re-run with pytMode=\"skip\" to run the rest of the model."
                };

            var workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ArcGisMcpBridge");
            Directory.CreateDirectory(workDir);
            var stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
            var scriptPath = System.IO.Path.Combine(workDir, $"pytrun_{stamp}.py");
            var specPath = System.IO.Path.Combine(workDir, $"pytrun_{stamp}.json");

            var kwObj = new JsonObject();
            foreach (var kv in kwargs) kwObj[kv.Key] = kv.Value;
            var envObj = new JsonObject();
            foreach (var kv in envs) envObj[kv.Key] = kv.Value;
            var spec = new JsonObject
            {
                ["pyt"] = pytPath,
                ["tool"] = toolName,
                ["kwargs"] = kwObj,
                ["env"] = envObj
            };

            try
            {
                File.WriteAllText(specPath, spec.ToJsonString(), new System.Text.UTF8Encoding(false));
                File.WriteAllText(scriptPath, PytRunnerScript, new System.Text.UTF8Encoding(false));

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    // propy.bat is a batch file — launch through cmd; /s keeps
                    // the nested quoting intact, /d skips AutoRun.
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /c \"\"{propy}\" \"{scriptPath}\" \"{specPath}\"\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workDir
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null)
                    return new PytRunResult { Error = "failed to start the child arcpy process" };

                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                using var cts = new System.Threading.CancellationTokenSource(
                    TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds)));
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return new PytRunResult
                    {
                        Error = $".pyt step timed out after {timeoutSeconds}s (child arcpy process killed). " +
                                "Raise pytTimeoutSeconds if the tool legitimately runs longer."
                    };
                }
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                var line = stdout.Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .LastOrDefault(l => l.StartsWith("::PYT_OK::") || l.StartsWith("::PYT_ERR::"));
                if (line == null)
                {
                    var detail = (stderr + "\n" + stdout).Trim();
                    if (detail.Length > 4000) detail = detail.Substring(0, 4000);
                    return new PytRunResult
                    {
                        Error = $"child arcpy process exited ({p.ExitCode}) without a result marker: {detail}"
                    };
                }
                if (line.StartsWith("::PYT_ERR::"))
                {
                    var errNode = JsonNode.Parse(line.Substring("::PYT_ERR::".Length));
                    return new PytRunResult { Error = errNode?["error"]?.ToString() ?? "unknown .pyt error" };
                }

                var okNode = JsonNode.Parse(line.Substring("::PYT_OK::".Length));
                var result = new PytRunResult { Ok = true };
                if (okNode?["outputs"] is JsonArray outputsArr)
                    foreach (var o in outputsArr)
                        result.Outputs.Add(o?.ToString() ?? "");
                result.Messages = okNode?["messages"]?.ToString();
                return result;
            }
            catch (Exception ex)
            {
                return new PytRunResult { Error = $"out-of-proc .pyt dispatch failed: {ex.Message}" };
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { /* temp cleanup only */ }
                try { File.Delete(specPath); } catch { /* temp cleanup only */ }
            }
        }

        /// <summary>
        /// Default geoprocessing environment for MCP-driven runs. Enables
        /// overwrite — programmatic invocation is idempotent-friendly and
        /// ERROR 000210 (output already exists) is an unhelpful failure
        /// mode when the whole point is repeatable automation.
        ///
        /// Also pins workspace + scratchWorkspace to the project's default
        /// GDB. The ribbon Run dialog applies these by default; ExecuteToolAsync
        /// from an Add-In does NOT, which causes ModelBuilder models whose
        /// intermediate outputs are derived (no explicit path) to fail
        /// pre-validation with ERROR 000735 ("Value is required") on every
        /// step's out_dataset — the GP engine cannot resolve where to place
        /// the derived output. Pinning both env vars gives derived outputs
        /// somewhere to land, mirroring GUI behavior.
        ///
        /// NOTE: MakeEnvironmentArray is a named-argument method (every GP
        /// env is a separate parameter); passing a Dictionary as a positional
        /// arg binds it to `workspace`, producing a cryptic runtime binder
        /// error. Use named-argument syntax.
        /// </summary>
        private static IReadOnlyList<KeyValuePair<string, string>> DefaultRunEnvironments()
        {
            string? defaultGdb = null;
            try { defaultGdb = Project.Current?.DefaultGeodatabasePath; }
            catch { /* no open project — fall through to env without workspace */ }

            return !string.IsNullOrEmpty(defaultGdb)
                ? Geoprocessing.MakeEnvironmentArray(
                    overwriteoutput: true,
                    workspace: defaultGdb,
                    scratchWorkspace: defaultGdb)
                : Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);
        }

        /// <summary>
        /// Substitutes ModelBuilder <c>%VarName%</c> patterns in a literal
        /// value with the runtime value of that variable. ModelBuilder's own
        /// engine performs this string substitution before handing expressions
        /// to arcpy (most commonly on <c>CalculateField.expression</c> and
        /// SQL where-clauses); our step-by-step executor must do the same or
        /// arcpy sees the literal <c>%Foo%</c> and treats it as Python /
        /// invalid SQL, producing ERROR 000539 (SyntaxError) or similar.
        ///
        /// Variable lookup is case-insensitive by name. Unresolved patterns
        /// are left in place; the resulting GP error will surface the missing
        /// variable name to the caller.
        /// </summary>
        private static string SubstituteModelVars(
            string literal,
            ModelGraph graph,
            Dictionary<string, string> runtimeValues)
        {
            if (string.IsNullOrEmpty(literal) || !literal.Contains('%'))
                return literal;
            // [^%]+ rather than an identifier pattern: ModelBuilder variable
            // names routinely contain spaces ("Output Features"), parentheses,
            // and other punctuation — %Output Features% must still substitute.
            // Names that don't match any variable are left in place unchanged,
            // so SQL wildcards like 'LIKE ''%foo%''' survive: the inner text
            // only gets replaced when it exactly matches a variable name.
            return System.Text.RegularExpressions.Regex.Replace(
                literal,
                @"%([^%]+)%",
                match =>
                {
                    var varName = match.Groups[1].Value;
                    // ModelBuilder writes %Display Name% (the variable's label,
                    // spaces and all) while v.Name carries the underscored
                    // param_name — match label, name, and the space/underscore-
                    // normalized form of each so "%Output Workspace%" finds
                    // the "Output_Workspace" parameter.
                    static string Norm(string? s) =>
                        (s ?? "").Trim().Replace(' ', '_');
                    var wanted = Norm(varName);
                    foreach (var v in graph.Variables.Values)
                    {
                        bool hit =
                            string.Equals(v.Name, varName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(v.DisplayName, varName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Norm(v.Name), wanted, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Norm(v.DisplayName), wanted, StringComparison.OrdinalIgnoreCase);
                        if (!hit) continue;
                        if (runtimeValues.TryGetValue(v.Id, out var val)
                            && !string.IsNullOrEmpty(val))
                            return val;
                        // Fall back to the variable's stored value (model
                        // parameter default) if the runtime map doesn't carry it.
                        if (!string.IsNullOrEmpty(v.StoredValue))
                            return v.StoredValue;
                    }
                    return match.Value;
                });
        }

        /// <summary>
        /// Sanitizes a ModelBuilder variable name for use as a File Geodatabase
        /// feature class or table name. GDB names must start with a letter and
        /// contain only letters, digits, and underscores. ModelBuilder lets you
        /// name a variable anything (including spaces, dashes, and other
        /// punctuation), and when the executor uses that name to derive an
        /// output path it would otherwise produce ERROR 000354 ("The name
        /// contains invalid characters") on the first step whose output
        /// variable wasn't typed to be GDB-safe.
        ///
        /// Replaces invalid characters with underscores, collapses runs of
        /// underscores, and prefixes a letter if the result would otherwise
        /// start with a digit. Empty/null input falls back to "out".
        /// </summary>
        private static string SanitizeGdbName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "out";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            // Collapse runs of underscores
            var collapsed = System.Text.RegularExpressions.Regex
                .Replace(sb.ToString(), "_+", "_")
                .Trim('_');
            if (string.IsNullOrEmpty(collapsed)) return "out";
            // Must start with a letter
            return char.IsLetter(collapsed[0]) ? collapsed : "x_" + collapsed;
        }

        /// <summary>
        /// Runs an arbitrary geoprocessing tool by name (e.g., <c>analysis.Buffer</c>,
        /// <c>management.AddField</c>). Parameters arrive as a JSON array; each element
        /// passes through <see cref="FlattenGpParam"/>, which recursively flattens
        /// two-level <see cref="JsonArray"/>s into arcpy's value-table string syntax
        /// (<c>"f1 v1;f2 v2"</c>). That's the F7 fix that lets value-table-taking GP
        /// tools (CalculateGeometryAttributes, JoinField, SpatialJoin field-map) work
        /// over MCP without callers having to pre-stringify their inputs.
        /// </summary>
        private static async Task<IpcResponse> HandleRunGPTool(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("tool", out string? toolName) ||
                string.IsNullOrWhiteSpace(toolName) ||
                !args.TryGetValue("parameters", out string? paramsJson) ||
                string.IsNullOrWhiteSpace(paramsJson))
                return new(false, "args 'tool' & 'parameters' required", null);

            var paramValues = new List<object>();
            var paramsNode = JsonNode.Parse(paramsJson)?.AsArray();
            if (paramsNode != null)
            {
                foreach (var p in paramsNode)
                    paramValues.Add(FlattenGpParam(p));
            }

            // Optional per-call environment overrides merged over the defaults:
            // {"extent": "...", "outputCoordinateSystem": "...", "cellSize": "30",
            //  "mask": "StudyArea", "parallelProcessingFactor": "75%"} etc.
            // Keys pass through verbatim as GP env names.
            IEnumerable<KeyValuePair<string, string>> env = DefaultRunEnvironments();
            if (args.TryGetValue("environments", out string? envJson) && !string.IsNullOrWhiteSpace(envJson))
            {
                try
                {
                    var envNode = JsonNode.Parse(envJson)?.AsObject()
                        ?? throw new InvalidOperationException("environments must be a JSON object");
                    var merged = new List<KeyValuePair<string, string>>(DefaultRunEnvironments());
                    foreach (var kv in envNode)
                    {
                        var val = CoerceScalarToString(kv.Value);
                        if (string.IsNullOrEmpty(val)) continue;
                        merged.RemoveAll(e => e.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                        merged.Add(new KeyValuePair<string, string>(kv.Key, val));
                    }
                    env = merged;
                }
                catch (Exception ex)
                {
                    return new(false, $"Invalid environments JSON: {ex.Message}", null);
                }
            }

            var valueArray = Geoprocessing.MakeValueArray(paramValues.ToArray());
            var result = await Geoprocessing.ExecuteToolAsync(toolName, valueArray, env);

            if (result.IsFailed)
            {
                var errorTexts = result.Messages
                    .Where(m => m.Type == GPMessageType.Error)
                    .Select(m => m.Text)
                    .ToList();

                var messages = result.Messages.Any()
                    ? string.Join("; ", result.Messages.Select(m => m.Text))
                    : errorTexts.Any()
                        ? string.Join("; ", errorTexts)
                        : $"arcpy reported failure with no messages — tool='{toolName}', check tool name and parameters";

                return new(false, $"GP tool failed: {messages}", null);
            }

            var outputMessages = result.Messages.Select(m => new { type = m.Type.ToString(), text = m.Text }).ToList();

            // Surface the tool's output values so the agent knows where results
            // landed (derived output paths, counts from GetCount, etc.) without
            // having to parse them out of message text.
            List<string>? outputValues = null;
            try
            {
                outputValues = result.Values?.Where(v => !string.IsNullOrEmpty(v)).ToList();
                if (outputValues is { Count: 0 }) outputValues = null;
            }
            catch { /* some tools expose no values — fine */ }

            return new(true, null, new
            {
                success = true,
                returnValue = result.ReturnValue,
                outputs = outputValues,
                messages = outputMessages
            });
        }

        // ─── Logging ────────────────────────────────────────────────────────

        /// <summary>
        /// Writes a full exception record (type, message, stack trace) to mcp-bridge.log
        /// in the active project's home folder, with a temp-dir fallback. Best-effort —
        /// any failure here is swallowed to keep the IPC loop alive.
        /// </summary>
        private static void LogException(IpcRequest req, Exception ex)
        {
            try
            {
                string dir;
                try { dir = Project.Current?.HomeFolderPath ?? Path.GetTempPath(); }
                catch { dir = Path.GetTempPath(); }

                var logPath = Path.Combine(dir, "mcp-bridge.log");
                var argsPreview = req.Args == null
                    ? "<none>"
                    : string.Join(", ", req.Args.Select(kv =>
                        $"{kv.Key}={Truncate(kv.Value, 200)}"));

                var entry = $"[{DateTime.UtcNow:O}] op={req.Op} args=[{argsPreview}]\n{ex}\n\n";
                File.AppendAllText(logPath, entry);
            }
            catch { /* best effort — never break the IPC loop to log */ }
        }

        /// <summary>
        /// Writes a non-success response (Ok=false, with its error text) to mcp-bridge.log.
        /// Mirrors LogException so handlers that return structured `{success:false}` instead
        /// of throwing still leave an audit trail. Best-effort — swallowed to keep the IPC loop alive.
        /// </summary>
        private static void LogNonSuccess(IpcRequest req, string? error)
        {
            try
            {
                string dir;
                try { dir = Project.Current?.HomeFolderPath ?? Path.GetTempPath(); }
                catch { dir = Path.GetTempPath(); }

                var logPath = Path.Combine(dir, "mcp-bridge.log");
                var argsPreview = req.Args == null
                    ? "<none>"
                    : string.Join(", ", req.Args.Select(kv =>
                        $"{kv.Key}={Truncate(kv.Value, 200)}"));

                var entry = $"[{DateTime.UtcNow:O}] op={req.Op} args=[{argsPreview}] RESPONSE_NOT_OK error={Truncate(error, 500)}\n\n";
                File.AppendAllText(logPath, entry);
            }
            catch { /* best effort — never break the IPC loop to log */ }
        }

        /// <summary>
        /// Converts any JSON scalar to its GP-friendly string form. LLM callers
        /// routinely emit JSON numbers and booleans where arcpy expects strings
        /// (<c>[..., 100, true]</c>); <c>JsonValue.GetValue&lt;string&gt;()</c>
        /// throws InvalidOperationException on those, killing the whole op.
        /// Numbers/bools serialize to their raw literal ("100", "true"), which
        /// is exactly what arcpy's value parser wants.
        /// </summary>
        internal static string CoerceScalarToString(JsonNode? node)
        {
            if (node is null) return "";
            if (node is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString(); // number → "100", bool → "true"
            }
            return node.ToJsonString();
        }

        private static string FlattenGpParam(JsonNode? node)
        {
            if (node is null) return "";
            if (node is JsonValue) return CoerceScalarToString(node);
            if (node is JsonArray arr)
            {
                return string.Join(";", arr.Select(row =>
                    row is JsonArray inner
                        ? string.Join(" ", inner.Select(CoerceScalarToString))
                        : CoerceScalarToString(row)));
            }
            return node.ToJsonString();
        }

        private static string Truncate(string? s, int max) =>
            s == null ? "<null>" : s.Length <= max ? s : s[..max] + $"…(+{s.Length - max})";
    }
}
