using System;
using System.Collections.Generic;

namespace APBridgeAddIn.ModelBuilder
{
    /// <summary>
    /// Shared per-tool metadata used by both the run-time executor
    /// (<see cref="ProBridgeService"/>) and the .atbx writer
    /// (<see cref="AtbxManager"/>).
    ///
    /// Pro's SDK exposes no introspection API for GP tool signatures —
    /// the docs just say "look it up". Extend the dictionaries below as
    /// new tools surface in real models.
    /// </summary>
    internal static class GpToolCatalog
    {
        /// <summary>
        /// Resolution chain for positional signatures: hand-pinned
        /// <see cref="Signatures"/> first (curated, wins on conflict), then
        /// <see cref="SystemToolboxCatalog"/> (parsed from Pro's installed system
        /// toolboxes at runtime — covers ~1700 tools). Null only for tools
        /// neither source knows (custom script tools, unlicensed extensions).
        /// </summary>
        public static string[]? ResolveSignature(string tool)
        {
            if (Signatures.TryGetValue(tool, out var pinned)) return pinned;
            return SystemToolboxCatalog.GetSignature(tool);
        }

        /// <summary>
        /// Resolution chain for canonical output slots: hand-pinned
        /// <see cref="OutputSlots"/> first, then the system catalog.
        /// </summary>
        public static (string Slot, string Type)? ResolveOutputSlot(string tool)
        {
            if (OutputSlots.TryGetValue(tool, out var pinned)) return pinned;
            return SystemToolboxCatalog.GetOutputSlot(tool);
        }

        /// <summary>
        /// Ordered slot names per <c>alias.toolName</c>. The executor uses
        /// this to remap a user's named-parameter dict back to the positional
        /// array <c>Geoprocessing.ExecuteToolAsync</c> requires; the writer
        /// uses it to canonicalize non-canonical user-supplied parameter
        /// keys before writing them into <c>tool.model</c>.
        ///
        /// For tools NOT listed here, the executor falls back to
        /// dense-packing (the old behavior); the writer passes
        /// user-supplied keys through unchanged.
        /// </summary>
        public static readonly Dictionary<string, string[]> Signatures =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["management.Project"] = new[]
                {
                    "in_dataset", "out_dataset", "out_coor_system",
                    "transform_method", "in_coor_system",
                    "preserve_shape", "max_deviation", "vertical"
                },
                ["management.CopyFeatures"] = new[]
                {
                    "in_features", "out_feature_class",
                    "config_keyword", "spatial_grid_1", "spatial_grid_2", "spatial_grid_3"
                },
                ["analysis.PairwiseErase"] = new[]
                {
                    "in_features", "erase_features", "out_feature_class", "cluster_tolerance"
                },
                ["analysis.PairwiseClip"] = new[]
                {
                    "in_features", "clip_features", "out_feature_class", "cluster_tolerance"
                },
                ["analysis.PairwiseIntersect"] = new[]
                {
                    "in_features", "out_feature_class", "join_attributes",
                    "cluster_tolerance", "output_type"
                },
                ["analysis.Identity"] = new[]
                {
                    "in_features", "identity_features", "out_feature_class",
                    "join_attributes", "cluster_tolerance", "relationship"
                },
                ["analysis.SummarizeWithin"] = new[]
                {
                    "in_polygons", "in_sum_features", "out_feature_class",
                    "keep_all_polygons", "sum_fields", "sum_shape", "shape_unit",
                    "group_field", "add_min_maj", "add_group_percent", "out_group_table"
                },
                ["analysis.Statistics"] = new[]
                {
                    "in_table", "out_table", "statistics_fields",
                    "case_field", "concatenation_separator"
                },
                ["management.SelectLayerByLocation"] = new[]
                {
                    "in_layer", "overlap_type", "select_features",
                    "search_distance", "selection_type", "invert_spatial_relationship"
                },
                ["management.SelectLayerByAttribute"] = new[]
                {
                    "in_layer_or_view", "selection_type", "where_clause",
                    "invert_where_clause"
                },
                ["management.CalculateField"] = new[]
                {
                    "in_table", "field", "expression", "expression_type",
                    "code_block", "field_type", "enforce_domains"
                },
                ["management.CalculateGeometryAttributes"] = new[]
                {
                    "in_features", "geometry_property", "length_unit", "area_unit",
                    "coordinate_system", "coordinate_format", "updated_features"
                },
                ["management.JoinField"] = new[]
                {
                    "in_data", "in_field", "join_table", "join_field",
                    "fields", "fm_option", "field_mapping", "index_join_fields"
                },
                ["management.AddField"] = new[]
                {
                    "in_table", "field_name", "field_type",
                    "field_precision", "field_scale", "field_length", "field_alias",
                    "field_is_nullable", "field_is_required", "field_domain"
                },
                ["analysis.Buffer"] = new[]
                {
                    "in_features", "out_feature_class", "buffer_distance_or_field",
                    "line_side", "line_end_type", "dissolve_option", "dissolve_field", "method"
                },
                ["analysis.Clip"] = new[]
                {
                    "in_features", "clip_features", "out_feature_class", "cluster_tolerance"
                },
                ["analysis.Intersect"] = new[]
                {
                    "in_features", "out_feature_class", "join_attributes",
                    "cluster_tolerance", "output_type"
                },
            };

        /// <summary>
        /// For each known tool: the canonical OUTPUT slot key and the default
        /// concrete data-element type Pro expects when the slot holds a
        /// derived output. Used by the writer to:
        ///   1. Canonicalize non-canonical user-supplied output keys (e.g.,
        ///      <c>out_features</c> → <c>updated_features</c> on
        ///      <c>CalculateGeometryAttributes</c>) so Pro's load-time
        ///      normalization never fires and renames the variable.
        ///   2. Coerce a <c>GPComposite</c> output type (which crashes Pro on
        ///      open) to the concrete <c>DE*</c> that belongs in this slot.
        ///
        /// In-place tools (selection, field calc) carry the same key here as
        /// their <c>in_*</c> slot — that is intentional: ModelBuilder treats
        /// the derived output of an in-place tool as a logical alias for the
        /// input. The writer's existing output recording in
        /// <see cref="AtbxManager"/> handles this correctly.
        /// </summary>
        public static readonly Dictionary<string, (string Slot, string Type)> OutputSlots =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["management.Project"]               = ("out_dataset",         "DEFeatureClass"),
                ["management.CopyFeatures"]          = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.PairwiseErase"]           = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.PairwiseClip"]            = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.PairwiseIntersect"]       = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.Identity"]                = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.SummarizeWithin"]         = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.Statistics"]              = ("out_table",           "DETable"),
                ["analysis.Buffer"]                  = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.Clip"]                    = ("out_feature_class",   "DEFeatureClass"),
                ["analysis.Intersect"]               = ("out_feature_class",   "DEFeatureClass"),
                ["management.CalculateField"]        = ("in_table",            "DETable"),
                ["management.CalculateGeometryAttributes"] = ("updated_features", "DEFeatureClass"),
                ["management.JoinField"]             = ("in_data",             "DETable"),
                ["management.AddField"]              = ("in_table",            "DETable"),
                ["management.SelectLayerByLocation"] = ("in_layer",            "GPFeatureLayer"),
                ["management.SelectLayerByAttribute"] = ("in_layer_or_view",   "GPFeatureLayer"),
            };
    }
}
