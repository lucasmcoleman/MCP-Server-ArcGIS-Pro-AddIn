using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// Layout furniture family: legend / north arrow / scale bar surrounds, free
    /// text, and map-frame camera control — the pieces needed to finish a real
    /// print map after add_map_frame_to_layout.
    ///
    /// Coordinate convention matches add_map_frame_to_layout: x/y are inches from
    /// the page TOP-LEFT (web convention agents expect); the Pro SDK's bottom-up
    /// page space is converted internally.
    /// </summary>
    internal partial class ProBridgeService
    {
        private static (Layout? layout, string? error) GetLayoutByName(string name)
        {
            var item = Project.Current?.GetItems<LayoutProjectItem>()
                .FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                var available = Project.Current?.GetItems<LayoutProjectItem>()
                    .Select(i => i.Name).ToList() ?? new List<string>();
                return (null, $"Layout not found: {name}. Available: " +
                    (available.Count > 0 ? string.Join(", ", available) : "<none>"));
            }
            var layout = item.GetLayout();
            return layout == null ? (null, $"Could not load layout: {name}") : (layout, null);
        }

        private static (MapFrame? frame, string? error) GetMapFrame(Layout layout, string? frameName)
        {
            var frames = layout.GetElements().OfType<MapFrame>().ToList();
            if (frames.Count == 0)
                return (null, "Layout has no map frame — add one with add_map_frame_to_layout first.");
            if (string.IsNullOrWhiteSpace(frameName))
                return (frames[0], null);
            var frame = frames.FirstOrDefault(f => f.Name.Equals(frameName, StringComparison.OrdinalIgnoreCase));
            return frame != null
                ? (frame, null)
                : (null, $"Map frame not found: {frameName}. Available: {string.Join(", ", frames.Select(f => f.Name))}");
        }

        private static Envelope TopLeftEnvelope(Layout layout, double x, double y, double w, double h)
        {
            double pageHeight = layout.GetPage().Height;
            return EnvelopeBuilderEx.CreateEnvelope(x, pageHeight - y - h, x + w, pageHeight - y);
        }

        private static double ArgDouble(Dictionary<string, string>? args, string key, double fallback)
        {
            if (args != null && args.TryGetValue(key, out string? s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
            return fallback;
        }

        /// <summary>pro.addLegend — legend bound to a map frame (auto-lists visible layers).</summary>
        private static async Task<IpcResponse> HandleAddLegend(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layoutName", out string? layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new(false, "arg 'layoutName' required", null);
            args.TryGetValue("frameName", out string? frameName);

            double x = ArgDouble(args, "xInches", 0.5), y = ArgDouble(args, "yInches", 0.5);
            double w = ArgDouble(args, "widthInches", 2.5), h = ArgDouble(args, "heightInches", 3.5);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var (layout, err) = GetLayoutByName(layoutName);
                if (layout == null) return new(false, err, null);
                var (frame, ferr) = GetMapFrame(layout, frameName);
                if (frame == null) return new(false, ferr, null);

                var env = TopLeftEnvelope(layout, x, y, w, h);
                var legendInfo = new LegendInfo { MapFrameName = frame.Name };
                var legend = ElementFactory.Instance.CreateMapSurroundElement(
                    layout, env, legendInfo, "Legend");
                if (legend == null)
                    return new(false, "CreateMapSurroundElement returned null for the legend", null);

                return new(true, null, new
                {
                    layoutName,
                    elementName = legend.Name,
                    boundToFrame = frame.Name
                });
            });
        }

        /// <summary>pro.addNorthArrow — style item from the ArcGIS 2D system style.</summary>
        private static async Task<IpcResponse> HandleAddNorthArrow(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layoutName", out string? layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new(false, "arg 'layoutName' required", null);
            args.TryGetValue("frameName", out string? frameName);
            args.TryGetValue("style", out string? styleName);

            double x = ArgDouble(args, "xInches", 10.2), y = ArgDouble(args, "yInches", 0.4);
            double w = ArgDouble(args, "widthInches", 0.5), h = ArgDouble(args, "heightInches", 0.8);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var (layout, err) = GetLayoutByName(layoutName);
                if (layout == null) return new(false, err, null);
                var (frame, ferr) = GetMapFrame(layout, frameName);
                if (frame == null) return new(false, ferr, null);

                var style2d = Project.Current?.GetItems<StyleProjectItem>()
                    .FirstOrDefault(s => s.Name == "ArcGIS 2D");
                var naStyle = style2d?.SearchNorthArrows(
                        string.IsNullOrWhiteSpace(styleName) ? "ESRI North 1" : styleName)
                    ?.FirstOrDefault()
                    ?? style2d?.SearchNorthArrows("")?.FirstOrDefault();

                var info = new NorthArrowInfo { MapFrameName = frame.Name };
                if (naStyle != null) info.NorthArrowStyleItem = naStyle;

                var env = TopLeftEnvelope(layout, x, y, w, h);
                var element = ElementFactory.Instance.CreateMapSurroundElement(
                    layout, env, info, "North Arrow");
                if (element == null)
                    return new(false, "CreateMapSurroundElement returned null for the north arrow", null);

                return new(true, null, new
                {
                    layoutName,
                    elementName = element.Name,
                    style = naStyle?.Name,
                    boundToFrame = frame.Name
                });
            });
        }

        /// <summary>pro.addScaleBar — style item from the ArcGIS 2D system style.</summary>
        private static async Task<IpcResponse> HandleAddScaleBar(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layoutName", out string? layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new(false, "arg 'layoutName' required", null);
            args.TryGetValue("frameName", out string? frameName);
            args.TryGetValue("style", out string? styleName);

            double x = ArgDouble(args, "xInches", 1.0), y = ArgDouble(args, "yInches", 7.7);
            double w = ArgDouble(args, "widthInches", 3.0), h = ArgDouble(args, "heightInches", 0.5);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var (layout, err) = GetLayoutByName(layoutName);
                if (layout == null) return new(false, err, null);
                var (frame, ferr) = GetMapFrame(layout, frameName);
                if (frame == null) return new(false, ferr, null);

                var style2d = Project.Current?.GetItems<StyleProjectItem>()
                    .FirstOrDefault(s => s.Name == "ArcGIS 2D");
                var sbStyle = style2d?.SearchScaleBars(
                        string.IsNullOrWhiteSpace(styleName) ? "Alternating Scale Bar 1" : styleName)
                    ?.FirstOrDefault()
                    ?? style2d?.SearchScaleBars("")?.FirstOrDefault();

                var info = new ScaleBarInfo { MapFrameName = frame.Name };
                if (sbStyle != null) info.ScaleBarStyleItem = sbStyle;

                var env = TopLeftEnvelope(layout, x, y, w, h);
                var element = ElementFactory.Instance.CreateMapSurroundElement(
                    layout, env, info, "Scale Bar");
                if (element == null)
                    return new(false, "CreateMapSurroundElement returned null for the scale bar", null);

                return new(true, null, new
                {
                    layoutName,
                    elementName = element.Name,
                    style = sbStyle?.Name,
                    boundToFrame = frame.Name
                });
            });
        }

        /// <summary>
        /// pro.addLayoutText — adds a free text element (title, caption, credits).
        /// Distinct from set_layout_text, which edits an EXISTING element's text.
        /// </summary>
        private static async Task<IpcResponse> HandleAddLayoutText(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layoutName", out string? layoutName) ||
                string.IsNullOrWhiteSpace(layoutName) ||
                !args.TryGetValue("text", out string? text) ||
                string.IsNullOrWhiteSpace(text))
                return new(false, "args 'layoutName' and 'text' required", null);
            args.TryGetValue("name", out string? elementName);

            double x = ArgDouble(args, "xInches", 1.0), y = ArgDouble(args, "yInches", 0.4);
            double fontSize = ArgDouble(args, "fontSize", 24);
            args.TryGetValue("font", out string? font);
            if (string.IsNullOrWhiteSpace(font)) font = "Arial";

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var (layout, err) = GetLayoutByName(layoutName);
                if (layout == null) return new(false, err, null);

                double pageHeight = layout.GetPage().Height;
                var location = MapPointBuilderEx.CreateMapPoint(x, pageHeight - y);
                var symbol = SymbolFactory.Instance.ConstructTextSymbol(
                    ColorFactory.Instance.BlackRGB, fontSize, font, "Regular");

                var element = ElementFactory.Instance.CreateTextGraphicElement(
                    layout, TextType.PointText, location, symbol, text,
                    string.IsNullOrWhiteSpace(elementName) ? null : elementName);
                if (element == null)
                    return new(false, "CreateTextGraphicElement returned null", null);

                return new(true, null, new
                {
                    layoutName,
                    elementName = element.Name,
                    text,
                    fontSize
                });
            });
        }

        /// <summary>
        /// pro.setMapFrameExtent — points a layout map frame's camera at a layer's
        /// extent or an explicit envelope (in the frame's map SR unless wkid given).
        /// </summary>
        private static async Task<IpcResponse> HandleSetMapFrameExtent(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layoutName", out string? layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new(false, "arg 'layoutName' required (plus 'layer' OR xmin/ymin/xmax/ymax)", null);
            args.TryGetValue("frameName", out string? frameName);
            args.TryGetValue("layer", out string? layerName);

            bool hasEnv = args.ContainsKey("xmin") && args.ContainsKey("ymin")
                       && args.ContainsKey("xmax") && args.ContainsKey("ymax");
            if (string.IsNullOrWhiteSpace(layerName) && !hasEnv)
                return new(false, "Provide 'layer' (zoom frame to that layer) or xmin/ymin/xmax/ymax", null);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var (layout, err) = GetLayoutByName(layoutName);
                if (layout == null) return new(false, err, null);
                var (frame, ferr) = GetMapFrame(layout, frameName);
                if (frame == null) return new(false, ferr, null);

                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    var map = frame.Map;
                    if (map == null) return new(false, "Map frame has no map bound", null);
                    var layer = map.GetLayersAsFlattenedList()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    if (layer == null)
                        return new(false,
                            $"Layer not found in frame's map: {layerName}. Available: " +
                            string.Join(", ", map.GetLayersAsFlattenedList().Select(l => l.Name)), null);
                    frame.SetCamera(layer);
                    return new(true, null, new { frame = frame.Name, zoomedToLayer = layer.Name });
                }

                double xmin = ArgDouble(args, "xmin", 0), ymin = ArgDouble(args, "ymin", 0);
                double xmax = ArgDouble(args, "xmax", 0), ymax = ArgDouble(args, "ymax", 0);
                int wkid = (int)ArgDouble(args, "wkid", 0);
                SpatialReference? sr = null;
                try
                {
                    sr = wkid > 0
                        ? SpatialReferenceBuilder.CreateSpatialReference(wkid)
                        : frame.Map?.SpatialReference;
                }
                catch (Exception ex)
                {
                    return new(false, $"Invalid wkid {wkid}: {ex.Message}", null);
                }
                var env = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                frame.SetCamera(env);
                return new(true, null, new { frame = frame.Name, xmin, ymin, xmax, ymax });
            });
        }
    }
}
