using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.GeoProcessing;
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

        private static Task SendAsync(StreamWriter w, IpcResponse resp)
            => w.WriteLineAsync(JsonSerializer.Serialize(resp));

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
                        if (fl == null) return 0;
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
                        if (fl != null)
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

                case "pro.exportLayer":
                    return await HandleExportLayer(req.Args);

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
                return new
                {
                    xmin = ext.XMin,
                    ymin = ext.YMin,
                    xmax = ext.XMax,
                    ymax = ext.YMax,
                    width = ext.Width,
                    height = ext.Height,
                    spatialReferenceWkid = ext.SpatialReference?.Wkid ?? 0,
                    spatialReferenceName = ext.SpatialReference?.Name
                };
            });

            if (extent == null)
                return new(false, "No active map view", null);
            return new(true, null, extent);
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
                var messages = string.Join("; ", result.Messages.Select(m => m.Text));
                return new(false, $"Model execution failed: {messages}", null);
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
                    paramValues.Add(p?.GetValue<string>() ?? "");
            }

            var valueArray = Geoprocessing.MakeValueArray(paramValues.ToArray());
            var result = await Geoprocessing.ExecuteToolAsync(toolName, valueArray, DefaultRunEnvironments());

            if (result.IsFailed)
            {
                var messages = string.Join("; ", result.Messages.Select(m => m.Text));
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

        private static string Truncate(string? s, int max) =>
            s == null ? "<null>" : s.Length <= max ? s : s[..max] + $"…(+{s.Length - max})";
    }
}
