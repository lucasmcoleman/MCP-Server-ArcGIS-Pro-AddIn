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
            if (widthInches.HasValue) args["widthInches"] = widthInches.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (heightInches.HasValue) args["heightInches"] = heightInches.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            if (xInches.HasValue) args["xInches"] = xInches.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (yInches.HasValue) args["yInches"] = yInches.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (widthInches.HasValue) args["widthInches"] = widthInches.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (heightInches.HasValue) args["heightInches"] = heightInches.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                args["resolution"] = resolution.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            "If no path is specified, creates it in the project home folder. " +
            "Refuses to replace an existing toolbox (which would destroy its " +
            "models) unless overwrite=true is passed explicitly.")]
        public static async Task<string> CreateToolbox(
            [Description("Display name for the new toolbox")] string name,
            [Description("Optional: full file path where the .atbx file should be created. " +
                "If omitted, uses the project home folder.")] string? path = null,
            [Description("Optional: replace an existing toolbox at that path, DESTROYING its models (default false)")] bool overwrite = false)
        {
            var args = new Dictionary<string, string> { ["name"] = name };
            if (!string.IsNullOrWhiteSpace(path))
                args["path"] = path;
            if (overwrite)
                args["overwrite"] = "true";

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
            "Surgically set (or clear) the default value of one model input parameter — " +
            "without regenerating the rest of the model. Use this instead of UpdateModel when " +
            "you only need to change a parameter default. Everything else in the model " +
            "(other variables, every step, the diagram) stays byte-identical, so this cannot " +
            "re-trigger slot canonicalization or any other round-trip behavior. " +
            "Pass an empty defaultValue to clear an existing default.")]
        public static async Task<string> SetParameterDefault(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("Name of the existing model containing the parameter")] string modelName,
            [Description("Exact param_name of the input parameter to modify (must be an exposed model Parameter, not a derived output)")] string parameterName,
            [Description("New default value as a string; empty string clears the default")] string defaultValue)
        {
            var r = await _client!.OpAsync("pro.setParameterDefault", new()
            {
                ["toolboxPath"] = toolboxPath,
                ["modelName"] = modelName,
                ["parameterName"] = parameterName,
                ["defaultValue"] = defaultValue ?? ""
            });
            return FormatResult(r, "pro.setParameterDefault");
        }

        [McpServerTool, Description(
            "Surgically set one parameter on one step inside an existing model — without " +
            "regenerating the rest of the model. Use this to retarget a step input or change " +
            "a literal step value. Everything else stays byte-identical. " +
            "paramValue is a JSON string with one of two shapes: " +
            "{\"ref\":\"VariableName\"} to wire the param to that variable, OR " +
            "{\"value\":\"literal\"} (or a bare JSON string) for a literal value. " +
            "Does NOT accept output declarations — changing a step's output reshapes the " +
            "graph and belongs in AddStep/RemoveStep (not yet implemented).")]
        public static async Task<string> SetStepParameter(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("Name of the existing model containing the step")] string modelName,
            [Description("Exact display name of the step to modify (match what DescribeModel returns)")] string stepName,
            [Description("Parameter key on the step (e.g., 'in_features', 'where_clause')")] string paramKey,
            [Description("New parameter value as JSON: {\"ref\":\"Var\"} or {\"value\":\"x\"} or a bare \"string\"")] string paramValue)
        {
            var r = await _client!.OpAsync("pro.setStepParameter", new()
            {
                ["toolboxPath"] = toolboxPath,
                ["modelName"] = modelName,
                ["stepName"] = stepName,
                ["paramKey"] = paramKey,
                ["paramValue"] = paramValue
            });
            return FormatResult(r, "pro.setStepParameter");
        }

        [McpServerTool, Description(
            "Run a ModelBuilder model with specified parameter values. " +
            "Use describe_model first to see what parameters the model expects. " +
            "Executes GP-tool steps, script-tool steps (dispatched by toolbox " +
            "path, including cross-toolbox references), and nested-model steps " +
            "(recursed step-by-step when hosted in an .atbx; legacy .tbx-hosted " +
            "nested models run whole-tool). Iterator steps are not supported. " +
            "Script-tool steps execute Python — subject to the same post-launch " +
            "warm-up caveat as execute_python. " +
            "SYNCHRONOUS — blocks until the model finishes. For models that may " +
            "run longer than ~2 minutes wall-clock, use start_run_model + " +
            "get_run_status instead so the tool call doesn't time out.")]
        public static async Task<string> RunModel(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("Name of the model to run")] string modelName,
            [Description("Optional: JSON object mapping parameter names to values, " +
                "e.g., {\"StudyArea\": \"Counties\", \"BufferDistance\": \"1000 Meters\"}")] string? parameters = null,
            [Description("Optional: JSON object overriding ANY model variable by name " +
                "(not just exposed parameters). Use to substitute dataset paths for " +
                "bare map-layer names when running a model without its project open, " +
                "e.g., {\"Farmland_CVWD\": \"C:\\\\data\\\\x.gdb\\\\Farmland\"}.")] string? variableOverrides = null)
        {
            var args = new Dictionary<string, string>
            {
                ["toolboxPath"] = toolboxPath,
                ["modelName"] = modelName
            };
            if (!string.IsNullOrWhiteSpace(parameters))
                args["parameters"] = parameters;
            if (!string.IsNullOrWhiteSpace(variableOverrides))
                args["variableOverrides"] = variableOverrides;

            var r = await _client!.OpAsync("pro.runModel", args);
            return FormatResult(r, "pro.runModel");
        }

        [McpServerTool, Description(
            "Start a ModelBuilder model run asynchronously and return a job id " +
            "immediately. Use this instead of run_model when the model may exceed " +
            "the agent's tool-call timeout (e.g., long-running models with hosted " +
            "service clips). Poll progress with get_run_status using the returned " +
            "jobId. Returns {jobId, started, pollWith}.")]
        public static async Task<string> StartRunModel(
            [Description("Full file path to the .atbx toolbox file")] string toolboxPath,
            [Description("Name of the model to run")] string modelName,
            [Description("Optional: JSON object mapping parameter names to values, " +
                "e.g., {\"StudyArea\": \"Counties\", \"BufferDistance\": \"1000 Meters\"}")] string? parameters = null,
            [Description("Optional: JSON object overriding ANY model variable by name " +
                "(not just exposed parameters) — see run_model for usage.")] string? variableOverrides = null)
        {
            var args = new Dictionary<string, string>
            {
                ["toolboxPath"] = toolboxPath,
                ["modelName"] = modelName
            };
            if (!string.IsNullOrWhiteSpace(parameters))
                args["parameters"] = parameters;
            if (!string.IsNullOrWhiteSpace(variableOverrides))
                args["variableOverrides"] = variableOverrides;

            var r = await _client!.OpAsync("pro.runModelAsync", args);
            return FormatResult(r, "pro.runModelAsync");
        }

        [McpServerTool, Description(
            "Get the current status of an async model run by job id (from " +
            "start_run_model). Returns a snapshot: status " +
            "(running/succeeded/failed), totalSteps, completedSteps, currentStep, " +
            "plus failedStep/failedTool/error on failure, totalMessages, and the " +
            "messages list. Pass messagesFrom=<totalMessages from your previous " +
            "poll> to receive only NEW messages instead of the whole list each " +
            "time. Cheap to poll. Once endedUtc is populated the run is done. " +
            "Jobs auto-expire 1 hour after completion.")]
        public static async Task<string> GetRunStatus(
            [Description("Job id returned by start_run_model")] string jobId,
            [Description("Optional: skip messages before this index (use totalMessages from the prior poll)")] int messagesFrom = 0)
        {
            var args = new Dictionary<string, string> { ["jobId"] = jobId };
            if (messagesFrom > 0)
                args["messagesFrom"] = messagesFrom.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var r = await _client!.OpAsync("pro.getRunStatus", args);
            return FormatResult(r, "pro.getRunStatus");
        }

        [McpServerTool, Description(
            "Run any geoprocessing tool directly (not just models). Useful for " +
            "one-off operations like Buffer, Clip, AddField, Statistics, etc. " +
            "Parameters are POSITIONAL in the tool's arcpy signature order — use " +
            "describe_gp_tool to get the exact order, and pass \"#\" in any " +
            "optional slot you want to skip (tool default applies). Multivalue/" +
            "value-table params accept nested JSON arrays: [\"f1\",\"v1\"] pairs " +
            "become arcpy's 'f1 v1;f2 v2' syntax automatically. Runs with " +
            "overwriteoutput=true (existing outputs are replaced). The response " +
            "includes returnValue/outputs so you know where results landed. " +
            "Outputs are NOT added to the map — use add_layer_from_file to " +
            "display them.")]
        public static async Task<string> RunGPTool(
            [Description("Geoprocessing tool name, e.g., 'analysis.Buffer', 'management.AddField'")] string tool,
            [Description("JSON array of parameter values in positional order, e.g., " +
                "[\"Roads\", \"C:/out.gdb/RoadsBuf\", \"100 Meters\", \"#\", \"#\", \"ALL\"]")] string parameters,
            [Description("Optional: JSON object of GP environment overrides for this call, e.g., " +
                "{\"extent\": \"xmin ymin xmax ymax\", \"outputCoordinateSystem\": \"PROJCS[...]or wkid\", " +
                "\"cellSize\": \"30\", \"mask\": \"StudyArea\", \"snapRaster\": \"dem\"}")] string? environments = null)
        {
            var args = new Dictionary<string, string>
            {
                ["tool"] = tool,
                ["parameters"] = parameters
            };
            if (!string.IsNullOrWhiteSpace(environments))
                args["environments"] = environments;
            var r = await _client!.OpAsync("pro.runGPTool", args);
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

        // ─── GP Tool Discovery ───────────────────────────────────────────

        [McpServerTool, Description(
            "Get the full parameter schema of any system geoprocessing tool by " +
            "'alias.ToolName' (e.g., 'analysis.Buffer', 'management.AddField'). " +
            "Returns each parameter IN POSITIONAL ORDER with name, data type, " +
            "in/out direction, optional flag, default value, allowed values " +
            "(coded domains), and dependencies. The positional order maps 1:1 to " +
            "run_gp_tool's parameters array — pass \"#\" to skip an optional slot " +
            "and use the tool's default. Call this BEFORE run_gp_tool when unsure " +
            "of a tool's exact signature instead of guessing parameter order.")]
        public static async Task<string> DescribeGpTool(
            [Description("Tool id as 'alias.ToolName', e.g. 'analysis.Buffer'")] string tool)
        {
            var r = await _client!.OpAsync("pro.describeGpTool", new() { ["tool"] = tool });
            return FormatResult(r, "pro.describeGpTool");
        }

        [McpServerTool, Description(
            "Search ArcGIS Pro's ~1700 system geoprocessing tools by name keyword. " +
            "Returns matching 'alias.ToolName' ids ready for describe_gp_tool / " +
            "run_gp_tool. Example: keyword 'buffer' finds analysis.Buffer, " +
            "analysis.PairwiseBuffer, analysis.GraphicBuffer, etc.")]
        public static async Task<string> SearchGpTools(
            [Description("Substring to match against tool names (case-insensitive)")] string keyword,
            [Description("Optional: max results (default 25, max 100)")] int limit = 25)
        {
            var args = new Dictionary<string, string>
            {
                ["keyword"] = keyword,
                ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            var r = await _client!.OpAsync("pro.searchGpTools", args);
            return FormatResult(r, "pro.searchGpTools");
        }

        // ─── Python Escape Hatch ─────────────────────────────────────────

        [McpServerTool, Description(
            "Execute arbitrary Python code INSIDE ArcGIS Pro's live Python " +
            "environment (arcpy pre-imported, full arcpy.mp / arcpy.da / CIM " +
            "access). Because it runs in-process, " +
            "arcpy.mp.ArcGISProject('CURRENT') manipulates the OPEN project — " +
            "use this for anything the dedicated tools don't cover: advanced " +
            "symbology, da.SearchCursor aggregation (pandas is available), " +
            "Describe on any dataset, layouts, metadata, etc. " +
            "CONTRACT: print() output is captured to 'stdout'; assign a variable " +
            "named `result` to return a JSON-serializable value; exceptions " +
            "return ok=false with the full traceback (read it and self-correct). " +
            "State does NOT persist between calls (fresh namespace each time); " +
            "batch related work into one call. Long-running code blocks Pro's GP " +
            "queue — keep calls under a few minutes. NOTE: for ~3 minutes after " +
            "ArcGIS Pro launches, calls are refused with a 'warming up' error " +
            "(calling Python too early can wedge Pro's GP for the whole session) " +
            "— just wait and retry as instructed.")]
        public static async Task<string> ExecutePython(
            [Description("Python source code. arcpy is pre-imported. Set `result = ...` to return data; print() for logs.")] string code)
        {
            var r = await _client!.OpAsync("pro.executePython", new() { ["code"] = code });
            return FormatResult(r, "pro.executePython");
        }

        // ─── View / Camera / Bookmarks ───────────────────────────────────

        [McpServerTool, Description(
            "Capture the active map view to a PNG image — this is how you SEE the " +
            "map (current extent, symbology, drawn layers). Returns the file path; " +
            "read the image file to inspect it. Use after symbology/layout changes " +
            "to verify results visually, or before answering questions about what " +
            "the map shows. Requires a map tab to be active in Pro (for layouts " +
            "use export_layout). Default 1200x900 px to project_home/mcp-captures/.")]
        public static async Task<string> CaptureMapView(
            [Description("Optional: output PNG path. Default: <project home>/mcp-captures/map_view_<timestamp>.png")] string? output = null,
            [Description("Optional: image width in pixels (default 1200, max 4096)")] int? width = null,
            [Description("Optional: image height in pixels (default 900, max 4096)")] int? height = null)
        {
            var args = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(output)) args["output"] = output;
            if (width.HasValue) args["width"] = width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (height.HasValue) args["height"] = height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var r = await _client!.OpAsync("pro.captureMapView", args);
            return FormatResult(r, "pro.captureMapView");
        }

        [McpServerTool, Description(
            "Zoom the active map view to an explicit bounding box. Coordinates " +
            "are in the map's spatial reference unless 'wkid' says otherwise " +
            "(e.g., pass wkid=4326 for lon/lat degrees).")]
        public static async Task<string> ZoomToExtent(
            [Description("West edge")] double xmin,
            [Description("South edge")] double ymin,
            [Description("East edge")] double xmax,
            [Description("North edge")] double ymax,
            [Description("Optional: spatial reference WKID of the coordinates (default: map SR)")] int? wkid = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var args = new Dictionary<string, string>
            {
                ["xmin"] = xmin.ToString(inv),
                ["ymin"] = ymin.ToString(inv),
                ["xmax"] = xmax.ToString(inv),
                ["ymax"] = ymax.ToString(inv)
            };
            if (wkid.HasValue) args["wkid"] = wkid.Value.ToString(inv);
            var r = await _client!.OpAsync("pro.zoomToExtent", args);
            return FormatResult(r, "pro.zoomToExtent");
        }

        [McpServerTool, Description(
            "Set the active map view's scale denominator (e.g., 24000 shows the " +
            "map at 1:24,000). Keeps the current center point.")]
        public static async Task<string> ZoomToScale(
            [Description("Scale denominator, e.g. 24000 for 1:24,000")] double scale)
        {
            var r = await _client!.OpAsync("pro.zoomToScale", new()
            {
                ["scale"] = scale.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            return FormatResult(r, "pro.zoomToScale");
        }

        [McpServerTool, Description(
            "Zoom the active map view to the union of all currently selected " +
            "features (any layer). No-op with a hint if nothing is selected.")]
        public static async Task<string> ZoomToSelected()
        {
            var r = await _client!.OpAsync("pro.zoomToSelected");
            return FormatResult(r, "pro.zoomToSelected");
        }

        [McpServerTool, Description(
            "List spatial bookmarks of a map (name + extent). Default: active map.")]
        public static async Task<string> ListBookmarks(
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.listBookmarks", args);
            return FormatResult(r, "pro.listBookmarks");
        }

        [McpServerTool, Description("Zoom the active map view to a named bookmark.")]
        public static async Task<string> ZoomToBookmark(
            [Description("Bookmark name (see list_bookmarks)")] string name)
        {
            var r = await _client!.OpAsync("pro.zoomToBookmark", new() { ["name"] = name });
            return FormatResult(r, "pro.zoomToBookmark");
        }

        [McpServerTool, Description(
            "Create a bookmark of the active map view's current extent.")]
        public static async Task<string> CreateBookmark(
            [Description("Name for the new bookmark")] string name)
        {
            var r = await _client!.OpAsync("pro.createBookmark", new() { ["name"] = name });
            return FormatResult(r, "pro.createBookmark");
        }

        // ─── Editing ─────────────────────────────────────────────────────

        [McpServerTool, Description(
            "Update attribute values on features/rows matching a WHERE clause or " +
            "an explicit ObjectID list (exactly one of 'where'/'oids' required — " +
            "use where=\"1=1\" to deliberately target every row; capped at 10,000 " +
            "rows). 'attributes' is a JSON object {field: value, ...}. Edits go " +
            "into Pro's undo-able edit session — call save_edits to persist to " +
            "disk. Example: layer='Parcels', where=\"ZONE='R1'\", " +
            "attributes='{\"STATUS\": \"Reviewed\", \"SCORE\": 5}'.")]
        public static async Task<string> UpdateFeatures(
            [Description("Layer or standalone table name (matches list_layers)")] string layer,
            [Description("JSON object of field:value pairs to set")] string attributes,
            [Description("SQL WHERE clause selecting rows to update (or use 'oids')")] string? where = null,
            [Description("Comma-separated ObjectIDs to update (or use 'where')")] string? oids = null,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["attributes"] = attributes
            };
            if (!string.IsNullOrWhiteSpace(where)) args["where"] = where;
            if (!string.IsNullOrWhiteSpace(oids)) args["oids"] = oids;
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.updateFeatures", args);
            return FormatResult(r, "pro.updateFeatures");
        }

        [McpServerTool, Description(
            "Delete features/rows matching a WHERE clause or an explicit ObjectID " +
            "list (exactly one of 'where'/'oids' required; capped at 10,000 rows). " +
            "Deletes go into Pro's undo-able edit session — call save_edits to " +
            "persist, or discard_edits to roll back.")]
        public static async Task<string> DeleteFeatures(
            [Description("Layer or standalone table name (matches list_layers)")] string layer,
            [Description("SQL WHERE clause selecting rows to delete (or use 'oids')")] string? where = null,
            [Description("Comma-separated ObjectIDs to delete (or use 'where')")] string? oids = null,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(where)) args["where"] = where;
            if (!string.IsNullOrWhiteSpace(oids)) args["oids"] = oids;
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.deleteFeatures", args);
            return FormatResult(r, "pro.deleteFeatures");
        }

        [McpServerTool, Description(
            "Add polyline features to an existing polyline layer in the active " +
            "map. Same contract as add_point_features/add_polygon_features: each " +
            "feature has 'vertices' ([x,y] pairs, at least 2, IN THE LAYER'S " +
            "SPATIAL REFERENCE) and optional 'attributes'. Example: " +
            "[{\"vertices\": [[-78.71, 35.74], [-78.70, 35.73]], " +
            "\"attributes\": {\"Name\": \"Route A\"}}]")]
        public static async Task<string> AddPolylineFeatures(
            [Description("Polyline layer name, matching what list_layers returns")] string layer,
            [Description("JSON array of polyline definitions — each has vertices ([x,y] pairs) and optional attributes")] string features)
        {
            var r = await _client!.OpAsync("pro.addPolylineFeatures", new()
            {
                ["layer"] = layer,
                ["features"] = features
            });
            return FormatResult(r, "pro.addPolylineFeatures");
        }

        [McpServerTool, Description(
            "Save all pending edits in Pro's edit session to disk. Use after " +
            "update_features / delete_features / add_*_features when the changes " +
            "should be permanent.")]
        public static async Task<string> SaveEdits()
        {
            var r = await _client!.OpAsync("pro.saveEdits");
            return FormatResult(r, "pro.saveEdits");
        }

        [McpServerTool, Description(
            "Discard ALL pending (unsaved) edits in Pro's edit session — rolls " +
            "back every update/delete/add since the last save. Destructive to " +
            "pending work; check has_edits first if unsure.")]
        public static async Task<string> DiscardEdits()
        {
            var r = await _client!.OpAsync("pro.discardEdits");
            return FormatResult(r, "pro.discardEdits");
        }

        [McpServerTool, Description("Check whether Pro has unsaved edits in its edit session.")]
        public static async Task<string> HasEdits()
        {
            var r = await _client!.OpAsync("pro.hasEdits");
            return FormatResult(r, "pro.hasEdits");
        }

        // ─── Map Administration ──────────────────────────────────────────

        [McpServerTool, Description(
            "Create a new map in the project (and open its view by default, " +
            "making it the active map). Uses the project's default basemap.")]
        public static async Task<string> CreateMap(
            [Description("Name for the new map")] string name,
            [Description("Optional: open a view pane for it (default true)")] bool open = true)
        {
            var r = await _client!.OpAsync("pro.createMap", new()
            {
                ["name"] = name,
                ["open"] = open.ToString().ToLowerInvariant()
            });
            return FormatResult(r, "pro.createMap");
        }

        [McpServerTool, Description(
            "Open (activate) a map view pane for a named map. Most mutation tools " +
            "target the ACTIVE map — use this to switch which map that is.")]
        public static async Task<string> OpenMapView(
            [Description("Map name (see list_maps)")] string name)
        {
            var r = await _client!.OpAsync("pro.openMapView", new() { ["name"] = name });
            return FormatResult(r, "pro.openMapView");
        }

        [McpServerTool, Description(
            "Set a map's basemap to a named Esri basemap (replaces current " +
            "basemap layers). The error message lists valid names if yours " +
            "doesn't match; common ones: Imagery, Streets, Topographic, " +
            "LightGray, DarkGray, Oceans, OpenStreetMap, Terrain, None.")]
        public static async Task<string> SetBasemap(
            [Description("Basemap name (e.g. 'Imagery', 'Topographic', 'None')")] string basemap,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["basemap"] = basemap };
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.setBasemap", args);
            return FormatResult(r, "pro.setBasemap");
        }

        [McpServerTool, Description(
            "Set or clear a layer's definition query. Unlike a selection, a " +
            "definition query persistently filters what the layer displays AND " +
            "what geoprocessing sees, until cleared. Pass an empty/omitted " +
            "'where' to clear. Example: layer='Wetlands', where=\"ACRES > 0.5\".")]
        public static async Task<string> SetDefinitionQuery(
            [Description("Layer or standalone table name (matches list_layers)")] string layer,
            [Description("SQL WHERE clause; omit or pass empty to CLEAR the query")] string? where = null,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(where)) args["where"] = where;
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.setDefinitionQuery", args);
            return FormatResult(r, "pro.setDefinitionQuery");
        }

        [McpServerTool, Description(
            "Set a layer's transparency: 0 = fully opaque, 100 = invisible. " +
            "Useful for overlay cartography (e.g., 40-60 for analysis results " +
            "over a basemap).")]
        public static async Task<string> SetLayerTransparency(
            [Description("Layer name, matching what list_layers returns")] string layer,
            [Description("Transparency percent 0-100")] double transparency,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["transparency"] = transparency.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.setLayerTransparency", args);
            return FormatResult(r, "pro.setLayerTransparency");
        }

        [McpServerTool, Description(
            "Turn feature labels on/off for a layer, optionally setting what they " +
            "show: pass 'field' for the simple case (labels display that field) " +
            "or 'expression' for full Arcade control (e.g., " +
            "'$feature.NAME + \\\" (\\\" + $feature.ACRES + \\\")\\\"').")]
        public static async Task<string> SetLabeling(
            [Description("Feature layer name, matching what list_layers returns")] string layer,
            [Description("true to show labels, false to hide")] bool visible,
            [Description("Optional: field name to label with (shortcut for expression '$feature.<field>')")] string? field = null,
            [Description("Optional: full Arcade label expression (overrides 'field')")] string? expression = null,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["visible"] = visible.ToString().ToLowerInvariant()
            };
            if (!string.IsNullOrWhiteSpace(field)) args["field"] = field;
            if (!string.IsNullOrWhiteSpace(expression)) args["expression"] = expression;
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.setLabeling", args);
            return FormatResult(r, "pro.setLabeling");
        }

        // ─── Layout Furniture ────────────────────────────────────────────

        [McpServerTool, Description(
            "Add a legend to a layout, bound to a map frame (auto-lists the " +
            "frame's visible layers). Position/size in inches from page TOP-LEFT. " +
            "Defaults: x=0.5, y=0.5, 2.5x3.5in.")]
        public static async Task<string> AddLegend(
            [Description("Layout name (see list_layouts)")] string layoutName,
            [Description("Optional: map frame name (default: the layout's first frame)")] string? frameName = null,
            [Description("Optional: x of top-left in inches")] double? xInches = null,
            [Description("Optional: y of top-left in inches")] double? yInches = null,
            [Description("Optional: width in inches")] double? widthInches = null,
            [Description("Optional: height in inches")] double? heightInches = null)
        {
            var args = BuildSurroundArgs(layoutName, frameName, xInches, yInches, widthInches, heightInches);
            var r = await _client!.OpAsync("pro.addLegend", args);
            return FormatResult(r, "pro.addLegend");
        }

        [McpServerTool, Description(
            "Add a north arrow to a layout, bound to a map frame (rotates with " +
            "the frame). Position/size in inches from page TOP-LEFT. Defaults " +
            "suit letter-landscape top-right (x=10.2, y=0.4, 0.5x0.8in).")]
        public static async Task<string> AddNorthArrow(
            [Description("Layout name (see list_layouts)")] string layoutName,
            [Description("Optional: map frame name (default: first frame)")] string? frameName = null,
            [Description("Optional: style item name (default 'ESRI North 1')")] string? style = null,
            [Description("Optional: x of top-left in inches")] double? xInches = null,
            [Description("Optional: y of top-left in inches")] double? yInches = null,
            [Description("Optional: width in inches")] double? widthInches = null,
            [Description("Optional: height in inches")] double? heightInches = null)
        {
            var args = BuildSurroundArgs(layoutName, frameName, xInches, yInches, widthInches, heightInches);
            if (!string.IsNullOrWhiteSpace(style)) args["style"] = style;
            var r = await _client!.OpAsync("pro.addNorthArrow", args);
            return FormatResult(r, "pro.addNorthArrow");
        }

        [McpServerTool, Description(
            "Add a scale bar to a layout, bound to a map frame (tracks the " +
            "frame's scale). Position/size in inches from page TOP-LEFT. Defaults " +
            "suit letter-landscape bottom-left (x=1, y=7.7, 3x0.5in).")]
        public static async Task<string> AddScaleBar(
            [Description("Layout name (see list_layouts)")] string layoutName,
            [Description("Optional: map frame name (default: first frame)")] string? frameName = null,
            [Description("Optional: style item name (default 'Alternating Scale Bar 1')")] string? style = null,
            [Description("Optional: x of top-left in inches")] double? xInches = null,
            [Description("Optional: y of top-left in inches")] double? yInches = null,
            [Description("Optional: width in inches")] double? widthInches = null,
            [Description("Optional: height in inches")] double? heightInches = null)
        {
            var args = BuildSurroundArgs(layoutName, frameName, xInches, yInches, widthInches, heightInches);
            if (!string.IsNullOrWhiteSpace(style)) args["style"] = style;
            var r = await _client!.OpAsync("pro.addScaleBar", args);
            return FormatResult(r, "pro.addScaleBar");
        }

        [McpServerTool, Description(
            "Add a NEW free text element to a layout (title, subtitle, credits). " +
            "Position in inches from page TOP-LEFT (the text anchors at that " +
            "point). To change text of an EXISTING element use set_layout_text.")]
        public static async Task<string> AddLayoutText(
            [Description("Layout name (see list_layouts)")] string layoutName,
            [Description("Text content")] string text,
            [Description("Optional: element name (auto-generated if omitted)")] string? name = null,
            [Description("Optional: x of anchor in inches (default 1)")] double? xInches = null,
            [Description("Optional: y of anchor in inches (default 0.4)")] double? yInches = null,
            [Description("Optional: font size in points (default 24)")] double? fontSize = null,
            [Description("Optional: font family (default Arial)")] string? font = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var args = new Dictionary<string, string>
            {
                ["layoutName"] = layoutName,
                ["text"] = text
            };
            if (!string.IsNullOrWhiteSpace(name)) args["name"] = name;
            if (xInches.HasValue) args["xInches"] = xInches.Value.ToString(inv);
            if (yInches.HasValue) args["yInches"] = yInches.Value.ToString(inv);
            if (fontSize.HasValue) args["fontSize"] = fontSize.Value.ToString(inv);
            if (!string.IsNullOrWhiteSpace(font)) args["font"] = font;
            var r = await _client!.OpAsync("pro.addLayoutText", args);
            return FormatResult(r, "pro.addLayoutText");
        }

        [McpServerTool, Description(
            "Point a layout map frame's camera at a layer's extent OR an explicit " +
            "bounding box — how you control what area the printed map shows. " +
            "Provide 'layer' (zoom to that layer) or xmin/ymin/xmax/ymax.")]
        public static async Task<string> SetMapFrameExtent(
            [Description("Layout name (see list_layouts)")] string layoutName,
            [Description("Optional: map frame name (default: first frame)")] string? frameName = null,
            [Description("Optional: layer name to zoom the frame to")] string? layer = null,
            [Description("Optional: west edge (with ymin/xmax/ymax)")] double? xmin = null,
            [Description("Optional: south edge")] double? ymin = null,
            [Description("Optional: east edge")] double? xmax = null,
            [Description("Optional: north edge")] double? ymax = null,
            [Description("Optional: WKID of the envelope coords (default: frame map SR)")] int? wkid = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var args = new Dictionary<string, string> { ["layoutName"] = layoutName };
            if (!string.IsNullOrWhiteSpace(frameName)) args["frameName"] = frameName;
            if (!string.IsNullOrWhiteSpace(layer)) args["layer"] = layer;
            if (xmin.HasValue) args["xmin"] = xmin.Value.ToString(inv);
            if (ymin.HasValue) args["ymin"] = ymin.Value.ToString(inv);
            if (xmax.HasValue) args["xmax"] = xmax.Value.ToString(inv);
            if (ymax.HasValue) args["ymax"] = ymax.Value.ToString(inv);
            if (wkid.HasValue) args["wkid"] = wkid.Value.ToString(inv);
            var r = await _client!.OpAsync("pro.setMapFrameExtent", args);
            return FormatResult(r, "pro.setMapFrameExtent");
        }

        // ─── Symbology ───────────────────────────────────────────────────

        [McpServerTool, Description(
            "Set a feature layer's renderer. rendererType options: " +
            "'simple' (one symbol — use fillR/fillG/fillB 0-255, optional " +
            "outline colors, size for points, lineWidth for lines); " +
            "'uniqueValues' (one color per distinct value of 'field'); " +
            "'graduatedColors' (numeric 'field' classified into 'breakCount' " +
            "classes with a color ramp, default Natural Breaks). " +
            "Optional 'colorRamp' names a Pro color ramp (e.g. 'Yellow to Red', " +
            "'Viridis'). For renderer types beyond these, use execute_python " +
            "with arcpy.mp. Verify results visually with capture_map_view.")]
        public static async Task<string> SetLayerRenderer(
            [Description("Feature layer name, matching what list_layers returns")] string layer,
            [Description("'simple', 'uniqueValues', or 'graduatedColors'")] string rendererType,
            [Description("Field name (required for uniqueValues/graduatedColors)")] string? field = null,
            [Description("Optional: color ramp name for uniqueValues/graduatedColors")] string? colorRamp = null,
            [Description("Optional: class count for graduatedColors (default 5)")] int? breakCount = null,
            [Description("Optional: fill/marker/line red 0-255 (simple)")] int? fillR = null,
            [Description("Optional: fill/marker/line green 0-255 (simple)")] int? fillG = null,
            [Description("Optional: fill/marker/line blue 0-255 (simple)")] int? fillB = null,
            [Description("Optional: point marker size in points (simple, default 8)")] double? size = null,
            [Description("Optional: line width in points (simple, default 1.5)")] double? lineWidth = null,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["rendererType"] = rendererType
            };
            if (!string.IsNullOrWhiteSpace(field)) args["field"] = field;
            if (!string.IsNullOrWhiteSpace(colorRamp)) args["colorRamp"] = colorRamp;
            if (breakCount.HasValue) args["breakCount"] = breakCount.Value.ToString(inv);
            if (fillR.HasValue) args["fillR"] = fillR.Value.ToString(inv);
            if (fillG.HasValue) args["fillG"] = fillG.Value.ToString(inv);
            if (fillB.HasValue) args["fillB"] = fillB.Value.ToString(inv);
            if (size.HasValue) args["size"] = size.Value.ToString(inv);
            if (lineWidth.HasValue) args["lineWidth"] = lineWidth.Value.ToString(inv);
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.setLayerRenderer", args);
            return FormatResult(r, "pro.setLayerRenderer");
        }

        [McpServerTool, Description(
            "Get a summary of a feature layer's current renderer (type, " +
            "classification field, class count).")]
        public static async Task<string> GetLayerSymbology(
            [Description("Feature layer name, matching what list_layers returns")] string layer,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string> { ["layer"] = layer };
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.getLayerSymbology", args);
            return FormatResult(r, "pro.getLayerSymbology");
        }

        // ─── Analysis ────────────────────────────────────────────────────

        [McpServerTool, Description(
            "Scan one field of a layer/table and return its value profile: " +
            "total/null counts, distinct count, the most frequent values with " +
            "counts, and min/max/mean for numeric fields. Use BEFORE writing a " +
            "WHERE clause (see actual values, not guessed ones) or choosing a " +
            "symbology field. Optional 'where' pre-filters the scan.")]
        public static async Task<string> GetFieldStatistics(
            [Description("Layer or standalone table name (matches list_layers)")] string layer,
            [Description("Field name to profile")] string field,
            [Description("Optional: SQL WHERE clause to scan a subset")] string? where = null,
            [Description("Optional: how many top values to return (default 20, max 100)")] int topN = 20,
            [Description("Optional: map name. Default: active map.")] string? map = null)
        {
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["field"] = field,
                ["topN"] = topN.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(where)) args["where"] = where;
            if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
            var r = await _client!.OpAsync("pro.getFieldStatistics", args);
            return FormatResult(r, "pro.getFieldStatistics");
        }

        [McpServerTool, Description(
            "Select features in one layer based on their spatial relationship " +
            "to another layer's features (intersect, within distance, contains, " +
            "etc.). Returns the resulting selected count. Combine with " +
            "select_by_attribute via selectionType (NEW_SELECTION, " +
            "ADD_TO_SELECTION, REMOVE_FROM_SELECTION, SUBSET_SELECTION). " +
            "Remember selections silently restrict later GP tool inputs — " +
            "clear_selection when done.")]
        public static async Task<string> SelectByLocation(
            [Description("Target layer whose features get selected")] string layer,
            [Description("Layer whose geometry does the selecting")] string selectFeatures,
            [Description("Optional: spatial relationship — INTERSECT (default), WITHIN_A_DISTANCE, CONTAINS, WITHIN, COMPLETELY_CONTAINS, COMPLETELY_WITHIN, HAVE_THEIR_CENTER_IN, SHARE_A_LINE_SEGMENT_WITH, BOUNDARY_TOUCHES, CROSSED_BY_THE_OUTLINE_OF, ARE_IDENTICAL_TO")] string? overlapType = null,
            [Description("Optional: distance for WITHIN_A_DISTANCE, e.g. '500 Meters'")] string? searchDistance = null,
            [Description("Optional: NEW_SELECTION (default), ADD_TO_SELECTION, REMOVE_FROM_SELECTION, SUBSET_SELECTION")] string? selectionType = null,
            [Description("Optional: invert the spatial relationship (default false)")] bool invert = false)
        {
            var args = new Dictionary<string, string>
            {
                ["layer"] = layer,
                ["selectFeatures"] = selectFeatures
            };
            if (!string.IsNullOrWhiteSpace(overlapType)) args["overlapType"] = overlapType;
            if (!string.IsNullOrWhiteSpace(searchDistance)) args["searchDistance"] = searchDistance;
            if (!string.IsNullOrWhiteSpace(selectionType)) args["selectionType"] = selectionType;
            if (invert) args["invert"] = "true";
            var r = await _client!.OpAsync("pro.selectByLocation", args);
            return FormatResult(r, "pro.selectByLocation");
        }

        // ─── Catalog / Data Discovery ────────────────────────────────────

        [McpServerTool, Description(
            "List the contents of a file geodatabase WITHOUT needing anything in " +
            "a map: feature classes (with geometry type + SR), tables, rasters, " +
            "and feature datasets. Defaults to the project's default GDB — where " +
            "run_model and run_gp_tool outputs land — so use this to verify " +
            "geoprocessing actually produced its outputs.")]
        public static async Task<string> ListGdbContents(
            [Description("Optional: path to a .gdb folder. Default: project default geodatabase.")] string? gdbPath = null)
        {
            var args = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(gdbPath)) args["gdbPath"] = gdbPath;
            var r = await _client!.OpAsync("pro.listGdbContents", args);
            return FormatResult(r, "pro.listGdbContents");
        }

        [McpServerTool, Description(
            "Describe a dataset by full path (e.g. 'F:/proj/data.gdb/Wetlands') " +
            "without adding it to a map: fields, geometry type, spatial " +
            "reference, extent, and row count. File-geodatabase paths only; for " +
            "shapefiles or rasters use execute_python with arcpy.Describe.")]
        public static async Task<string> DescribeDataset(
            [Description("Full path to a feature class or table inside a .gdb")] string path)
        {
            var r = await _client!.OpAsync("pro.describeDataset", new() { ["path"] = path });
            return FormatResult(r, "pro.describeDataset");
        }

        // ─── Advanced ────────────────────────────────────────────────────

        [McpServerTool, Description(
            "ADVANCED escape hatch: send a raw bridge op with a JSON args object. " +
            "Lets you reach Add-In ops that don't have a dedicated MCP tool yet " +
            "(e.g., after the Add-In is updated but before this server is " +
            "rebuilt). All arg values must be strings. Returns the raw bridge " +
            "response. Unknown ops return 'op not found: <op>'.")]
        public static async Task<string> BridgeOp(
            [Description("Bridge op name, e.g. 'pro.getProjectInfo'")] string op,
            [Description("Optional: JSON object of string arguments, e.g. {\"layer\": \"Roads\"}")] string? argsJson = null)
        {
            Dictionary<string, string>? args = null;
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(argsJson)?.AsObject()
                        ?? throw new FormatException("argsJson must be a JSON object");
                    args = new Dictionary<string, string>();
                    foreach (var kv in node)
                    {
                        args[kv.Key] = kv.Value is System.Text.Json.Nodes.JsonValue v &&
                                       v.TryGetValue<string>(out var s)
                            ? s
                            : kv.Value?.ToJsonString() ?? "";
                    }
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(
                        new FormatErrorPayload(false, op, $"Invalid argsJson: {ex.Message}"),
                        IndentedJsonContext.Default.FormatErrorPayload);
                }
            }
            var r = await _client!.OpAsync(op, args);
            return FormatResult(r, op);
        }

        [McpServerTool, Description(
            "List live ArcGIS Pro instances this MCP server can see (one bridge " +
            "per Pro process), without contacting any of them. Shows each " +
            "instance's PID, open project, pipe name, and which one THIS server " +
            "routes to ('selected'). Routing: if the ARCGIS_PROJECT env var is " +
            "set on this server, it is pinned strictly to the Pro instance with " +
            "that project open (requests fail rather than touch a different " +
            "instance); otherwise the most-recently-started instance is used. " +
            "Run this first when multiple Pro windows may be open, or to " +
            "diagnose 'pinned project not open' errors.")]
        public static string ListBridges()
        {
            // Pure registry read — works even when no Pro instance is running,
            // which is exactly when an agent most needs to see what's going on.
            var entries = BridgeDiscovery.ReadAllLive();
            var selected = BridgeDiscovery.SelectCurrent(entries);
            var envPin = BridgeDiscovery.PinnedProject;
            var pin = BridgeDiscovery.EffectivePin;
            var source = envPin != null ? "env" : (pin != null ? "agent" : "auto");

            var pinLabel = source == "env" ? "PINNED via ARCGIS_PROJECT" : "Routing set via select_bridge";
            var note = pin == null
                ? (entries.Count > 1
                    ? "Auto routing: this server follows the most-recently-started instance, which can " +
                      "change if another Pro launches. Use select_bridge (or the ARCGIS_PROJECT env var) to pin."
                    : "Auto routing (most-recently-started instance). Use select_bridge to target a specific instance.")
                : (selected == null
                    ? $"{pinLabel} to '{pin}' but that project is not open in any live instance — requests will fail until it is."
                    : $"{pinLabel} to '{pin}': all requests route to pid {selected.Pid} only.");

            var payload = new BridgeListPayload(
                pin,
                source,
                entries
                    .OrderByDescending(e => e.StartedUtc)
                    .Select(e => new BridgeInstanceInfo(
                        e.Pid, e.ProjectName, e.ProjectPath, e.PipeName, e.StartedUtc,
                        Selected: ReferenceEquals(e, selected)))
                    .ToList(),
                note);
            return JsonSerializer.Serialize(payload, IndentedJsonContext.Default.BridgeListPayload);
        }

        [McpServerTool, Description(
            "Route this server's subsequent tool calls to a specific ArcGIS Pro " +
            "instance, selected by the project it has open (run list_bridges " +
            "first to see live instances). Lets one agent work across multiple " +
            "Pro instances by switching between them: select_bridge('ProjectB'), " +
            "do work there, select_bridge('ProjectA') to switch back, or " +
            "select_bridge() with no argument to return to automatic " +
            "most-recent routing. The selection is strict — while it is set, " +
            "calls fail rather than touch a different instance — and persists " +
            "for this server process until changed. Refused when the server is " +
            "hard-pinned via the ARCGIS_PROJECT env var (that pin is the user's " +
            "isolation guarantee in multi-agent setups and cannot be overridden " +
            "from inside a session). Returns the same payload as list_bridges " +
            "reflecting the new routing.")]
        public static string SelectBridge(
            [Description("Project to route to: bare name, name.aprx, or full path (case-insensitive). " +
                         "Omit/empty to clear the selection and return to automatic routing.")]
            string? project = null)
        {
            if (BridgeDiscovery.PinnedProject is { } envPin)
                return JsonSerializer.Serialize(
                    new FormatErrorPayload(false, "select_bridge",
                        $"This server is hard-pinned to '{envPin}' via the ARCGIS_PROJECT env var; " +
                        "select_bridge cannot override an operator pin. To work across multiple Pro " +
                        "instances from one session, either run this server unpinned or configure one " +
                        "pinned server entry per instance in .mcp.json."),
                    IndentedJsonContext.Default.FormatErrorPayload);

            BridgeDiscovery.RuntimeOverride = project;
            // Echo the resulting routing state so the agent sees immediately
            // whether the selection matched a live instance.
            return ListBridges();
        }

        private static Dictionary<string, string> BuildSurroundArgs(
            string layoutName, string? frameName,
            double? xInches, double? yInches, double? widthInches, double? heightInches)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var args = new Dictionary<string, string> { ["layoutName"] = layoutName };
            if (!string.IsNullOrWhiteSpace(frameName)) args["frameName"] = frameName;
            if (xInches.HasValue) args["xInches"] = xInches.Value.ToString(inv);
            if (yInches.HasValue) args["yInches"] = yInches.Value.ToString(inv);
            if (widthInches.HasValue) args["widthInches"] = widthInches.Value.ToString(inv);
            if (heightInches.HasValue) args["heightInches"] = heightInches.Value.ToString(inv);
            return args;
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
