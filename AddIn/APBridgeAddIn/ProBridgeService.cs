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
    internal class ProBridgeService : IDisposable
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

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName,
                        PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break; // Clean shutdown
                }
                catch (Exception)
                {
                    // Pipe broke or other transient error — restart the listener.
                    // Small delay prevents tight spin if errors repeat.
                    try { await Task.Delay(100, ct); } catch { break; }
                }
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
                        var member = FindMapMemberByName(map, layerName)
                            ?? throw new InvalidOperationException($"Layer or table not found: {layerName}");
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
                        var fl = MapView.Active?.Map?.GetLayersAsFlattenedList()
                            .OfType<FeatureLayer>()
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                        if (fl == null)
                            throw new InvalidOperationException($"Layer not found: {layerName}");
                        await MapView.Active!.ZoomToAsync(fl);
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
                        var member = FindMapMemberByName(map, layerName)
                            ?? throw new InvalidOperationException($"Layer or table not found: {layerName}");
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
                        var member = FindMapMemberByName(map, lfLayerName)
                            ?? throw new InvalidOperationException($"Layer or table not found: {lfLayerName}");

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
                        var member = FindMapMemberByName(map, lpLayerName)
                            ?? throw new InvalidOperationException($"Layer or table not found: {lpLayerName}");

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
                        var member = FindMapMemberByName(map, raLayerName)
                            ?? throw new InvalidOperationException($"Layer or table not found: {raLayerName}");

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
                        var member = FindMapMemberByName(map, gsfLayerName)
                            ?? throw new InvalidOperationException($"Layer or table not found: {gsfLayerName}");

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

                case "pro.runModel":
                    return await HandleRunModel(req.Args);

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
            var mapItem = Project.Current.GetItems<MapProjectItem>()
                .FirstOrDefault(m => m.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Map not found: {mapName}");
            return mapItem.GetMap();
        }

        /// <summary>
        /// Find a MapMember (Layer or StandaloneTable) by name in a Map. Searches
        /// flattened layer tree (descending into group layers) AND standalone
        /// tables. Returns null if not found. Case-insensitive name match.
        /// First match wins — for duplicate names across groups, layer order in
        /// the TOC determines priority.
        /// </summary>
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
                    var member = FindMapMemberByName(map, layerName)
                        ?? throw new InvalidOperationException($"Layer or table not found: {layerName}");
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

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null)
                    return new(false, "No active map view", null);

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

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null)
                    return new(false, "No active map view", null);

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
            var toolboxes = await QueuedTask.Run(() =>
            {
                var items = Project.Current.GetItems<GeoprocessingProjectItem>();
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

            AtbxManager.CreateToolbox(path, tbxName);

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
            return new(true, null, new { modelName, toolboxPath = path, created = true });
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
        /// Declared parameter signatures for common system GP tools, keyed by
        /// <c>"toolboxAlias.toolName"</c>. Order matches each tool's positional
        /// signature in the ArcGIS Pro 3.x docs. When the step-by-step model
        /// executor encounters one of these tools, it walks this list in order
        /// and fills each position by looking up the slot name in the process's
        /// stored params (using arcpy's <c>"#"</c> sentinel for slots the model
        /// omits, so the GP engine uses each tool's declared default).
        ///
        /// Without this name-to-position mapping, the executor packs values
        /// densely in JSON insertion order — which silently corrupts the call
        /// when a model omits an optional slot before an included one. Example:
        /// <c>management.Project</c> with <c>{in_dataset, out_dataset, out_coor_system,
        /// preserve_shape, vertical}</c> in JSON order dense-packs as positions
        /// 0..4, but the real signature has <c>transform_method</c> at slot 3 and
        /// <c>in_coor_system</c> at slot 4, so <c>"false"</c> from preserve_shape
        /// lands in transform_method (→ ERROR 000365) and <c>"false"</c> from
        /// vertical lands in in_coor_system (→ WARNING 230002).
        ///
        /// Pro SDK exposes no introspection API for tool signatures — the docs
        /// just say "look it up". Extend this dictionary as new tools are
        /// encountered in real models. For tools not listed here, the executor
        /// falls back to dense-packing (the old behavior) and the resulting
        /// shift will surface as misnamed-slot errors that pinpoint the tool.
        /// </summary>
        private static readonly Dictionary<string, string[]> GpToolSignatures =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["management.Project"] = new[]
                {
                    "in_dataset", "out_dataset", "out_coor_system",
                    "transform_method", "in_coor_system",
                    "preserve_shape", "max_deviation", "vertical"
                },
                ["management.CopyFeatures"] = new[]
                {
                    "in_features", "out_feature_class",
                    "config_keyword", "spatial_grid_1", "spatial_grid_2", "spatial_grid_3"
                },
                ["analysis.PairwiseErase"] = new[]
                {
                    "in_features", "erase_features", "out_feature_class", "cluster_tolerance"
                },
                ["analysis.SummarizeWithin"] = new[]
                {
                    "in_polygons", "in_sum_features", "out_feature_class",
                    "keep_all_polygons", "sum_fields", "sum_shape", "shape_unit",
                    "group_field", "add_min_maj", "add_group_percent", "out_group_table"
                },
                ["management.SelectLayerByLocation"] = new[]
                {
                    "in_layer", "overlap_type", "select_features",
                    "search_distance", "selection_type", "invert_spatial_relationship"
                },
                ["management.CalculateField"] = new[]
                {
                    "in_table", "field", "expression", "expression_type",
                    "code_block", "field_type", "enforce_domains"
                },
                ["management.JoinField"] = new[]
                {
                    "in_data", "in_field", "join_table", "join_field",
                    "fields", "fm_option", "field_mapping", "index_join_fields"
                },
                ["management.AddField"] = new[]
                {
                    "in_table", "field_name", "field_type",
                    "field_precision", "field_scale", "field_length", "field_alias",
                    "field_is_nullable", "field_is_required", "field_domain"
                },
                ["analysis.Buffer"] = new[]
                {
                    "in_features", "out_feature_class", "buffer_distance_or_field",
                    "line_side", "line_end_type", "dissolve_option", "dissolve_field", "method"
                },
                ["analysis.Clip"] = new[]
                {
                    "in_features", "clip_features", "out_feature_class", "cluster_tolerance"
                },
                ["analysis.Intersect"] = new[]
                {
                    "in_features", "out_feature_class", "join_attributes",
                    "cluster_tolerance", "output_type"
                },
            };

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
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName))
                return new(false, "args 'toolboxPath' & 'modelName' required", null);

            // Step-by-step execution. Calling ExecuteToolAsync on the model as a
            // whole tool triggers Pro's chain pre-validation, which rejects any
            // intermediate INPUT whose producing tool has not yet created the FC
            // on disk — fatal on first run. The ribbon Run dialog avoids this by
            // running ModelBuilder's own engine: each process is validated JIT
            // immediately before it executes, after its upstream outputs already
            // exist. We mirror that here by parsing the model graph, topologically
            // sorting processes, and calling ExecuteToolAsync once per step with
            // refs resolved against a runtime variable map.
            ModelGraph graph;
            try
            {
                graph = AtbxManager.WalkModel(path, modelName);
            }
            catch (Exception ex)
            {
                return new(false, $"Failed to read model from '{path}': {ex.Message}", null);
            }

            // Reject iterators / nested sub-models — step-by-step semantics don't
            // apply. Agents needing iteration should compose run_gp_tool calls.
            var iterator = graph.Processes.FirstOrDefault(p => p.IsIterator);
            if (iterator != null)
            {
                return new(false,
                    $"Model '{modelName}' contains iterator or nested model '{iterator.Name}' " +
                    $"(tool '{iterator.Tool}'). Step-by-step execution doesn't support these yet — " +
                    $"compose run_gp_tool calls instead.",
                    null);
            }

            // Collect user-supplied named values (case-insensitive matching).
            var namedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args.TryGetValue("parameters", out string? paramsJson) && !string.IsNullOrWhiteSpace(paramsJson))
            {
                var paramsNode = JsonNode.Parse(paramsJson)?.AsObject();
                if (paramsNode != null)
                {
                    foreach (var kv in paramsNode)
                        namedValues[kv.Key] = kv.Value?.GetValue<string>() ?? "";
                }
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
                return new(false,
                    $"Unknown model parameter(s): {string.Join(", ", unknownKeys)}. " +
                    $"Model '{modelName}' expects: [{string.Join(", ", inputParamNames)}]",
                    null);
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
                    runtimeValues[v.Id] = v.StoredValue;
            }

            // Workspace for generating derived-output paths. Same source as
            // DefaultRunEnvironments, but we need the path string directly to
            // build per-step output paths upfront (so downstream refs resolve).
            string scratchGdb;
            try { scratchGdb = Project.Current?.DefaultGeodatabasePath ?? ""; }
            catch { scratchGdb = ""; }

            var env = DefaultRunEnvironments();
            var allMessages = new List<object>();

            foreach (var proc in graph.Processes)
            {
                // Resolve each slot in JSON-insertion order. Pro empirically writes
                // process params in tool-declared slot order (per Desktop's data on
                // SummarizeWithin: in_polygons, in_sum_features, out_feature_class,
                // keep_all_polygons, sum_fields, sum_shape, shape_unit). Trusting
                // that order produces the positional value array ExecuteToolAsync
                // expects.
                // Build positional value array. Two strategies:
                //
                //   1) Known tool: walk GpToolSignatures[proc.Tool] in declared order
                //      and fill each position from proc.Params by slot NAME. For slots
                //      the model omitted (sparse storage), insert "#" so arcpy uses
                //      the tool's declared default. This is the correct contract for
                //      GP system tools.
                //
                //   2) Unknown tool: fall back to dense-packing by JSON insertion
                //      order. Same as the old behavior — wrong for any tool that
                //      omits optional slots before included ones, but the resulting
                //      misalignment surfaces as obvious slot-mismatch errors that
                //      point at which tool to add to the signature table.
                var slotOrder = GpToolSignatures.TryGetValue(proc.Tool, out var sig)
                    ? sig.AsEnumerable()
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
                if (sig != null)
                {
                    foreach (var (slotName, pm) in proc.Params)
                    {
                        if (pm.OutputVariableId == null) continue;
                        if (sig.Contains(slotName, StringComparer.OrdinalIgnoreCase)) continue;
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
                                ? ov.Name
                                : $"output_{pm.OutputVariableId}";
                            outPath = string.IsNullOrEmpty(scratchGdb)
                                ? varName
                                : $"{scratchGdb}\\{varName}";
                            runtimeValues[pm.OutputVariableId] = outPath;
                        }
                        values.Add(outPath);
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
                        values.Add(pm.LiteralValue);
                    }
                    else
                    {
                        values.Add("");
                    }
                }

                var valueArray = Geoprocessing.MakeValueArray(values.ToArray());
                var stepResult = await Geoprocessing.ExecuteToolAsync(proc.Tool, valueArray, env);

                if (stepResult.IsFailed)
                {
                    var msgs = stepResult.Messages.Any()
                        ? string.Join("; ", stepResult.Messages.Select(m => $"{m.Type}: {m.Text}"))
                        : "arcpy reported failure with no messages";
                    return new(false,
                        $"Step '{proc.Name}' ({proc.Tool}) failed: {msgs}",
                        new { failedStep = proc.Name, tool = proc.Tool, completedSteps = allMessages.Count });
                }

                foreach (var m in stepResult.Messages)
                    allMessages.Add(new { step = proc.Name, type = m.Type.ToString(), text = m.Text });
            }

            return new(true, null, new
            {
                success = true,
                stepsRun = graph.Processes.Count,
                messages = allMessages
            });
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

            var valueArray = Geoprocessing.MakeValueArray(paramValues.ToArray());
            var result = await Geoprocessing.ExecuteToolAsync(toolName, valueArray, DefaultRunEnvironments());

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
            return new(true, null, new { success = true, messages = outputMessages });
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

        private static string FlattenGpParam(JsonNode? node)
        {
            if (node is null) return "";
            if (node is JsonValue v) return v.GetValue<string>();
            if (node is JsonArray arr)
            {
                return string.Join(";", arr.Select(row =>
                    row is JsonArray inner
                        ? string.Join(" ", inner.Select(t => t?.GetValue<string>() ?? ""))
                        : (row?.GetValue<string>() ?? "")));
            }
            return node.ToJsonString();
        }

        private static string Truncate(string? s, int max) =>
            s == null ? "<null>" : s.Length <= max ? s : s[..max] + $"…(+{s.Length - max})";
    }
}
