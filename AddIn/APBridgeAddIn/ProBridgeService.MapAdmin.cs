using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core; // FrameworkExtender: CreateMapPaneAsync extension on Panes
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// Map administration family: create/open maps, basemaps, definition queries,
    /// transparency, labeling — the "project bootstrap" and per-layer display
    /// controls an agent needs to stage a map from nothing.
    /// </summary>
    internal partial class ProBridgeService
    {
        /// <summary>pro.createMap — creates a new map in the project (optionally opens it).</summary>
        private static async Task<IpcResponse> HandleCreateMap(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name))
                return new(false, "arg 'name' required", null);

            bool open = !args.TryGetValue("open", out string? openStr)
                        || !bool.TryParse(openStr, out var ob) || ob; // default true

            var map = await QueuedTask.Run(() =>
                MapFactory.Instance.CreateMap(name, MapType.Map, MapViewingMode.Map, Basemap.ProjectDefault));

            if (map == null)
                return new(false, "MapFactory.CreateMap returned null", null);

            if (open)
            {
                // Pane creation is GUI-thread-only (same pattern as HandleOpenLayout).
                try
                {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.CheckAccess())
                        await dispatcher.InvokeAsync(() => FrameworkApplication.Panes.CreateMapPaneAsync(map));
                    else
                        await FrameworkApplication.Panes.CreateMapPaneAsync(map);
                }
                catch { /* map created; opening the pane is best-effort */ }
            }

            return new(true, null, new { name = map.Name, opened = open });
        }

        /// <summary>
        /// pro.openMapView — opens (activates) a map view pane for the named map.
        /// This is how an agent switches the ACTIVE map, which most mutation ops
        /// target implicitly.
        /// </summary>
        private static async Task<IpcResponse> HandleOpenMapView(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name))
                return new(false, "arg 'name' required", null);

            ArcGIS.Desktop.Mapping.Map map;
            try
            {
                map = await QueuedTask.Run(() => ResolveMap(name));
            }
            catch (InvalidOperationException ex)
            {
                return new(false, ex.Message, null);
            }

            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    await dispatcher.InvokeAsync(() => FrameworkApplication.Panes.CreateMapPaneAsync(map));
                else
                    await FrameworkApplication.Panes.CreateMapPaneAsync(map);
            }
            catch (Exception ex)
            {
                return new(false, $"Failed to open map pane: {ex.Message}", null);
            }

            return new(true, null, new { opened = map.Name });
        }

        /// <summary>
        /// pro.setBasemap — swaps the map's basemap layers to a named Esri basemap.
        /// Accepts the ArcGIS.Desktop.Mapping.Basemap enum names; 'None' removes.
        /// </summary>
        private static async Task<IpcResponse> HandleSetBasemap(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("basemap", out string? basemapName) ||
                string.IsNullOrWhiteSpace(basemapName))
                return new(false,
                    $"arg 'basemap' required. Valid values: {string.Join(", ", Enum.GetNames(typeof(Basemap)))}", null);
            args.TryGetValue("map", out string? mapName);

            if (!Enum.TryParse<Basemap>(basemapName, true, out var basemap))
                return new(false,
                    $"Unknown basemap '{basemapName}'. Valid values: {string.Join(", ", Enum.GetNames(typeof(Basemap)))}", null);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = ResolveMap(mapName);
                map.SetBasemapLayers(basemap);
                return new(true, null, new { map = map.Name, basemap = basemap.ToString() });
            });
        }

        /// <summary>
        /// pro.setDefinitionQuery — sets (or clears, with empty string) a layer's
        /// definition query. Unlike selections, a definition query persistently
        /// restricts what the layer shows AND what GP tools see.
        /// </summary>
        private static async Task<IpcResponse> HandleSetDefinitionQuery(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new(false, "arg 'layer' required ('where' empty or omitted clears the query)", null);
            args.TryGetValue("where", out string? where);
            args.TryGetValue("map", out string? mapName);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = ResolveMap(mapName);
                var member = RequireMapMember(map, layerName);

                switch (member)
                {
                    case BasicFeatureLayer bfl:
                        bfl.SetDefinitionQuery(where ?? "");
                        break;
                    case ArcGIS.Desktop.Mapping.StandaloneTable st:
                        st.SetDefinitionQuery(where ?? "");
                        break;
                    default:
                        return new(false,
                            $"'{member.Name}' is a {member.GetType().Name} which doesn't support definition queries.", null);
                }

                return new(true, null, new
                {
                    layer = member.Name,
                    definitionQuery = where ?? "",
                    cleared = string.IsNullOrWhiteSpace(where)
                });
            });
        }

        /// <summary>pro.setLayerTransparency — 0 (opaque) to 100 (invisible).</summary>
        private static async Task<IpcResponse> HandleSetLayerTransparency(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("transparency", out string? trStr) ||
                !double.TryParse(trStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double transparency))
                return new(false, "args 'layer' and 'transparency' (0-100) required", null);
            args.TryGetValue("map", out string? mapName);

            transparency = Math.Max(0, Math.Min(100, transparency));

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = ResolveMap(mapName);
                var layer = map.GetLayersAsFlattenedList()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null)
                    return new(false,
                        $"Layer not found: {layerName}. Available: " +
                        string.Join(", ", map.GetLayersAsFlattenedList().Select(l => l.Name)), null);
                layer.SetTransparency(transparency);
                return new(true, null, new { layer = layer.Name, transparency });
            });
        }

        /// <summary>
        /// pro.setLabeling — toggles labels on a feature layer; optionally sets the
        /// label expression (Arcade). Pass 'field' for the common case (labels show
        /// that field) or 'expression' for full Arcade control.
        /// </summary>
        private static async Task<IpcResponse> HandleSetLabeling(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("visible", out string? visStr) ||
                !bool.TryParse(visStr, out bool visible))
                return new(false, "args 'layer' and 'visible' (true/false) required; optional 'field' or 'expression'", null);
            args.TryGetValue("field", out string? field);
            args.TryGetValue("expression", out string? expression);
            args.TryGetValue("map", out string? mapName);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = ResolveMap(mapName);
                var member = RequireMapMember(map, layerName);
                if (member is not FeatureLayer fl)
                    return new(false, $"'{member.Name}' is a {member.GetType().Name} — labeling applies to feature layers.", null);

                // Expression precedence: explicit Arcade > field shortcut > leave as-is.
                string? arcade = !string.IsNullOrWhiteSpace(expression)
                    ? expression
                    : !string.IsNullOrWhiteSpace(field) ? $"$feature.{field}" : null;

                if (arcade != null)
                {
                    if (fl.GetDefinition() is not CIMFeatureLayer cimLayer || cimLayer.LabelClasses == null ||
                        cimLayer.LabelClasses.Length == 0)
                        return new(false, $"Layer '{fl.Name}' has no label classes to configure.", null);

                    foreach (var lc in cimLayer.LabelClasses)
                    {
                        lc.Expression = arcade;
                        lc.ExpressionEngine = LabelExpressionEngine.Arcade;
                    }
                    fl.SetDefinition(cimLayer);
                }

                fl.SetLabelVisibility(visible);
                return new(true, null, new
                {
                    layer = fl.Name,
                    labelsVisible = visible,
                    expression = arcade
                });
            });
        }
    }
}
