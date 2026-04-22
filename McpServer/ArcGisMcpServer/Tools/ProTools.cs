using ArcGisMcpServer.Ipc;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ArcGisMcpServer.Tools
{

    [McpServerToolType]
    public static class ProTools
    {
        private static BridgeClient? _client;
        public static void Configure(BridgeClient client) => _client = client;

        // ─── Existing Map Tools ──────────────────────────────────────────

        [McpServerTool, Description("Name of the active map in ArcGIS Pro")]
        public static async Task<string> GetActiveMapName()
        {
            var r = await _client!.OpAsync("pro.getActiveMapName");
            if (!r.Ok) throw new Exception(r.Error);
            return ((JsonElement)r.Data!).GetProperty("name").GetString()!;
        }

        [McpServerTool, Description("List of layers in the active map")]
        public static async Task<List<string>> ListLayers()
        {
            var r = await _client!.OpAsync("pro.listLayers");
            if (!r.Ok) throw new Exception(r.Error);
            return JsonSerializer.Deserialize<List<string>>(r.Data!.ToString()!)!;
        }

        [McpServerTool, Description("Count features in a layer by name")]
        public static async Task<int> CountFeatures(string layer)
        {
            var r = await _client!.OpAsync("pro.countFeatures", new() { ["layer"] = layer });
            if (!r.Ok) throw new Exception(r.Error);
            return ((JsonElement)r.Data!).GetProperty("count").GetInt32();
        }

        [McpServerTool, Description("Zoom to a layer's extent by name")]
        public static async Task<bool> ZoomToLayer(string layer)
        {
            var r = await _client!.OpAsync("pro.zoomToLayer", new() { ["layer"] = layer });
            if (!r.Ok) throw new Exception(r.Error);
            return true;
        }

        [McpServerTool, Description("Ping test to validate the MCP server (without depending on ArcGIS Pro)")]
        public static Task<string> Ping()
        {
            return Task.FromResult($"pong {DateTimeOffset.UtcNow:O}");
        }

        [McpServerTool, Description("MCP echo test")]
        public static string Echo(string text)
        {
            return $"echo: {text}";
        }

        // ─── ModelBuilder Tools ──────────────────────────────────────────

        [McpServerTool, Description(
            "List all toolboxes (.atbx) in the current ArcGIS Pro project. " +
            "Returns name and file path for each toolbox.")]
        public static async Task<string> ListToolboxes()
        {
            var r = await _client!.OpAsync("pro.listToolboxes");
            return FormatResult(r, "pro.listToolboxes");
        }

        [McpServerTool, Description(
            "List all tools (models and scripts) in a specific toolbox. " +
            "Returns the name and type (Model/Script) of each tool.")]
        public static async Task<string> ListModels(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath)
        {
            var r = await _client!.OpAsync("pro.listModels", new() { ["toolboxPath"] = toolboxPath });
            return FormatResult(r, "pro.listModels");
        }

        [McpServerTool, Description(
            "Get the full definition of a ModelBuilder model, including all input parameters, " +
            "processing steps (geoprocessing tools), and data connections between them. " +
            "The definition uses a simplified JSON format where: " +
            "'inputs' lists model parameters with name/type/default, and " +
            "'steps' lists geoprocessing operations with their tool name and parameter connections. " +
            "Parameter connections use {\"ref\": \"name\"} to reference inputs or previous step outputs, " +
            "and {\"output\": \"name\", \"type\": \"datatype\"} to declare step outputs.")]
        public static async Task<string> DescribeModel(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("Name of the model within the toolbox")] string modelName)
        {
            var r = await _client!.OpAsync("pro.describeModel", new()
            {
                ["toolboxPath"] = toolboxPath,
                ["modelName"] = modelName
            });
            return FormatResult(r, "pro.describeModel");
        }

        [McpServerTool, Description(
            "Create a new empty toolbox (.atbx) file. " +
            "If no path is specified, creates it in the project home folder.")]
        public static async Task<string> CreateToolbox(
            [Description("Display name for the new toolbox")] string name,
            [Description("Optional: full file path where the .atbx file should be created. " +
                "If omitted, uses the project home folder.")] string? path = null)
        {
            var args = new Dictionary<string, string> { ["name"] = name };
            if (!string.IsNullOrWhiteSpace(path))
                args["path"] = path;

            var r = await _client!.OpAsync("pro.createToolbox", args);
            return FormatResult(r, "pro.createToolbox");
        }

        [McpServerTool, Description(
            "Create a new ModelBuilder model in a toolbox from a JSON definition. " +
            "The definition must include: " +
            "- 'name': string - the model name (no spaces, alphanumeric + underscores) " +
            "- 'description': string - what the model does " +
            "- 'inputs': array of {name, type, description, default?} - model parameters " +
            "- 'steps': array of processing steps, each with: " +
            "  - 'name': display name for the step " +
            "  - 'tool': geoprocessing tool name (e.g., 'analysis.Buffer', 'sa.Reclassify') " +
            "  - 'parameters': object mapping param names to either: " +
            "    - {\"ref\": \"InputName\"} to connect to an input or previous output " +
            "    - {\"output\": \"OutputName\", \"type\": \"DEFeatureClass\"} to declare an output " +
            "    - \"literal value\" for constant values " +
            "Common GP tool categories: analysis (overlay, proximity), conversion, " +
            "management (fields, joins), sa (spatial analyst - raster), na (network analyst). " +
            "Common data types: GPFeatureLayer, DERasterDataset, DEFeatureClass, GPString, " +
            "GPLong, GPDouble, GPLinearUnit, GPSQLExpression, Field.")]
        public static async Task<string> CreateModel(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("JSON model definition with name, description, inputs, and steps")] string definition)
        {
            var r = await _client!.OpAsync("pro.createModel", new()
            {
                ["toolboxPath"] = toolboxPath,
                ["definition"] = definition
            });
            return FormatResult(r, "pro.createModel");
        }

        [McpServerTool, Description(
            "Update an existing model's definition. Replaces the model's workflow entirely " +
            "with the new definition. Use DescribeModel first to get the current definition, " +
            "modify it, then pass the updated JSON here. The definition format is the same " +
            "as CreateModel.")]
        public static async Task<string> UpdateModel(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("Name of the existing model to update")] string modelName,
            [Description("Updated JSON model definition")] string definition)
        {
            var r = await _client!.OpAsync("pro.updateModel", new()
            {
                ["toolboxPath"] = toolboxPath,
                ["modelName"] = modelName,
                ["definition"] = definition
            });
            return FormatResult(r, "pro.updateModel");
        }

        [McpServerTool, Description(
            "Run a ModelBuilder model with specified parameter values. " +
            "Use DescribeModel first to see what parameters the model expects.")]
        public static async Task<string> RunModel(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("Name of the model to run")] string modelName,
            [Description("Optional: JSON object mapping parameter names to values, " +
                "e.g., {\"StudyArea\": \"Counties\", \"BufferDistance\": \"1000 Meters\"}")] string? parameters = null)
        {
            var args = new Dictionary<string, string>
            {
                ["toolboxPath"] = toolboxPath,
                ["modelName"] = modelName
            };
            if (!string.IsNullOrWhiteSpace(parameters))
                args["parameters"] = parameters;

            var r = await _client!.OpAsync("pro.runModel", args);
            return FormatResult(r, "pro.runModel");
        }

        [McpServerTool, Description(
            "Run any geoprocessing tool directly (not just models). " +
            "Useful for one-off operations like Buffer, Clip, Select, etc.")]
        public static async Task<string> RunGPTool(
            [Description("Geoprocessing tool name, e.g., 'analysis.Buffer', 'management.AddField'")] string tool,
            [Description("JSON array of parameter values in order, e.g., " +
                "[\"input_features\", \"output_features\", \"100 Meters\"]")] string parameters)
        {
            var r = await _client!.OpAsync("pro.runGPTool", new()
            {
                ["tool"] = tool,
                ["parameters"] = parameters
            });
            return FormatResult(r, "pro.runGPTool");
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

        /// <summary>
        /// Serializes a bridge response as a JSON string. On success returns the raw data;
        /// on failure returns a structured error payload so the model can see what went wrong
        /// (the MCP SDK swallows thrown exception messages, leaving only a generic wrapper).
        /// </summary>
        private static string FormatResult(IpcResponse r, string op) =>
            r.Ok
                ? JsonSerializer.Serialize(r.Data, _jsonOpts)
                : JsonSerializer.Serialize(
                    new { success = false, op, error = r.Error ?? "<empty>" },
                    _jsonOpts);
    }
}
