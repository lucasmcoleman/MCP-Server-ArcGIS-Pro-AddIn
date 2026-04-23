using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.GeoProcessing;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using APBridgeAddIn.ModelBuilder;
using System;
using System.Collections.Generic;
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

        private static async Task<IpcResponse> HandleAsync(IpcRequest req, CancellationToken ct)
        {
            switch (req.Op)
            {
                // ─── Existing Map Operations ────────────────────────────────
                case "pro.getActiveMapName":
                    var name = MapView.Active?.Map?.Name ?? "<none>";
                    return new(true, null, new { name });

                case "pro.listLayers":
                    var layers = await QueuedTask.Run(() =>
                        MapView.Active?.Map?.Layers.Select(l => l.Name).ToList()
                        ?? new List<string>());
                    return new(true, null, layers);

                case "pro.countFeatures":
                {
                    if (req.Args == null ||
                        !req.Args.TryGetValue("layer", out string? layerName) ||
                        string.IsNullOrWhiteSpace(layerName))
                        return new(false, "arg 'layer' required", null);

                    int count = await QueuedTask.Run(() =>
                    {
                        var fl = MapView.Active?.Map?.Layers
                            .OfType<FeatureLayer>()
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                        if (fl == null) throw new InvalidOperationException($"Layer not found: {layerName}");
                        using var fc = fl.GetFeatureClass();
                        return (int)fc.GetCount();
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
                        var fl = MapView.Active?.Map?.Layers
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

                    var selectionInfo = await QueuedTask.Run<object?>(() =>
                    {
                        var fl = MapView.Active?.Map?.Layers
                            .OfType<FeatureLayer>()
                            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                        if (fl == null) return null;
                        var sel = fl.Select(new ArcGIS.Core.Data.QueryFilter { WhereClause = where });
                        return new { layer = fl.Name, selectedCount = sel?.GetCount() ?? 0 };
                    });

                    if (selectionInfo == null)
                        return new(false, $"Layer not found: {layerName}", null);
                    return new(true, null, selectionInfo);
                }

                case "pro.getCurrentExtent":
                    return await HandleGetCurrentExtent();

                case "pro.getViewDiagnostics":
                    return await HandleGetViewDiagnostics();

                case "pro.clearSelection":
                    return await HandleClearSelection(req.Args);

                case "pro.getProjectInfo":
                    return await HandleGetProjectInfo();

                case "pro.exportLayer":
                    return await HandleExportLayer(req.Args);

                // ─── Project Operations ─────────────────────────────────────
                case "pro.createProject":
                    return await HandleCreateProject(req.Args);

                case "pro.openProject":
                    return await HandleOpenProject(req.Args);

                // ─── Layer-from-URL ─────────────────────────────────────────
                case "pro.addLayerFromUrl":
                    return await HandleAddLayerFromUrl(req.Args);

                // ─── Layout Operations ──────────────────────────────────────
                case "pro.listLayouts":
                    return await HandleListLayouts();

                case "pro.openLayout":
                    return await HandleOpenLayout(req.Args);

                case "pro.listLayoutElements":
                    return await HandleListLayoutElements(req.Args);

                case "pro.setLayoutText":
                    return await HandleSetLayoutText(req.Args);

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

                default:
                    return new(false, $"op not found: {req.Op}", null);
            }
        }

        // ─── Map/Layer Handler Methods ───────────────────────────────────────

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
            args?.TryGetValue("layer", out layerName);

            var result = await QueuedTask.Run<(bool ok, string? error, int cleared, string? layerCleared)>(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return (false, "No active map view", 0, null);

                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    var fl = map.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    if (fl == null)
                        throw new InvalidOperationException($"Layer not found: {layerName}");
                    fl.ClearSelection();
                    return (true, null, 1, fl.Name);
                }

                var featureLayers = map.Layers.OfType<FeatureLayer>().ToList();
                foreach (var fl in featureLayers)
                    fl.ClearSelection();
                return (true, null, featureLayers.Count, null);
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
                MapView.Active?.Map?.Layers
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

            try { if (Project.Current != null) await Project.Current.SaveAsync(); }
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

        private static async Task<IpcResponse> HandleOpenProject(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("path", out string? path) ||
                string.IsNullOrWhiteSpace(path))
                return new(false, "arg 'path' required", null);

            if (!File.Exists(path))
                return new(false, $"Project file not found: {path}", null);

            try { if (Project.Current != null) await Project.Current.SaveAsync(); }
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
                int.TryParse(res, out var r) && r > 0)
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

        private static async Task<IpcResponse> HandleRunModel(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("toolboxPath", out string? path) ||
                string.IsNullOrWhiteSpace(path) ||
                !args.TryGetValue("modelName", out string? modelName) ||
                string.IsNullOrWhiteSpace(modelName))
                return new(false, "args 'toolboxPath' & 'modelName' required", null);

            // Build the tool path: "toolboxPath\modelName"
            var toolPath = $"{path}\\{modelName}";

            // Collect parameter values from args (everything except toolboxPath and modelName)
            var paramValues = new List<string>();
            if (args.TryGetValue("parameters", out string? paramsJson) && !string.IsNullOrWhiteSpace(paramsJson))
            {
                var paramsNode = JsonNode.Parse(paramsJson)?.AsObject();
                if (paramsNode != null)
                {
                    foreach (var kv in paramsNode)
                        paramValues.Add(kv.Value?.GetValue<string>() ?? "");
                }
            }

            var valueArray = Geoprocessing.MakeValueArray(paramValues.ToArray());
            var result = await Geoprocessing.ExecuteToolAsync(toolPath, valueArray, DefaultRunEnvironments());

            if (result.IsFailed)
            {
                var errorTexts = result.Messages
                    .Where(m => m.Type == GPMessageType.Error)
                    .Select(m => m.Text)
                    .ToList();

                var msgs = result.Messages.Any()
                    ? string.Join("; ", result.Messages.Select(m => m.Text))
                    : errorTexts.Any()
                        ? string.Join("; ", errorTexts)
                        : "arcpy reported failure with no messages — check parameters";

                return new(false, $"Model execution failed: {msgs}", null);
            }

            var outputMessages = result.Messages.Select(m => new { type = m.Type.ToString(), text = m.Text }).ToList();
            return new(true, null, new { success = true, messages = outputMessages });
        }

        /// <summary>
        /// Default geoprocessing environment for MCP-driven runs. Enables
        /// overwrite — programmatic invocation is idempotent-friendly and
        /// ERROR 000210 (output already exists) is an unhelpful failure
        /// mode when the whole point is repeatable automation.
        ///
        /// NOTE: MakeEnvironmentArray is a named-argument method (every GP
        /// env is a separate parameter); passing a Dictionary as a positional
        /// arg binds it to `workspace`, producing a cryptic runtime binder
        /// error. Use named-argument syntax.
        /// </summary>
        private static IReadOnlyList<KeyValuePair<string, string>> DefaultRunEnvironments() =>
            Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);

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
