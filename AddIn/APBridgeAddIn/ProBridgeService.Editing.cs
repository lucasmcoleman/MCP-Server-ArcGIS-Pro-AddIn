using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    /// <summary>
    /// Editing family: completes CRUD. The bridge had Create (add_*_features) and
    /// Read (read_layer_attributes); update_features / delete_features close the
    /// loop using the same EditOperation pattern (undo-able, transactional).
    /// Edits land in Pro's edit session — save_edits persists them to disk.
    /// </summary>
    internal partial class ProBridgeService
    {
        // Safety ceiling for where-clause expansion: an LLM "fix all rows" with a
        // sloppy where could otherwise queue a million-row edit op.
        private const int MaxEditFeatures = 10000;

        /// <summary>
        /// Resolves target OIDs from either an explicit comma list ('oids') or a
        /// SQL where clause. Exactly one must be supplied — refusing implicit
        /// all-rows operations is deliberate (pass where="1=1" to be explicit).
        /// </summary>
        private static (List<long>? oids, string? error) ResolveTargetOids(
            ArcGIS.Core.Data.Table table, Dictionary<string, string> args)
        {
            args.TryGetValue("oids", out string? oidsStr);
            args.TryGetValue("where", out string? where);

            bool hasOids = !string.IsNullOrWhiteSpace(oidsStr);
            bool hasWhere = !string.IsNullOrWhiteSpace(where);
            if (hasOids == hasWhere)
                return (null, "Provide exactly one of 'oids' (comma-separated ObjectIDs) or 'where' " +
                              "(SQL clause; use \"1=1\" to explicitly target every row).");

            var oids = new List<long>();
            if (hasOids)
            {
                foreach (var part in oidsStr!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long oid))
                        return (null, $"'oids' entry '{part}' is not an integer ObjectID");
                    oids.Add(oid);
                }
            }
            else
            {
                var qf = new ArcGIS.Core.Data.QueryFilter { WhereClause = where! };
                using var cursor = table.Search(qf, false);
                while (cursor.MoveNext())
                {
                    if (oids.Count >= MaxEditFeatures)
                        return (null, $"WHERE clause matches more than {MaxEditFeatures} rows — " +
                                      "narrow the clause or batch the operation.");
                    using var row = cursor.Current;
                    oids.Add(row.GetObjectID());
                }
            }
            return (oids, null);
        }

        /// <summary>
        /// Coerces a JSON attribute value to the field's .NET type. Shared by
        /// update_features and the add_*_features buffer path.
        /// </summary>
        private static object? CoerceAttributeValue(ArcGIS.Core.Data.Field field, JsonNode? valueNode)
        {
            if (valueNode == null) return null;
            return field.FieldType switch
            {
                ArcGIS.Core.Data.FieldType.String => CoerceScalarToString(valueNode),
                ArcGIS.Core.Data.FieldType.Integer => valueNode.GetValue<int>(),
                ArcGIS.Core.Data.FieldType.SmallInteger => (short)valueNode.GetValue<int>(),
                ArcGIS.Core.Data.FieldType.Single => valueNode.GetValue<float>(),
                ArcGIS.Core.Data.FieldType.Double => valueNode.GetValue<double>(),
                ArcGIS.Core.Data.FieldType.Date => DateTime.Parse(
                    CoerceScalarToString(valueNode), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ArcGIS.Core.Data.FieldType.GUID or ArcGIS.Core.Data.FieldType.GlobalID =>
                    Guid.Parse(CoerceScalarToString(valueNode)),
                _ => throw new InvalidOperationException(
                    $"Field '{field.Name}' has unsupported type {field.FieldType} for attribute writes")
            };
        }

        /// <summary>
        /// pro.updateFeatures — sets attribute values on rows matched by 'where' or
        /// 'oids'. Attributes arrive as a JSON object {field: value, ...}.
        /// </summary>
        private static async Task<IpcResponse> HandleUpdateFeatures(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("attributes", out string? attrsJson) ||
                string.IsNullOrWhiteSpace(attrsJson))
                return new(false, "args 'layer' and 'attributes' (JSON object of field:value) required", null);
            args.TryGetValue("map", out string? mapName);

            JsonObject attrsObj;
            try
            {
                attrsObj = JsonNode.Parse(attrsJson) as JsonObject
                    ?? throw new InvalidOperationException("attributes must be a JSON object");
            }
            catch (Exception ex)
            {
                return new(false, $"Invalid attributes JSON: {ex.Message}", null);
            }
            if (attrsObj.Count == 0)
                return new(false, "attributes object is empty — nothing to update", null);

            return await QueuedTask.Run<IpcResponse>(async () =>
            {
                var map = ResolveMap(mapName);
                var member = RequireMapMember(map, layerName);
                using var table = GetTableFromMember(member)
                    ?? throw new InvalidOperationException(
                        $"'{member.Name}' has no attribute table — update_features works on feature layers and standalone tables.");

                var allFields = table.GetDefinition().GetFields();

                // Validate + coerce attribute values once, up front.
                var attrDict = new Dictionary<string, object?>();
                foreach (var kv in attrsObj)
                {
                    var field = allFields.FirstOrDefault(f =>
                        f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            $"Field '{kv.Key}' does not exist on '{member.Name}'. Fields: " +
                            string.Join(", ", allFields.Select(f => f.Name)));
                    if (!field.IsEditable ||
                        field.FieldType is ArcGIS.Core.Data.FieldType.OID
                            or ArcGIS.Core.Data.FieldType.Geometry
                            or ArcGIS.Core.Data.FieldType.GlobalID)
                        throw new InvalidOperationException($"Field '{field.Name}' is not editable ({field.FieldType})");
                    attrDict[field.Name] = CoerceAttributeValue(field, kv.Value);
                }

                var (oids, error) = ResolveTargetOids(table, args);
                if (error != null) return new(false, error, null);
                if (oids!.Count == 0)
                    return new(true, null, new { layer = member.Name, modified = 0, note = "no rows matched" });

                var editOp = new EditOperation
                {
                    Name = $"MCP update {oids.Count} row(s) on {member.Name}",
                    ShowProgressor = false,
                    ShowModalMessageAfterFailure = false
                };
                foreach (var oid in oids)
                {
                    // Dictionary<string, object> per Modify's signature; nulls allowed.
                    var inspectorDict = new Dictionary<string, object>();
                    foreach (var kv in attrDict) inspectorDict[kv.Key] = kv.Value!;
                    editOp.Modify(member, oid, inspectorDict);
                }

                if (!await editOp.ExecuteAsync())
                {
                    var msg = editOp.ErrorMessage ?? "";
                    // All-no-op edits (every value already equals the current value)
                    // make Execute fail by design — report as benign.
                    if (msg.Contains("no change", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("did not make any", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(msg))
                        return new(true, null, new
                        {
                            layer = member.Name,
                            modified = 0,
                            note = "edit was a no-op (values already match)"
                        });
                    return new(false, $"Edit operation failed: {msg}", null);
                }

                return new(true, null, new
                {
                    layer = member.Name,
                    modified = oids.Count,
                    oids = oids.Take(100).ToList(),
                    pendingEdits = true,
                    hint = "Edits are in Pro's edit session (undo-able). Call save_edits to persist."
                });
            });
        }

        /// <summary>pro.deleteFeatures — deletes rows matched by 'where' or 'oids'.</summary>
        private static async Task<IpcResponse> HandleDeleteFeatures(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new(false, "arg 'layer' required (plus 'where' or 'oids')", null);
            args.TryGetValue("map", out string? mapName);

            return await QueuedTask.Run<IpcResponse>(async () =>
            {
                var map = ResolveMap(mapName);
                var member = RequireMapMember(map, layerName);
                using var table = GetTableFromMember(member)
                    ?? throw new InvalidOperationException(
                        $"'{member.Name}' has no attribute table — delete_features works on feature layers and standalone tables.");

                var (oids, error) = ResolveTargetOids(table, args);
                if (error != null) return new(false, error, null);
                if (oids!.Count == 0)
                    return new(true, null, new { layer = member.Name, deleted = 0, note = "no rows matched" });

                var editOp = new EditOperation
                {
                    Name = $"MCP delete {oids.Count} row(s) from {member.Name}",
                    ShowProgressor = false,
                    ShowModalMessageAfterFailure = false
                };
                editOp.Delete(member, oids);

                if (!await editOp.ExecuteAsync())
                    return new(false, $"Delete operation failed: {editOp.ErrorMessage ?? "<no message>"}", null);

                return new(true, null, new
                {
                    layer = member.Name,
                    deleted = oids.Count,
                    pendingEdits = true,
                    hint = "Deletes are in Pro's edit session (undo-able). Call save_edits to persist."
                });
            });
        }

        /// <summary>
        /// pro.addPolylineFeatures — same contract as add_point/polygon_features:
        /// features = [{"vertices": [[x,y], ...] (>=2), "attributes": {...}}, ...].
        /// </summary>
        private static async Task<IpcResponse> HandleAddPolylineFeatures(Dictionary<string, string>? args)
        {
            if (args == null ||
                !args.TryGetValue("layer", out string? layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !args.TryGetValue("features", out string? featuresJson) ||
                string.IsNullOrWhiteSpace(featuresJson))
                return new(false, "args 'layer' and 'features' required", null);

            JsonArray featuresArray;
            try
            {
                var node = JsonNode.Parse(featuresJson);
                if (node is not JsonArray arr)
                    return new(false, "features must be a JSON array", null);
                featuresArray = arr;
            }
            catch (Exception ex)
            {
                return new(false, $"Invalid features JSON: {ex.Message}", null);
            }

            var addedOids = new List<long>();
            string actualName = string.Empty;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map
                    ?? throw new InvalidOperationException("No active map");
                var fl = map.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Layer not found: {layerName}");
                actualName = fl.Name;

                if (fl.ShapeType != esriGeometryType.esriGeometryPolyline)
                    throw new InvalidOperationException(
                        $"Layer '{fl.Name}' is not a polyline layer (geometry type: {fl.ShapeType}).");

                using var fc = fl.GetFeatureClass()
                    ?? throw new InvalidOperationException($"Layer '{fl.Name}' has no resolved feature class.");
                var fcDef = fc.GetDefinition();
                var sr = fcDef.GetSpatialReference();
                var shapeFieldName = fcDef.GetShapeField();
                var allFields = fcDef.GetFields();

                var editOp = new EditOperation
                {
                    Name = $"Add {featuresArray.Count} polyline feature(s) to {fl.Name}",
                    ShowProgressor = false,
                    ShowModalMessageAfterFailure = false
                };

                editOp.Callback(context =>
                {
                    for (int i = 0; i < featuresArray.Count; i++)
                    {
                        if (featuresArray[i] is not JsonObject obj)
                            throw new InvalidOperationException($"feature[{i}] is not a JSON object");

                        if (!obj.TryGetPropertyValue("vertices", out var vertsNode) ||
                            vertsNode is not JsonArray vertsArr || vertsArr.Count < 2)
                            throw new InvalidOperationException(
                                $"feature[{i}] requires 'vertices': a JSON array of at least 2 [x,y] pairs");

                        var points = new List<MapPoint>(vertsArr.Count);
                        for (int v = 0; v < vertsArr.Count; v++)
                        {
                            if (vertsArr[v] is not JsonArray pair || pair.Count < 2 || pair[0] is null || pair[1] is null)
                                throw new InvalidOperationException(
                                    $"feature[{i}].vertices[{v}] must be a [x, y] number pair");
                            points.Add(MapPointBuilderEx.CreateMapPoint(
                                pair[0]!.GetValue<double>(),
                                pair[1]!.GetValue<double>(),
                                sr));
                        }

                        using var rowBuffer = fc.CreateRowBuffer();
                        rowBuffer[shapeFieldName] = PolylineBuilderEx.CreatePolyline(points, sr);

                        if (obj.TryGetPropertyValue("attributes", out var attrsNode) && attrsNode is JsonObject attrs)
                            SetAttributesOnBuffer(rowBuffer, attrs, allFields, i);

                        using var feature = fc.CreateRow(rowBuffer);
                        addedOids.Add(feature.GetObjectID());
                        context.Invalidate(feature);
                    }
                }, fc);

                if (!await editOp.ExecuteAsync())
                    throw new InvalidOperationException($"Edit operation failed: {editOp.ErrorMessage}");
            });

            return new(true, null, new { layer = actualName, added = addedOids.Count, oids = addedOids });
        }

        /// <summary>pro.saveEdits / pro.discardEdits / pro.hasEdits — edit session control.</summary>
        private static async Task<IpcResponse> HandleEditSession(string action)
        {
            var project = Project.Current;
            if (project == null) return new(false, "No project currently open", null);

            switch (action)
            {
                case "save":
                    await Project.Current.SaveEditsAsync();
                    return new(true, null, new { savedEdits = true });
                case "discard":
                    await Project.Current.DiscardEditsAsync();
                    return new(true, null, new { discardedEdits = true });
                case "query":
                    return new(true, null, new { hasEdits = Project.Current.HasEdits });
                default:
                    return new(false, $"unknown edit-session action: {action}", null);
            }
        }
    }
}
