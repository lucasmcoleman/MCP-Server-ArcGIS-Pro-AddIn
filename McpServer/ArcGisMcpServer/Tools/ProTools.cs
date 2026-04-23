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

        // The four tools below return Task<string> (not typed values) so that
        // bridge-side errors reach the agent as structured JSON via FormatResult.
        // The MCP SDK swallows thrown exception messages (leaves only a generic
        // "An error occurred invoking X"), so `throw new Exception(r.Error)`
        // loses all the structured error context the bridge already produces.
        // Returning FormatResult(r, op) matches the pattern used by the other
        // tools and keeps error text visible to the model.

        [McpServerTool, Description("Name of the active map in ArcGIS Pro")]
        public static async Task<string> GetActiveMapName()
        {
            var r = await _client!.OpAsync("pro.getActiveMapName");
            return FormatResult(r, "pro.getActiveMapName");
        }

        [McpServerTool, Description("List of layers in the active map")]
        public static async Task<string> ListLayers()
        {
            var r = await _client!.OpAsync("pro.listLayers");
            return FormatResult(r, "pro.listLayers");
        }

        [McpServerTool, Description("Count features in a layer by name")]
        public static async Task<string> CountFeatures(string layer)
        {
            var r = await _client!.OpAsync("pro.countFeatures", new() { ["layer"] = layer });
            return FormatResult(r, "pro.countFeatures");
        }

        [McpServerTool, Description("Zoom to a layer's extent by name")]
        public static async Task<string> ZoomToLayer(string layer)
        {
            var r = await _client!.OpAsync("pro.zoomToLayer", new() { ["layer"] = layer });
            return FormatResult(r, "pro.zoomToLayer");
        }

        [McpServerTool, Description(
            "Select features in a layer using a SQL WHERE clause. " +
            "Returns the number of selected features. " +
            "Example where clauses: \"POP > 1000\", \"NAME = 'Seattle'\", \"STATE IN ('WA','OR')\".")]
        public static async Task<string> SelectByAttribute(
            [Description("Name of the feature layer in the active map")] string layer,
            [Description("SQL WHERE clause to filter the layer's features")] string where)
        {
            var r = await _client!.OpAsync("pro.selectByAttribute", new()
            {
                ["layer"] = layer,
                ["where"] = where
            });
            return FormatResult(r, "pro.selectByAttribute");
        }

        [McpServerTool, Description(
            "Get the current extent (viewport) of the active map view. " +
            "Returns xmin/ymin/xmax/ymax, width/height, and the spatial reference WKID.")]
        public static async Task<string> GetCurrentExtent()
        {
            var r = await _client!.OpAsync("pro.getCurrentExtent");
            return FormatResult(r, "pro.getCurrentExtent");
        }

        [McpServerTool, Description(
            "Export a layer's features to a feature class or shapefile. " +
            "The output path determines the format: use a '.shp' extension for shapefile " +
            "output, otherwise provide a path inside a file/enterprise geodatabase " +
            "(e.g., 'C:/data/out.gdb/Buildings_Export'). An optional SQL WHERE clause " +
            "filters the exported features.")]
        public static async Task<string> ExportLayer(
            [Description("Name of the feature layer in the active map")] string layer,
            [Description("Full output path (shapefile path ending in .shp, or a feature class path inside a geodatabase)")] string output,
            [Description("Optional SQL WHERE clause to filter exported features")] string? where = null)
        {
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["output"] = output
            };
            if (!string.IsNullOrWhiteSpace(where))
                args["where"] = where;

            var r = await _client!.OpAsync("pro.exportLayer", args);
            return FormatResult(r, "pro.exportLayer");
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

        // ─── Project Tools ───────────────────────────────────────────────

        [McpServerTool, Description(
            "Create a new ArcGIS Pro project. The current project is saved first " +
            "to avoid a modal 'save changes?' dialog that would hang the bridge. " +
            "Returns the new project's name and .aprx path.")]
        public static async Task<string> CreateProject(
            [Description("Project name (used to name the .aprx file and project folder)")] string name,
            [Description("Folder path where the project folder will be created (e.g., 'F:/ArcGIS/Projects')")] string location,
            [Description("Optional: path to a .aptx project template")] string? template = null,
            [Description("Optional: overwrite an existing project with the same name/location (default false)")] bool overwrite = false)
        {
            var args = new Dictionary<string, string>
            {
                ["name"] = name,
                ["location"] = location,
                ["overwrite"] = overwrite.ToString()
            };
            if (!string.IsNullOrWhiteSpace(template))
                args["template"] = template;
            var r = await _client!.OpAsync("pro.createProject", args);
            return FormatResult(r, "pro.createProject");
        }

        [McpServerTool, Description(
            "Open an existing ArcGIS Pro project. The current project is saved first " +
            "to avoid a modal dialog.")]
        public static async Task<string> OpenProject(
            [Description("Full path to the .aprx project file")] string path)
        {
            var r = await _client!.OpAsync("pro.openProject", new() { ["path"] = path });
            return FormatResult(r, "pro.openProject");
        }

        // ─── Layer Tools ─────────────────────────────────────────────────

        [McpServerTool, Description(
            "Add a layer to the active map from a URL — typically an ArcGIS feature service " +
            "(e.g., 'https://services.arcgis.com/.../FeatureServer/0'). " +
            "Also accepts image services, tile services, WMS, and other Pro-supported URI sources.")]
        public static async Task<string> AddLayerFromUrl(
            [Description("URL to the service or layer endpoint")] string url,
            [Description("Optional: display name for the new layer in the TOC")] string? name = null)
        {
            var args = new Dictionary<string, string> { ["url"] = url };
            if (!string.IsNullOrWhiteSpace(name))
                args["name"] = name;
            var r = await _client!.OpAsync("pro.addLayerFromUrl", args);
            return FormatResult(r, "pro.addLayerFromUrl");
        }

        // ─── Layout Tools ────────────────────────────────────────────────

        [McpServerTool, Description("List all layouts in the current project (name + item path).")]
        public static async Task<string> ListLayouts()
        {
            var r = await _client!.OpAsync("pro.listLayouts");
            return FormatResult(r, "pro.listLayouts");
        }

        [McpServerTool, Description(
            "Open a layout in a new layout view pane in ArcGIS Pro. " +
            "Use list_layouts first to see available layout names.")]
        public static async Task<string> OpenLayout(
            [Description("Name of the layout to open")] string name)
        {
            var r = await _client!.OpAsync("pro.openLayout", new() { ["name"] = name });
            return FormatResult(r, "pro.openLayout");
        }

        [McpServerTool, Description(
            "List all elements on a layout — titles, scale bars, legends, north arrows, map frames, etc. " +
            "Returns element name, type, visibility, and (for text elements) a preview of the current text. " +
            "Use this before set_layout_text to discover the correct element name.")]
        public static async Task<string> ListLayoutElements(
            [Description("Name of the layout")] string name)
        {
            var r = await _client!.OpAsync("pro.listLayoutElements", new() { ["name"] = name });
            return FormatResult(r, "pro.listLayoutElements");
        }

        [McpServerTool, Description(
            "Set the text content of a text element on a layout (title, subtitle, notes, date stamp, etc.). " +
            "Use list_layout_elements first to find the element's exact name.")]
        public static async Task<string> SetLayoutText(
            [Description("Name of the layout containing the element")] string layoutName,
            [Description("Name of the text element on the layout")] string elementName,
            [Description("New text content (can include multiple lines)")] string text)
        {
            var r = await _client!.OpAsync("pro.setLayoutText", new()
            {
                ["layoutName"] = layoutName,
                ["elementName"] = elementName,
                ["text"] = text
            });
            return FormatResult(r, "pro.setLayoutText");
        }

        [McpServerTool, Description(
            "Export a layout to PDF (default), PNG, JPG, TIFF, or SVG. " +
            "Format is selected by the 'format' argument or by the output file's extension. " +
            "Raster formats default to 300 DPI; pass 'resolution' to override.")]
        public static async Task<string> ExportLayout(
            [Description("Name of the layout to export")] string name,
            [Description("Full output file path (e.g., 'C:/output/site_map.pdf')")] string output,
            [Description("Optional: 'pdf', 'png', 'jpg', 'tiff', or 'svg' (else inferred from extension)")] string? format = null,
            [Description("Optional: raster DPI for PNG/JPG/TIFF (default 300)")] int? resolution = null)
        {
            var args = new Dictionary<string, string>
            {
                ["name"] = name,
                ["output"] = output
            };
            if (!string.IsNullOrWhiteSpace(format))
                args["format"] = format;
            if (resolution.HasValue)
                args["resolution"] = resolution.Value.ToString();
            var r = await _client!.OpAsync("pro.exportLayout", args);
            return FormatResult(r, "pro.exportLayout");
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
