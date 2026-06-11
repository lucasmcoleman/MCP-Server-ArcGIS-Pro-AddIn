using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace APBridgeAddIn.ModelBuilder
{
    /// <summary>
    /// Runtime GP tool schema discovery from Pro's installed system toolboxes.
    ///
    /// Pro ships every system toolbox at
    /// <c>{ProInstall}\Resources\ArcToolBox\toolboxes\*.tbx</c> — each a DIRECTORY of
    /// <c>{Tool}.tool/tool.content</c> JSON files in the exact same format AtbxManager
    /// parses for .atbx archives. <c>tool.content</c>'s <c>params</c> object is keyed in
    /// arcpy POSITIONAL order (JSON declaration order — NOT <c>display_order</c>, which
    /// reorders for UI; e.g. Buffer's <c>method</c> is positionally last but display
    /// slot 5). This replaces per-tool hand-maintenance of
    /// <see cref="GpToolCatalog.Signatures"/> for the ~1700 system tools.
    ///
    /// The hand-pinned <see cref="GpToolCatalog"/> entries always WIN over this
    /// catalog (see <see cref="GpToolCatalog.ResolveSignature"/>) — the on-disk format
    /// is undocumented and could drift across Pro versions, so curated overrides stay
    /// authoritative. A startup sanity check (Buffer must parse to its 8 known slots)
    /// guards against silent format drift; on failure the catalog disables itself and
    /// the executor falls back to pre-catalog behavior.
    /// </summary>
    internal static class SystemToolboxCatalog
    {
        private static readonly object _initLock = new();
        private static Dictionary<string, string>? _aliasDirs; // alias → toolbox dir
        private static bool _disabled;

        // "alias.Tool" → parsed schema (null = definitively not found)
        private static readonly ConcurrentDictionary<string, ToolSchema?> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        internal sealed class ToolSchema
        {
            public string Alias { get; init; } = "";
            public string ToolName { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string Description { get; init; } = "";
            public List<ToolParam> Params { get; init; } = new();
        }

        internal sealed class ToolParam
        {
            public string Name { get; init; } = "";
            public string? DataType { get; init; }
            public List<string>? CompositeTypes { get; init; }
            public bool IsOutput { get; init; }
            public bool Optional { get; init; }
            public bool Derived { get; init; }
            public string? DefaultValue { get; init; }
            public List<string>? DomainValues { get; init; }
            public List<string>? Depends { get; init; }
            public string? DisplayName { get; init; }
            public string? Description { get; init; }
        }

        private static string? TryGetString(JsonNode? node)
        {
            if (node is null) return null;
            if (node is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString();
            }
            if (node is JsonArray arr && arr.Count > 0) return TryGetString(arr[0]);
            return null;
        }

        private static string? ToolboxesRoot()
        {
            // Env override first — used by out-of-process tests and unusual installs.
            var overrideDir = Environment.GetEnvironmentVariable("ARCGIS_PRO_TOOLBOXES_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir))
                return overrideDir;

            try
            {
                // The Add-In runs inside ArcGISPro.exe — resolve the install
                // relative to the host process rather than hardcoding paths.
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                var binDir = exe != null ? Path.GetDirectoryName(exe) : null;
                if (binDir != null)
                {
                    var root = Path.GetFullPath(Path.Combine(binDir, "..", "Resources", "ArcToolBox", "toolboxes"));
                    if (Directory.Exists(root)) return root;
                }
            }
            catch { }

            // Standard install path fallback (also makes the catalog usable from
            // out-of-process tooling/tests that aren't hosted in ArcGISPro.exe).
            var standard = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ArcGIS", "Pro", "Resources", "ArcToolBox", "toolboxes");
            return Directory.Exists(standard) ? standard : null;
        }

        /// <summary>Scan toolbox.content of every *.tbx dir once to map alias → dir.</summary>
        private static Dictionary<string, string>? AliasDirs()
        {
            if (_disabled) return null;
            if (_aliasDirs != null) return _aliasDirs;
            lock (_initLock)
            {
                if (_aliasDirs != null) return _aliasDirs;
                var root = ToolboxesRoot();
                if (root == null) { _disabled = true; return null; }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, "*.tbx"))
                    {
                        try
                        {
                            var contentPath = Path.Combine(dir, "toolbox.content");
                            if (!File.Exists(contentPath)) continue;
                            var node = JsonNode.Parse(File.ReadAllText(contentPath));
                            var alias = TryGetString(node?["alias"]);
                            if (!string.IsNullOrEmpty(alias) && !map.ContainsKey(alias!))
                                map[alias!] = dir;
                        }
                        catch { /* one bad toolbox shouldn't kill discovery */ }
                    }
                }
                catch { _disabled = true; return null; }

                _aliasDirs = map;

                // Sanity check against format drift: Buffer must parse to the 8
                // known slots in the known order. If Esri changes the on-disk
                // format, disable the dynamic catalog entirely rather than risk
                // feeding wrong positional orders to the executor.
                var buffer = LoadSchema("analysis", "Buffer");
                var expected = new[] { "in_features", "out_feature_class", "buffer_distance_or_field",
                    "line_side", "line_end_type", "dissolve_option", "dissolve_field", "method" };
                if (buffer == null ||
                    !buffer.Params.Select(p => p.Name).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
                {
                    _disabled = true;
                    _aliasDirs = null;
                    return null;
                }

                return _aliasDirs;
            }
        }

        public static ToolSchema? GetSchema(string aliasDotTool)
        {
            if (string.IsNullOrWhiteSpace(aliasDotTool)) return null;
            return _cache.GetOrAdd(aliasDotTool, key =>
            {
                var dot = key.IndexOf('.');
                if (dot <= 0 || dot == key.Length - 1) return null;
                var alias = key[..dot];
                var tool = key[(dot + 1)..];
                return LoadSchema(alias, tool);
            });
        }

        private static ToolSchema? LoadSchema(string alias, string tool)
        {
            var dirs = AliasDirs();
            if (dirs == null || !dirs.TryGetValue(alias, out var tbxDir)) return null;

            var toolDir = Path.Combine(tbxDir, $"{tool}.tool");
            var contentPath = Path.Combine(toolDir, "tool.content");
            if (!File.Exists(contentPath))
            {
                // Tool name casing may differ from the directory — probe insensitively.
                try
                {
                    var match = Directory.EnumerateDirectories(tbxDir, "*.tool")
                        .FirstOrDefault(d => Path.GetFileNameWithoutExtension(d)
                            .Equals(tool, StringComparison.OrdinalIgnoreCase));
                    if (match == null) return null;
                    toolDir = match;
                    contentPath = Path.Combine(toolDir, "tool.content");
                    if (!File.Exists(contentPath)) return null;
                }
                catch { return null; }
            }

            try
            {
                var content = JsonNode.Parse(File.ReadAllText(contentPath));
                if (content?["params"] is not JsonObject paramsObj) return null;

                // rc map for human-readable titles/descriptions (best effort)
                var rc = new Dictionary<string, string>();
                var rcPath = Path.Combine(toolDir, "tool.content.rc");
                if (File.Exists(rcPath))
                {
                    try
                    {
                        if (JsonNode.Parse(File.ReadAllText(rcPath))?["map"] is JsonObject rcMap)
                            foreach (var kv in rcMap)
                                rc[kv.Key] = TryGetString(kv.Value) ?? "";
                    }
                    catch { }
                }

                string Rc(string? r, string fallback) =>
                    r != null && r.StartsWith("$rc:") && rc.TryGetValue(r[4..], out var v) ? v : fallback;

                var schema = new ToolSchema
                {
                    Alias = alias,
                    ToolName = Path.GetFileNameWithoutExtension(toolDir),
                    DisplayName = Rc(TryGetString(content["displayname"]), tool),
                    Description = Rc(TryGetString(content["description"]), ""),
                };

                foreach (var p in paramsObj)
                {
                    if (p.Value is not JsonObject po) continue;
                    var dtNode = po["datatype"];
                    var dataType = TryGetString(dtNode?["type"]);

                    List<string>? compositeTypes = null;
                    if (dtNode?["datatypes"] is JsonArray subs)
                    {
                        compositeTypes = subs
                            .Select(s => TryGetString(s?["type"]) ?? TryGetString(s))
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Select(s => s!)
                            .ToList();
                        if (compositeTypes.Count == 0) compositeTypes = null;
                    }

                    List<string>? domainValues = null;
                    if (po["domain"] is JsonObject dom &&
                        TryGetString(dom["type"]) == "GPCodedValueDomain" &&
                        dom["items"] is JsonArray items)
                    {
                        domainValues = items
                            .Select(i => TryGetString(i?["value"]))
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Select(s => s!)
                            .ToList();
                        if (domainValues.Count == 0) domainValues = null;
                    }

                    List<string>? depends = null;
                    if (po["depends"] is JsonArray depArr)
                    {
                        depends = depArr.Select(d => TryGetString(d))
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Select(s => s!)
                            .ToList();
                        if (depends.Count == 0) depends = null;
                    }

                    var typeFlag = TryGetString(po["type"]); // "optional" | "derived" | absent

                    schema.Params.Add(new ToolParam
                    {
                        Name = p.Key,
                        DataType = dataType,
                        CompositeTypes = compositeTypes,
                        IsOutput = TryGetString(po["direction"]) == "out",
                        Optional = string.Equals(typeFlag, "optional", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(typeFlag, "derived", StringComparison.OrdinalIgnoreCase),
                        Derived = string.Equals(typeFlag, "derived", StringComparison.OrdinalIgnoreCase),
                        DefaultValue = TryGetString(po["value"]),
                        DomainValues = domainValues,
                        Depends = depends,
                        DisplayName = Rc(TryGetString(po["displayname"]), p.Key),
                        Description = Rc(TryGetString(po["description"]), ""),
                    });
                }

                return schema.Params.Count > 0 ? schema : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Ordered positional slot names for "alias.Tool", or null when unknown.
        /// DERIVED output params are excluded — they have no positional slot in
        /// arcpy's signature (mirrors why the executor's output pre-pass exists).
        /// </summary>
        public static string[]? GetSignature(string aliasDotTool)
        {
            var schema = GetSchema(aliasDotTool);
            if (schema == null) return null;
            var slots = schema.Params
                .Where(p => !(p.IsOutput && p.Derived))
                .Select(p => p.Name)
                .ToArray();
            return slots.Length > 0 ? slots : null;
        }

        /// <summary>
        /// Canonical output slot + concrete datatype for "alias.Tool", or null.
        /// Prefers a non-derived out param (real positional output); falls back
        /// to the first derived out param (in-place tools).
        /// </summary>
        public static (string Slot, string Type)? GetOutputSlot(string aliasDotTool)
        {
            var schema = GetSchema(aliasDotTool);
            if (schema == null) return null;
            var output = schema.Params.FirstOrDefault(p => p.IsOutput && !p.Derived)
                      ?? schema.Params.FirstOrDefault(p => p.IsOutput);
            if (output == null) return null;
            return (output.Name, output.DataType ?? "DEFeatureClass");
        }

        /// <summary>
        /// Enumerate all known toolbox aliases → directory names (for search).
        /// </summary>
        public static IReadOnlyDictionary<string, string>? GetAliasDirs() => AliasDirs();

        /// <summary>
        /// Search all system tools by keyword (matched against tool name and the
        /// toolbox alias). Returns up to <paramref name="limit"/> "alias.Tool" ids.
        /// </summary>
        public static List<(string ToolId, string Toolbox)> SearchTools(string keyword, int limit = 25)
        {
            var results = new List<(string, string)>();
            var dirs = AliasDirs();
            if (dirs == null || string.IsNullOrWhiteSpace(keyword)) return results;

            foreach (var (alias, dir) in dirs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                IEnumerable<string> toolDirs;
                try { toolDirs = Directory.EnumerateDirectories(dir, "*.tool"); }
                catch { continue; }

                foreach (var td in toolDirs)
                {
                    var name = Path.GetFileNameWithoutExtension(td);
                    if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(($"{alias}.{name}", Path.GetFileNameWithoutExtension(dir)));
                        if (results.Count >= limit) return results;
                    }
                }
            }
            return results;
        }
    }
}
