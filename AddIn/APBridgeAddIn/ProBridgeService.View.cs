using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// View family: capture_map_view (the LLM's eyes), zoom/camera control, and
    /// bookmarks. capture_map_view + export_layout close the see-act-verify loop
    /// that separates scripting Pro from actually driving it.
    /// </summary>
    internal partial class ProBridgeService
    {
        /// <summary>
        /// Exports the active MAP view to a PNG image so the agent can see the
        /// current map (extent, symbology, drawn state). Layout tabs don't have
        /// a MapView — the error directs the agent to export_layout instead.
        /// </summary>
        private static async Task<IpcResponse> HandleCaptureMapView(Dictionary<string, string>? args)
        {
            string? output = null;
            args?.TryGetValue("output", out output);

            int width = 1200, height = 900;
            if (args != null && args.TryGetValue("width", out string? ws) &&
                int.TryParse(ws, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w > 0)
                width = Math.Min(w, 4096);
            if (args != null && args.TryGetValue("height", out string? hs) &&
                int.TryParse(hs, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h > 0)
                height = Math.Min(h, 4096);

            if (string.IsNullOrWhiteSpace(output))
            {
                string dir;
                try { dir = ArcGIS.Desktop.Core.Project.Current?.HomeFolderPath ?? Path.GetTempPath(); }
                catch { dir = Path.GetTempPath(); }
                var capDir = Path.Combine(dir, "mcp-captures");
                Directory.CreateDirectory(capDir);
                output = Path.Combine(capDir, $"map_view_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            }

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var view = MapView.Active;
                if (view == null)
                    return new(false,
                        "No active MAP view — a layout or catalog tab may be focused. " +
                        "Click a map tab in Pro (or use export_layout for layouts).", null);

                var png = new ArcGIS.Desktop.Mapping.PNGFormat
                {
                    OutputFileName = output,
                    Width = width,
                    Height = height,
                    Resolution = 96
                };
                if (!png.ValidateOutputFilePath())
                    return new(false, $"Invalid output path: {output}", null);

                try
                {
                    view.Export(png);
                }
                catch (Exception ex)
                {
                    return new(false, $"Map view export failed: {ex.Message}", null);
                }

                if (!File.Exists(output))
                    return new(false, $"Export reported no error but no file at {output}", null);

                var ext = view.Extent;
                return new(true, null, new
                {
                    output = Path.GetFullPath(output),
                    width,
                    height,
                    extent = ext == null ? null : (object)new
                    {
                        xmin = ext.XMin, ymin = ext.YMin, xmax = ext.XMax, ymax = ext.YMax,
                        srWkid = ext.SpatialReference?.Wkid ?? 0
                    },
                    hint = "Read the PNG file to see the map."
                });
            });
        }

        /// <summary>
        /// Zooms the active map view to an envelope. Coordinates are interpreted
        /// in the given wkid (default: the map's spatial reference).
        /// </summary>
        private static async Task<IpcResponse> HandleZoomToExtent(Dictionary<string, string>? args)
        {
            if (args == null ||
                !TryGetDouble(args, "xmin", out double xmin) ||
                !TryGetDouble(args, "ymin", out double ymin) ||
                !TryGetDouble(args, "xmax", out double xmax) ||
                !TryGetDouble(args, "ymax", out double ymax))
                return new(false, "args 'xmin', 'ymin', 'xmax', 'ymax' (numbers) required", null);

            int wkid = 0;
            if (args.TryGetValue("wkid", out string? wkidStr))
                int.TryParse(wkidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out wkid);

            return await QueuedTask.Run<IpcResponse>(async () =>
            {
                var view = MapView.Active;
                if (view == null) return new(false, "No active map view", null);

                SpatialReference sr;
                try
                {
                    sr = wkid > 0
                        ? SpatialReferenceBuilder.CreateSpatialReference(wkid)
                        : view.Map.SpatialReference;
                }
                catch (Exception ex)
                {
                    return new(false, $"Invalid wkid {wkid}: {ex.Message}", null);
                }

                var env = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                await view.ZoomToAsync(env, TimeSpan.Zero);
                return new(true, null, new { zoomed = true, xmin, ymin, xmax, ymax, srWkid = sr?.Wkid ?? 0 });
            });
        }

        /// <summary>Sets the active map view's scale (e.g., 24000 for 1:24,000).</summary>
        private static async Task<IpcResponse> HandleZoomToScale(Dictionary<string, string>? args)
        {
            if (args == null || !TryGetDouble(args, "scale", out double scale) || scale <= 0)
                return new(false, "arg 'scale' (positive number, e.g. 24000 for 1:24,000) required", null);

            return await QueuedTask.Run<IpcResponse>(async () =>
            {
                var view = MapView.Active;
                if (view == null) return new(false, "No active map view", null);
                var camera = view.Camera;
                camera.Scale = scale;
                await view.ZoomToAsync(camera, TimeSpan.Zero);
                return new(true, null, new { scale });
            });
        }

        /// <summary>Zooms to the union of all selected features in the active map.</summary>
        private static async Task<IpcResponse> HandleZoomToSelected()
        {
            return await QueuedTask.Run<IpcResponse>(async () =>
            {
                var view = MapView.Active;
                if (view == null) return new(false, "No active map view", null);
                var zoomed = await view.ZoomToSelectedAsync(TimeSpan.Zero);
                return new(true, null, new
                {
                    zoomed,
                    hint = zoomed ? null : "Nothing selected — select features first (select_by_attribute)."
                });
            });
        }

        private static async Task<IpcResponse> HandleListBookmarks(Dictionary<string, string>? args)
        {
            string? mapName = null;
            args?.TryGetValue("map", out mapName);

            var data = await QueuedTask.Run<object>(() =>
            {
                var map = ResolveMap(mapName);
                return map.GetBookmarks().Select(b => new
                {
                    name = b.Name,
                    // Extent gives the agent a usable zoom target even without
                    // calling zoom_to_bookmark.
                    extent = b.GetDefinition()?.Location == null ? null : (object)new
                    {
                        xmin = b.GetDefinition().Location.XMin,
                        ymin = b.GetDefinition().Location.YMin,
                        xmax = b.GetDefinition().Location.XMax,
                        ymax = b.GetDefinition().Location.YMax
                    }
                }).ToList();
            });
            return new(true, null, data);
        }

        private static async Task<IpcResponse> HandleZoomToBookmark(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name))
                return new(false, "arg 'name' required", null);

            return await QueuedTask.Run<IpcResponse>(async () =>
            {
                var view = MapView.Active;
                if (view == null) return new(false, "No active map view", null);
                var bookmark = view.Map.GetBookmarks()
                    .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (bookmark == null)
                    return new(false,
                        $"Bookmark not found: {name}. Available: " +
                        string.Join(", ", view.Map.GetBookmarks().Select(b => b.Name)), null);
                await view.ZoomToAsync(bookmark, TimeSpan.Zero);
                return new(true, null, new { zoomedTo = bookmark.Name });
            });
        }

        private static async Task<IpcResponse> HandleCreateBookmark(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("name", out string? name) ||
                string.IsNullOrWhiteSpace(name))
                return new(false, "arg 'name' required", null);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var view = MapView.Active;
                if (view == null) return new(false, "No active map view", null);
                var bookmark = view.Map.AddBookmark(view, name);
                return new(true, null, new { created = bookmark?.Name ?? name });
            });
        }

        private static bool TryGetDouble(Dictionary<string, string> args, string key, out double value)
        {
            value = 0;
            return args.TryGetValue(key, out string? s) &&
                   double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
