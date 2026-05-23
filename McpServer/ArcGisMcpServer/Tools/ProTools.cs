using ArcGisMcpServer.Ipc;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ArcGisMcpServer.Tools
{

    // Class is non-static (despite all members being static) so MCP SDK's
    // generic WithTools<T>() registration can take it as a type argument —
    // that overload is trim-safe; WithToolsFromAssembly() is not.
    [McpServerToolType]
    public class ProTools
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

        [McpServerTool, Description(
            "List all maps in the current project (name + item path). " +
            "Use this to enumerate maps before operations that take a map name " +
            "(e.g., add_map_frame_to_layout).")]
        public static async Task<string> ListMaps()
        {
            var r = await _client!.OpAsync("pro.listMaps");
            return FormatResult(r, "pro.listMaps");
        }

        [McpServerTool, Description(
            "List names of layers AND standalone tables in a map. Returns a flat " +
            "JSON array of names including spatial layers (nested via group layers " +
            "appear inline with their parents in TOC order) AND non-spatial " +
            "standalone tables. Use get_layer_properties on a returned name to " +
            "distinguish layer-vs-table or to discover geometry type. Default: " +
            "active map; specify 'map' to list items from a different map in the project.")]
        public static async Task<string> ListLayers(
            [Description("Optional: name of the map to list. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.listLayers", args);
            return FormatResult(r, "pro.listLayers");
        }

        [McpServerTool, Description(
            "Count features (or rows, for standalone tables) in a layer or " +
            "standalone table by name. Searches the active map by default; " +
            "specify 'map' to target a different map in the project.")]
        public static async Task<string> CountFeatures(
            [Description("Layer or standalone table name (matches what list_layers returns)")] string layer,
            [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.countFeatures", args);
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
            [Description("Feature layer OR standalone table name (matches what list_layers returns)")] string layer,
            [Description("SQL WHERE clause to filter the rows. Example: \"POP > 1000\"")] string where,
            [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["where"] = where
            };
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.selectByAttribute", args);
            return FormatResult(r, "pro.selectByAttribute");
        }

        [McpServerTool, Description(
            "List the field schema of a feature layer or standalone table: name, " +
            "alias, type, length, isNullable, isEditable for each field. Use before " +
            "select_by_attribute, read_layer_attributes, or run_gp_tool calls that " +
            "take field names so the agent can verify fields exist and check types " +
            "before crafting a query. Works on standalone tables (non-spatial " +
            "attribute tables) as well as feature layers. Default: active map; " +
            "specify 'map' to target a different map in the project.")]
        public static async Task<string> ListFields(
            [Description("Layer or standalone table name (matches what list_layers returns)")] string layer,
            [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.listFields", args);
            return FormatResult(r, "pro.listFields");
        }

        [McpServerTool, Description(
            "Get general properties of a layer or standalone table. For layers: type " +
            "(FeatureLayer, RasterLayer, etc.), data source path, spatial reference " +
            "(wkid + name), extent, visibility, feature count, geometry type. For " +
            "standalone tables: type (StandaloneTable), data source path, row count " +
            "(no SR/extent/geometry — they're non-spatial). Useful as a 'tell me about " +
            "this' query before deciding what operations apply. Default: active map; " +
            "specify 'map' to target a different map in the project.")]
        public static async Task<string> GetLayerProperties(
            [Description("Layer or standalone table name (matches what list_layers returns)")] string layer,
            [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.getLayerProperties", args);
            return FormatResult(r, "pro.getLayerProperties");
        }

        [McpServerTool, Description(
            "Read feature attribute values from a layer in the active map. Returns " +
            "JSON with field names and up to 'limit' rows. Geometry/Shape/Blob/Raster " +
            "fields are excluded from output. Useful for surfacing attribute data in " +
            "chat replies — e.g., turn-by-turn directions from a Network Analyst " +
            "Route\\DirectionPoints sublayer, top-N records by some field, or sampled " +
            "rows for exploratory analysis. Use 'where' to filter, 'orderBy' to sort, " +
            "and 'limit' to cap response size. If 'limited' is true in the response, " +
            "more rows exist than were returned — narrow with 'where' to see them.")]
        public static async Task<string> ReadLayerAttributes(
            [Description("Layer or standalone table name (matches what list_layers returns)")] string layer,
            [Description("Optional: comma-separated field names. Omit for all non-geometry fields.")] string? fields = null,
            [Description("Optional: SQL WHERE clause to filter rows.")] string? where = null,
            [Description("Optional: ORDER BY clause without the 'ORDER BY' keyword (e.g., 'Population DESC').")] string? orderBy = null,
            [Description("Optional: max rows to return. Default 50, max 1000.")] int limit = 50,
            [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(fields)) args["fields"] = fields;
            if (!string.IsNullOrWhiteSpace(where)) args["where"] = where;
            if (!string.IsNullOrWhiteSpace(orderBy)) args["orderBy"] = orderBy;
            args["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.readLayerAttributes", args);
            return FormatResult(r, "pro.readLayerAttributes");
        }

        [McpServerTool, Description(
            "Read attribute values from the layer's currently-selected features. " +
            "Useful after select_by_attribute to inspect exactly which features matched " +
            "the WHERE clause, or to read attributes of features the user selected " +
            "interactively in Pro. Returns the same JSON shape as read_layer_attributes " +
            "with an additional 'selectedTotal' count. If nothing is selected, returns " +
            "an empty rows list and selectedTotal=0 (not an error).")]
        public static async Task<string> GetSelectedFeatures(
            [Description("Layer or standalone table name (matches what list_layers returns)")] string layer,
            [Description("Optional: comma-separated field names. Omit for all non-geometry fields.")] string? fields = null,
            [Description("Optional: max rows to return. Default 50, max 1000.")] int limit = 50,
            [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(fields)) args["fields"] = fields;
            args["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.getSelectedFeatures", args);
            return FormatResult(r, "pro.getSelectedFeatures");
        }

        [McpServerTool, Description(
            "Clear feature selections in the active map. If 'layer' is specified, " +
            "clears selection only on that layer (errors if the layer is not found). " +
            "If omitted, clears selections across every feature layer in the active map. " +
            "Useful as a pre-op reset — leftover selections silently restrict geoprocessing " +
            "tool inputs when those tools accept layer names, which is a common source of " +
            "confusing 'unexpectedly-empty' outputs.")]
        public static async Task<string> ClearSelection(
            [Description("Optional: name of a specific layer or standalone table to clear. Omit to clear ALL feature layers AND all standalone tables.")] string? layer = null,
            [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(layer)) args["layer"] = layer;
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.clearSelection", args);
            return FormatResult(r, "pro.clearSelection");
        }

        [McpServerTool, Description(
            "Remove a layer from the active map's Table of Contents by name. " +
            "Removes the TOC reference only — the underlying feature class, raster, " +
            "or service is NOT deleted from disk. To delete data, use run_gp_tool " +
            "with management.Delete instead.")]
        public static async Task<string> RemoveLayer(
            [Description("Name of the layer to remove, matching what list_layers returns")] string layer)
        {
            var r = await _client!.OpAsync("pro.removeLayer", new() { ["layer"] = layer });
            return FormatResult(r, "pro.removeLayer");
        }

        [McpServerTool, Description(
            "Rename a layer in the active map. If the new name conflicts with an " +
            "existing layer, ArcGIS Pro may auto-uniquify (e.g., 'Foo' becomes " +
            "'Foo (2)') — the returned 'to' value reflects the actual post-rename name.")]
        public static async Task<string> RenameLayer(
            [Description("Current layer name, matching what list_layers returns")] string layer,
            [Description("New name for the layer")] string newName)
        {
            var r = await _client!.OpAsync("pro.renameLayer", new()
            {
                ["layer"] = layer,
                ["newName"] = newName
            });
            return FormatResult(r, "pro.renameLayer");
        }

        [McpServerTool, Description(
            "Show or hide a layer in the active map without removing it from the TOC. " +
            "Useful when staging a map for export: hide reference layers, show analysis " +
            "outputs, export, then restore.")]
        public static async Task<string> SetLayerVisibility(
            [Description("Layer name, matching what list_layers returns")] string layer,
            [Description("true to show the layer, false to hide it")] bool visible)
        {
            var r = await _client!.OpAsync("pro.setLayerVisibility", new()
            {
                ["layer"] = layer,
                ["visible"] = visible.ToString().ToLowerInvariant()
            });
            return FormatResult(r, "pro.setLayerVisibility");
        }

        [McpServerTool, Description(
            "Move a layer to a new position in the active map's Table of Contents. " +
            "Position is 0-based: 0 is topmost, higher numbers are below. " +
            "Out-of-range values are clamped silently to the valid range. " +
            "Operates on top-level layers only; nested layers inside group layers " +
            "are not supported in this version.")]
        public static async Task<string> MoveLayer(
            [Description("Layer name, matching what list_layers returns")] string layer,
            [Description("Target 0-based position. 0 = topmost.")] int position)
        {
            var r = await _client!.OpAsync("pro.moveLayer", new()
            {
                ["layer"] = layer,
                ["position"] = position.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            return FormatResult(r, "pro.moveLayer");
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
            "Diagnostic: returns raw Map.SpatialReference, Extent.SpatialReference, " +
            "Camera (X/Y/Z/Scale/Heading/Pitch/Roll), and Map.CalculateFullExtent(). " +
            "Use this when get_current_extent returns values that don't match the " +
            "reported SR, or when an agent needs to introspect 2D-vs-3D view state. " +
            "NaN/Infinity values (e.g., Camera.Z in 2D mode) appear as JSON string " +
            "literals due to System.Text.Json's named-floating-point handling.")]
        public static async Task<string> GetViewDiagnostics()
        {
            var r = await _client!.OpAsync("pro.getViewDiagnostics");
            return FormatResult(r, "pro.getViewDiagnostics");
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
            "Get metadata about the currently open ArcGIS Pro project — name, aprx file " +
            "path, home folder, default geodatabase, default toolbox, counts of maps / " +
            "layouts / toolboxes, and active map info (name + spatial reference). " +
            "Useful for agents to orient themselves before operations that depend on " +
            "project context.")]
        public static async Task<string> GetProjectInfo()
        {
            var r = await _client!.OpAsync("pro.getProjectInfo");
            return FormatResult(r, "pro.getProjectInfo");
        }

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

        [McpServerTool, Description(
            "Explicitly save the currently-open project. Most project-lifecycle ops " +
            "save-first automatically, but this is useful as a pre-operation safety " +
            "rail or to persist a batch of edits the agent wants to commit to disk.")]
        public static async Task<string> SaveProject()
        {
            var r = await _client!.OpAsync("pro.saveProject");
            return FormatResult(r, "pro.saveProject");
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

        [McpServerTool, Description(
            "Add a layer to the active map from a file-system path. Supports shapefiles " +
            "(path/to/file.shp), file-geodatabase feature classes (path/to/my.gdb/FeatureClass), " +
            "rasters, and any other path LayerFactory can resolve. For .gdb feature classes, " +
            "use a composite path where the .gdb folder is followed by the feature-class name " +
            "(e.g., 'F:/projects/my.gdb/Roads').")]
        public static async Task<string> AddLayerFromFile(
            [Description("Full file-system path to the data source")] string path,
            [Description("Optional: display name for the new layer in the TOC")] string? name = null)
        {
            var args = new Dictionary<string, string> { ["path"] = path };
            if (!string.IsNullOrWhiteSpace(name))
                args["name"] = name;
            var r = await _client!.OpAsync("pro.addLayerFromFile", args);
            return FormatResult(r, "pro.addLayerFromFile");
        }

        // ─── Layout Tools ────────────────────────────────────────────────

        [McpServerTool, Description("List all layouts in the current project (name + item path).")]
        public static async Task<string> ListLayouts()
        {
            var r = await _client!.OpAsync("pro.listLayouts");
            return FormatResult(r, "pro.listLayouts");
        }

        [McpServerTool, Description(
            "Create a new blank layout. Defaults to letter-landscape (11x8.5 in). " +
            "The layout is empty — use add_map_frame_to_layout to attach a map, and " +
            "add other elements before export. Use list_layouts to see result or " +
            "open_layout to view it in Pro.")]
        public static async Task<string> CreateLayout(
            [Description("Name for the new layout (must be unique within the project)")] string name,
            [Description("Optional: page width in inches (default 11)")] double? widthInches = null,
            [Description("Optional: page height in inches (default 8.5)")] double? heightInches = null,
            [Description("Optional: 'landscape' (default) or 'portrait' — coerces width/height order to match")] string? orientation = null)
        {
            var args = new Dictionary<string, string> { ["name"] = name };
            if (widthInches.HasValue) args["widthInches"] = widthInches.Value.ToString();
            if (heightInches.HasValue) args["heightInches"] = heightInches.Value.ToString();
            if (!string.IsNullOrWhiteSpace(orientation)) args["orientation"] = orientation;
            var r = await _client!.OpAsync("pro.createLayout", args);
            return FormatResult(r, "pro.createLayout");
        }

        [McpServerTool, Description(
            "Add a map-frame element to an existing layout and bind it to a map. " +
            "This is the step that makes a layout actually renderable — without a " +
            "map frame, create_layout's output is blank. Default placement is 1in " +
            "from top-left, sized 9x6.5in (fits letter-landscape with 1in margins).")]
        public static async Task<string> AddMapFrameToLayout(
            [Description("Name of the existing layout to add the frame to")] string layoutName,
            [Description("Name of an existing map to wire into the frame (use list_maps to discover)")] string mapName,
            [Description("Optional: x-position of frame top-left in inches (default 1)")] double? xInches = null,
            [Description("Optional: y-position of frame top-left in inches (default 1)")] double? yInches = null,
            [Description("Optional: frame width in inches (default 9)")] double? widthInches = null,
            [Description("Optional: frame height in inches (default 6.5)")] double? heightInches = null)
        {
            var args = new Dictionary<string, string>
            {
                ["layoutName"] = layoutName,
                ["mapName"] = mapName
            };
            if (xInches.HasValue) args["xInches"] = xInches.Value.ToString();
            if (yInches.HasValue) args["yInches"] = yInches.Value.ToString();
            if (widthInches.HasValue) args["widthInches"] = widthInches.Value.ToString();
            if (heightInches.HasValue) args["heightInches"] = heightInches.Value.ToString();
            var r = await _client!.OpAsync("pro.addMapFrameToLayout", args);
            return FormatResult(r, "pro.addMapFrameToLayout");
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
            "- 'inputs': array of input parameter objects (see schema below) " +
            "- 'steps': array of processing steps, each with: " +
            "  - 'name': display name for the step " +
            "  - 'tool': geoprocessing tool name (e.g., 'analysis.Buffer', 'sa.Reclassify') " +
            "  - 'parameters': object mapping param names to either: " +
            "    - {\"ref\": \"InputName\"} to connect to an input or previous output " +
            "    - {\"output\": \"OutputName\", \"type\": \"DEFeatureClass\"} to declare an output " +
            "    - \"literal value\" for constant values " +
            "\n\nINPUT PARAMETER SCHEMA — each entry in 'inputs' supports: " +
            "- 'name' (required): string — parameter name (no spaces). " +
            "- 'type' (optional): string — Pro datatype (GPFeatureLayer, GPString, GPLong, " +
            "  GPDouble, GPLinearUnit, GPSQLExpression, DERasterDataset, DEFeatureClass, " +
            "  Field, GPComposite, etc.). Omit when the parameter is a Field that depends " +
            "  on another input — declare 'dependencies' instead and the writer auto-types " +
            "  it as Field. " +
            "- 'dependencies' (optional): string[] — list of other input parameter names " +
            "  this Field-typed parameter validates against. E.g., for a 'ZoneField' that " +
            "  references fields on a 'CorridorLayer' input, write " +
            "  {\"name\":\"ZoneField\",\"dependencies\":[\"CorridorLayer\"]}. " +
            "- 'compositeTypes' (optional, only when type is 'GPComposite'): string[] — " +
            "  the list of accepted subtypes when the parameter wires into a GPComposite " +
            "  slot (e.g., CalculateField.in_table accepts " +
            "  GPComposite{GPTableView, GPRasterLayer, GPMosaicLayer}). Example: " +
            "  {\"name\":\"InTable\",\"type\":\"GPComposite\"," +
            "  \"compositeTypes\":[\"GPTableView\",\"GPRasterLayer\",\"GPMosaicLayer\"]}. " +
            "- 'default' (optional): string — the parameter's default value. " +
            "- 'displayName' (optional): string — human-readable name for the GP dialog." +
            "\n\nCommon GP tool categories: analysis (overlay, proximity), conversion, " +
            "management (fields, joins), sa (spatial analyst - raster), na (network analyst).")]
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
            "Start a ModelBuilder model run asynchronously and return a job id " +
            "immediately. Use this instead of RunModel when the model may exceed " +
            "the agent's tool-call timeout (e.g., Aurora-class models with hosted " +
            "service clips). Poll progress with GetRunStatus(jobId). Returns " +
            "{jobId, started, pollWith}.")]
        public static async Task<string> RunModelAsync(
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

            var r = await _client!.OpAsync("pro.runModelAsync", args);
            return FormatResult(r, "pro.runModelAsync");
        }

        [McpServerTool, Description(
            "Get the current status of an async model run by job id. Returns a " +
            "snapshot: status (running/succeeded/failed), totalSteps, " +
            "completedSteps, currentStep, plus failedStep/failedTool/error on " +
            "failure and the cumulative messages list. Cheap to poll; reads a " +
            "snapshot without blocking the run. Once endedUtc is populated the " +
            "run is done and messages are final. Jobs auto-expire 1 hour after " +
            "completion.")]
        public static async Task<string> GetRunStatus(
            [Description("Job id returned by RunModelAsync")] string jobId)
        {
            var args = new Dictionary<string, string> { ["jobId"] = jobId };
            var r = await _client!.OpAsync("pro.getRunStatus", args);
            return FormatResult(r, "pro.getRunStatus");
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

        [McpServerTool, Description(
            "Add point features to an existing point layer in the active map. " +
            "The 'features' parameter is a JSON array of point definitions; each " +
            "point has x and y coordinates IN THE LAYER'S SPATIAL REFERENCE (no " +
            "automatic reprojection) and an optional attributes map for other " +
            "fields. Coordinates use X (longitude) first, Y (latitude) second. " +
            "Inserts run in a single transactional edit operation — if any feature " +
            "fails, none are committed. Errors specify the failing feature's index. " +
            "Returns the count of added features and their ObjectIDs. " +
            "Example features value: " +
            "[{\"x\": -78.7073, \"y\": 35.7345, \"attributes\": {\"Name\": \"Home\"}}, " +
            "{\"x\": -78.7819, \"y\": 35.7312, \"attributes\": {\"Name\": \"Work\"}}]. " +
            "Use list_fields to discover the layer's field names and types first; " +
            "use get_layer_properties to confirm the layer's spatial reference.")]
        public static async Task<string> AddPointFeatures(
            [Description("Point layer name, matching what list_layers returns")] string layer,
            [Description("JSON array of point feature definitions — each has x, y, and optional attributes")] string features)
        {
            var r = await _client!.OpAsync("pro.addPointFeatures", new()
            {
                ["layer"] = layer,
                ["features"] = features
            });
            return FormatResult(r, "pro.addPointFeatures");
        }

        [McpServerTool, Description(
            "Add polygon features to an existing polygon layer in the active map. " +
            "The 'features' parameter is a JSON array of polygon definitions; each " +
            "polygon has a 'vertices' array of [x, y] coordinate pairs IN THE " +
            "LAYER'S SPATIAL REFERENCE (no automatic reprojection) and an optional " +
            "attributes map. Vertices must include at least 3 points; the ring is " +
            "auto-closed if the first and last vertex differ, so don't repeat the " +
            "first vertex. Inserts run in a single transactional edit operation. " +
            "Returns the count of added features and their ObjectIDs. " +
            "Example features value: [{\"vertices\": [[-78.71, 35.74], [-78.70, 35.74], " +
            "[-78.70, 35.73], [-78.71, 35.73]], \"attributes\": {\"Name\": \"Barrier1\"}}]. " +
            "Useful for Network Analyst polygon barriers, custom AOIs, and any " +
            "polygon-creation-from-coordinates workflow. For complex shapes with " +
            "holes or multiple rings, generate the feature via run_gp_tool " +
            "(e.g., management.CreateFeatureclass + JSONToFeatures) instead.")]
        public static async Task<string> AddPolygonFeatures(
            [Description("Polygon layer name, matching what list_layers returns")] string layer,
            [Description("JSON array of polygon feature definitions — each has vertices ([x,y] pairs) and optional attributes")] string features)
        {
            var r = await _client!.OpAsync("pro.addPolygonFeatures", new()
            {
                ["layer"] = layer,
                ["features"] = features
            });
            return FormatResult(r, "pro.addPolygonFeatures");
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Serializes a bridge response as a JSON string. On success returns the raw data;
        /// on failure returns a structured error payload so the model can see what went wrong
        /// (the MCP SDK swallows thrown exception messages, leaving only a generic wrapper).
        /// </summary>
        private static string FormatResult(IpcResponse r, string op)
        {
            if (!r.Ok)
                return JsonSerializer.Serialize(
                    new FormatErrorPayload(false, op, r.Error ?? "<empty>"),
                    IndentedJsonContext.Default.FormatErrorPayload);

            // r.Ok=true: bridge returned successfully. Data is normally a real
            // JsonElement; null only occurs for side-effect-only ops that don't
            // produce a payload, in which case we surface the literal "null".
            return r.Data is JsonElement data
                ? JsonSerializer.Serialize(data, IndentedJsonContext.Default.JsonElement)
                : "null";
        }
    }
}
