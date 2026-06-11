using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
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
    /// Symbology family: native CIM renderer control for the three workhorse
    /// renderer types (simple, unique values, graduated colors). For anything
    /// fancier (heat maps, dictionary renderers, per-class symbol overrides),
    /// execute_python with arcpy.mp / CIM is the escape hatch.
    /// </summary>
    internal partial class ProBridgeService
    {
        private static CIMColor ParseColor(Dictionary<string, string>? args, string prefix, CIMColor fallback)
        {
            double r = ArgDouble(args, $"{prefix}R", -1);
            double g = ArgDouble(args, $"{prefix}G", -1);
            double b = ArgDouble(args, $"{prefix}B", -1);
            if (r < 0 || g < 0 || b < 0) return fallback;
            double a = ArgDouble(args, $"{prefix}Alpha", 100);
            return ColorFactory.Instance.CreateRGBColor(
                Math.Min(255, r), Math.Min(255, g), Math.Min(255, b), Math.Max(0, Math.Min(100, a)));
        }

        private static CIMColorRamp? FindColorRamp(string? rampName)
        {
            // System color-ramp styles: "ArcGIS Colors" carries the standard ramps.
            var styles = Project.Current?.GetItems<StyleProjectItem>().ToList()
                ?? new List<StyleProjectItem>();
            foreach (var style in styles.OrderByDescending(s => s.Name == "ArcGIS Colors"))
            {
                try
                {
                    var ramps = style.SearchColorRamps(rampName ?? "");
                    var hit = ramps?.FirstOrDefault();
                    if (hit != null) return hit.ColorRamp;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// pro.setLayerRenderer — rendererType: 'simple' | 'uniqueValues' |
        /// 'graduatedColors'. simple uses fillR/G/B + outline; the other two need
        /// 'field' (+ optional colorRamp name, breakCount for graduated).
        /// </summary>
        private static async Task<IpcResponse> HandleSetLayerRenderer(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("rendererType", out string? rendererType) ||
                string.IsNullOrWhiteSpace(rendererType))
                return new(false, "args 'layer' and 'rendererType' (simple|uniqueValues|graduatedColors) required", null);
            args.TryGetValue("map", out string? mapName);
            args.TryGetValue("field", out string? field);
            args.TryGetValue("colorRamp", out string? rampName);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = ResolveMap(mapName);
                var member = RequireMapMember(map, layerName);
                if (member is not FeatureLayer fl)
                    return new(false, $"'{member.Name}' is a {member.GetType().Name} — renderers apply to feature layers.", null);

                switch (rendererType.ToLowerInvariant())
                {
                    case "simple":
                    {
                        var fill = ParseColor(args, "fill", ColorFactory.Instance.CreateRGBColor(76, 129, 205));
                        var outlineColor = ParseColor(args, "outline", ColorFactory.Instance.CreateRGBColor(60, 60, 60));
                        double outlineWidth = ArgDouble(args, "outlineWidth", 0.7);
                        double markerSize = ArgDouble(args, "size", 8);
                        double lineWidth = ArgDouble(args, "lineWidth", 1.5);

                        CIMSymbol symbol = fl.ShapeType switch
                        {
                            esriGeometryType.esriGeometryPoint or esriGeometryType.esriGeometryMultipoint =>
                                SymbolFactory.Instance.ConstructPointSymbol(fill, markerSize, SimpleMarkerStyle.Circle),
                            esriGeometryType.esriGeometryPolyline =>
                                SymbolFactory.Instance.ConstructLineSymbol(fill, lineWidth),
                            _ => SymbolFactory.Instance.ConstructPolygonSymbol(
                                fill, SimpleFillStyle.Solid,
                                SymbolFactory.Instance.ConstructStroke(outlineColor, outlineWidth))
                        };

                        fl.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
                        return new(true, null, new { layer = fl.Name, renderer = "simple" });
                    }

                    case "uniquevalues":
                    {
                        if (string.IsNullOrWhiteSpace(field))
                            return new(false, "rendererType 'uniqueValues' requires 'field'", null);
                        var def = new UniqueValueRendererDefinition(new List<string> { field });
                        var ramp = FindColorRamp(rampName);
                        if (ramp != null) def.ColorRamp = ramp;
                        var renderer = fl.CreateRenderer(def);
                        if (renderer == null)
                            return new(false, "CreateRenderer returned null — check the field name", null);
                        fl.SetRenderer(renderer);
                        return new(true, null, new { layer = fl.Name, renderer = "uniqueValues", field });
                    }

                    case "graduatedcolors":
                    {
                        if (string.IsNullOrWhiteSpace(field))
                            return new(false, "rendererType 'graduatedColors' requires 'field' (numeric)", null);
                        int breakCount = (int)ArgDouble(args, "breakCount", 5);
                        var def = new GraduatedColorsRendererDefinition
                        {
                            ClassificationField = field,
                            ClassificationMethod = ClassificationMethod.NaturalBreaks,
                            BreakCount = Math.Max(2, Math.Min(12, breakCount))
                        };
                        var ramp = FindColorRamp(rampName ?? "Yellow to Red");
                        if (ramp != null) def.ColorRamp = ramp;
                        var renderer = fl.CreateRenderer(def);
                        if (renderer == null)
                            return new(false, "CreateRenderer returned null — is the field numeric?", null);
                        fl.SetRenderer(renderer);
                        return new(true, null, new { layer = fl.Name, renderer = "graduatedColors", field, breakCount });
                    }

                    default:
                        return new(false,
                            $"Unknown rendererType '{rendererType}'. Use simple, uniqueValues, or graduatedColors. " +
                            "For other renderer types use execute_python with arcpy.mp.", null);
                }
            });
        }

        /// <summary>pro.getLayerSymbology — summary of the current renderer.</summary>
        private static async Task<IpcResponse> HandleGetLayerSymbology(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new(false, "arg 'layer' required", null);
            args.TryGetValue("map", out string? mapName);

            return await QueuedTask.Run<IpcResponse>(() =>
            {
                var map = ResolveMap(mapName);
                var member = RequireMapMember(map, layerName);
                if (member is not FeatureLayer fl)
                    return new(false, $"'{member.Name}' is a {member.GetType().Name} — no feature renderer.", null);

                var renderer = fl.GetRenderer();
                object summary = renderer switch
                {
                    CIMUniqueValueRenderer uv => new
                    {
                        type = "uniqueValues",
                        fields = uv.Fields,
                        classCount = uv.Groups?.Sum(g => g.Classes?.Length ?? 0) ?? 0
                    },
                    CIMClassBreaksRenderer cb => new
                    {
                        type = "classBreaks",
                        field = cb.Field,
                        breakCount = cb.Breaks?.Length ?? 0,
                        method = cb.ClassificationMethod.ToString()
                    } as object,
                    CIMSimpleRenderer => new { type = "simple" } as object,
                    null => new { type = "<none>" } as object,
                    _ => new { type = renderer.GetType().Name } as object
                };

                return new(true, null, new { layer = fl.Name, renderer = summary });
            });
        }
    }
}
