using ArcGIS.Desktop.Core.Geoprocessing;
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
    /// Analysis helpers: field statistics / unique values (the data an agent
    /// needs BEFORE it can write a correct WHERE clause or pick a renderer
    /// field), and spatial selection as a first-class op.
    /// </summary>
    internal partial class ProBridgeService
    {
        /// <summary>
        /// pro.getFieldStatistics — single-pass scan of one field: row/null
        /// counts, distinct count, top values by frequency, min/max/mean for
        /// numerics. Saves the agent from sampling rows blind to learn a
        /// field's value domain.
        /// </summary>
        private static async Task<IpcResponse> HandleGetFieldStatistics(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("field", out string? fieldName) ||
                string.IsNullOrWhiteSpace(fieldName))
                return new(false, "args 'layer' and 'field' required", null);
            args.TryGetValue("map", out string? mapName);
            args.TryGetValue("where", out string? where);

            int topN = 20;
            if (args.TryGetValue("topN", out string? topStr) &&
                int.TryParse(topStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTop))
                topN = Math.Max(1, Math.Min(100, parsedTop));

            const int distinctCap = 10000;

            var data = await QueuedTask.Run<object>(() =>
            {
                var map = ResolveMap(mapName);
                var member = RequireMapMember(map, layerName);
                using var table = GetTableFromMember(member)
                    ?? throw new InvalidOperationException(
                        $"'{member.Name}' has no attribute table.");

                var field = table.GetDefinition().GetFields()
                    .FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Field not found: {fieldName}. Fields: " +
                        string.Join(", ", table.GetDefinition().GetFields().Select(f => f.Name)));

                bool isNumeric = field.FieldType is ArcGIS.Core.Data.FieldType.Integer
                    or ArcGIS.Core.Data.FieldType.SmallInteger
                    or ArcGIS.Core.Data.FieldType.Single
                    or ArcGIS.Core.Data.FieldType.Double;

                long total = 0, nulls = 0;
                double min = double.MaxValue, max = double.MinValue, sum = 0;
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                bool distinctOverflow = false;

                var qf = new ArcGIS.Core.Data.QueryFilter
                {
                    SubFields = field.Name,
                    WhereClause = where ?? string.Empty
                };
                using (var cursor = table.Search(qf, false))
                {
                    while (cursor.MoveNext())
                    {
                        total++;
                        using var row = cursor.Current;
                        var val = row[field.Name];
                        if (val == null || val is DBNull) { nulls++; continue; }

                        if (isNumeric)
                        {
                            var d = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                            if (d < min) min = d;
                            if (d > max) max = d;
                            sum += d;
                        }

                        if (!distinctOverflow)
                        {
                            var key = val switch
                            {
                                DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
                                _ => Convert.ToString(val, CultureInfo.InvariantCulture) ?? ""
                            };
                            if (counts.TryGetValue(key, out var c)) counts[key] = c + 1;
                            else if (counts.Count < distinctCap) counts[key] = 1;
                            else distinctOverflow = true;
                        }
                    }
                }

                long nonNull = total - nulls;
                return new
                {
                    layer = member.Name,
                    field = field.Name,
                    fieldType = field.FieldType.ToString(),
                    totalRows = total,
                    nullCount = nulls,
                    distinctCount = distinctOverflow ? (object)$">{distinctCap}" : counts.Count,
                    topValues = counts
                        .OrderByDescending(kv => kv.Value)
                        .Take(topN)
                        .Select(kv => new { value = kv.Key, count = kv.Value })
                        .ToList(),
                    min = isNumeric && nonNull > 0 ? (double?)min : null,
                    max = isNumeric && nonNull > 0 ? (double?)max : null,
                    mean = isNumeric && nonNull > 0 ? (double?)(sum / nonNull) : null
                };
            });

            return new(true, null, data);
        }

        /// <summary>
        /// pro.selectByLocation — spatial selection via the
        /// SelectLayerByLocation GP tool, returning the resulting selected
        /// count. Complements select_by_attribute; results combine through
        /// selectionType (NEW_SELECTION, ADD_TO_SELECTION, SUBSET_SELECTION...).
        /// </summary>
        private static async Task<IpcResponse> HandleSelectByLocation(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("selectFeatures", out string? selectFeatures) ||
                string.IsNullOrWhiteSpace(selectFeatures))
                return new(false, "args 'layer' (target) and 'selectFeatures' (the layer whose geometry selects) required", null);

            args.TryGetValue("overlapType", out string? overlapType);
            if (string.IsNullOrWhiteSpace(overlapType)) overlapType = "INTERSECT";
            args.TryGetValue("searchDistance", out string? searchDistance);
            args.TryGetValue("selectionType", out string? selectionType);
            if (string.IsNullOrWhiteSpace(selectionType)) selectionType = "NEW_SELECTION";
            bool invert = args.TryGetValue("invert", out string? invStr)
                          && bool.TryParse(invStr, out var inv) && inv;

            // Resolve both layers up front for friendly errors (the GP error for
            // a bad layer name is comparatively cryptic).
            var resolveError = await QueuedTask.Run<string?>(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return "No active map view";
                if (FindMapMemberByName(map, layerName) == null)
                    return $"Layer not found: {layerName}. Available: " +
                           string.Join(", ", map.GetLayersAsFlattenedList().Select(l => l.Name));
                if (FindMapMemberByName(map, selectFeatures) == null)
                    return $"selectFeatures layer not found: {selectFeatures}. Available: " +
                           string.Join(", ", map.GetLayersAsFlattenedList().Select(l => l.Name));
                return null;
            });
            if (resolveError != null) return new(false, resolveError, null);

            var valueArray = Geoprocessing.MakeValueArray(
                layerName, overlapType, selectFeatures,
                string.IsNullOrWhiteSpace(searchDistance) ? "#" : searchDistance,
                selectionType,
                invert ? "INVERT" : "NOT_INVERT");
            var result = await Geoprocessing.ExecuteToolAsync(
                "management.SelectLayerByLocation", valueArray, DefaultRunEnvironments());

            if (result.IsFailed)
            {
                var messages = result.Messages.Any()
                    ? string.Join("; ", result.Messages.Select(m => m.Text))
                    : "no messages";
                return new(false, $"SelectLayerByLocation failed: {messages}", null);
            }

            var selectedCount = await QueuedTask.Run<long>(() =>
            {
                var map = MapView.Active?.Map;
                var member = map != null ? FindMapMemberByName(map, layerName) : null;
                return member switch
                {
                    FeatureLayer fl => fl.GetSelection().GetCount(),
                    ArcGIS.Desktop.Mapping.StandaloneTable st => st.GetSelection().GetCount(),
                    _ => 0
                };
            });

            return new(true, null, new
            {
                layer = layerName,
                overlapType,
                selectFeatures,
                selectionType,
                selectedCount
            });
        }
    }
}
