using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace APBridgeAddIn.ModelBuilder
{
    /// <summary>
    /// Manages reading, creating, and updating ModelBuilder models inside .atbx (ZIP) archives.
    /// Translates between a simplified Claude-friendly JSON format and the internal .atbx format.
    /// </summary>
    internal static class AtbxManager
    {
        // Inherit from JsonSerializerOptions.Default so we get the built-in
        // reflection-based TypeInfoResolver. Required because JsonNode.ToJsonString
        // delegates to Utf8JsonWriter which demands a resolver when serializing
        // JsonValueCustomized<T> instances (the type created by, e.g.,
        // `jsonArray.Add("someCSharpString")`).
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerOptions.Default)
        {
            WriteIndented = true
        };

        /// <summary>
        /// Extracts a string from a JsonNode that may be either a JsonValue
        /// (the common case) or a JsonArray (ATBX uses arrays for element_id
        /// when multiple inputs feed one parameter slot — we take the first).
        /// Returns null for null, missing, or non-scalar/non-array nodes.
        /// </summary>
        private static string? TryGetString(JsonNode? node)
        {
            if (node is null) return null;
            if (node is JsonValue v)
            {
                try { return v.GetValue<string>(); } catch { return v.ToString(); }
            }
            if (node is JsonArray arr && arr.Count > 0)
                return TryGetString(arr[0]);
            return null;
        }

        /// <summary>
        /// Builds a Pro-native datatype JSON object for a parameter. For most
        /// types this is just <c>{"type": typeName}</c>. For GPComposite — when
        /// the caller supplies the optional list of subtype names (matching
        /// Pro's native pattern of GPComposite{GPTableView, GPRasterLayer,
        /// GPMosaicLayer} for tools like CalculateField.in_table) — this also
        /// writes the nested <c>datatypes</c> array so Pro's slot validator
        /// accepts the right values at runtime.
        /// </summary>
        private static JsonObject BuildDataTypeJson(string type, JsonArray? compositeTypes)
        {
            var obj = new JsonObject { ["type"] = type };
            if (string.Equals(type, "GPComposite", StringComparison.OrdinalIgnoreCase)
                && compositeTypes != null)
            {
                var subtypes = new JsonArray();
                foreach (var sub in compositeTypes)
                {
                    var s = TryGetString(sub);
                    if (!string.IsNullOrEmpty(s))
                        subtypes.Add(new JsonObject { ["type"] = s });
                }
                if (subtypes.Count > 0)
                    obj["datatypes"] = subtypes;
            }
            return obj;
        }

        #region Read Operations

        /// <summary>
        /// Lists all tool names in a toolbox, optionally filtering to only model tools.
        /// </summary>
        public static List<Dictionary<string, string>> ListModels(string atbxPath)
        {
            var results = new List<Dictionary<string, string>>();
            using var zip = ZipFile.OpenRead(atbxPath);

            var manifestEntry = zip.GetEntry("toolbox.content");
            if (manifestEntry == null) return results;

            var manifest = ReadJsonEntry<JsonNode>(zip, "toolbox.content");
            var toolsets = manifest?["toolsets"];
            if (toolsets == null) return results;

            foreach (var toolset in toolsets.AsObject())
            {
                var tools = toolset.Value?["tools"]?.AsArray();
                if (tools == null) continue;
                foreach (var tool in tools)
                {
                    var name = TryGetString(tool);
                    if (string.IsNullOrEmpty(name)) continue;

                    var isModel = zip.GetEntry($"{name}.tool/tool.model") != null;
                    var isScript = zip.GetEntry($"{name}.tool/tool.script.execute.py") != null;
                    var toolType = isModel ? "Model" : isScript ? "Script" : "Unknown";

                    results.Add(new Dictionary<string, string>
                    {
                        ["name"] = name,
                        ["type"] = toolType,
                        ["toolset"] = toolset.Key == "<root>" ? "" : toolset.Key
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// Reads a model from .atbx and returns it in simplified Claude-friendly JSON format.
        /// </summary>
        public static string DescribeModel(string atbxPath, string modelName)
        {
            using var zip = ZipFile.OpenRead(atbxPath);

            var modelNode = ReadJsonEntry<JsonNode>(zip, $"{modelName}.tool/tool.model");
            if (modelNode == null)
                throw new Exception($"Model '{modelName}' not found in toolbox");

            var rcNode = ReadJsonEntry<JsonNode>(zip, $"{modelName}.tool/tool.content.rc");
            var displayNames = new Dictionary<string, string>();
            if (rcNode?["map"] is JsonNode map)
            {
                foreach (var kv in map.AsObject())
                    displayNames[kv.Key] = TryGetString(kv.Value) ?? "";
            }

            var contentNode = ReadJsonEntry<JsonNode>(zip, $"{modelName}.tool/tool.content");

            return SimplifyModel(modelNode, displayNames, contentNode, modelName);
        }

        /// <summary>
        /// Translates the internal .atbx model format to a simplified format for Claude.
        /// </summary>
        private static string SimplifyModel(JsonNode model, Dictionary<string, string> displayNames,
            JsonNode? toolContent, string modelName)
        {
            var variables = model["variables"]?.AsArray() ?? new JsonArray();
            var processes = model["processes"]?.AsArray() ?? new JsonArray();

            // Build element ID → info map
            var varMap = new Dictionary<string, JsonNode>();
            foreach (var v in variables)
            {
                var id = TryGetString(v?["id"]);
                if (id != null) varMap[id] = v!;
            }

            // Resolve display name from $rc:key
            string ResolveName(string? titleRef, string fallback)
            {
                if (titleRef != null && titleRef.StartsWith("$rc:"))
                {
                    var key = titleRef[4..];
                    if (displayNames.TryGetValue(key, out var name))
                        return name;
                }
                return fallback;
            }

            // Assign readable names to variables
            var varNames = new Dictionary<string, string>();
            foreach (var v in variables)
            {
                var id = TryGetString(v?["id"]);
                if (id == null) continue;
                var title = TryGetString(v?["title"]);
                var paramName = TryGetString(v?["param_name"]);
                var name = paramName ?? ResolveName(title, $"var_{id}");
                // Ensure uniqueness
                var baseName = name;
                int suffix = 2;
                while (varNames.ContainsValue(name))
                    name = $"{baseName}_{suffix++}";
                varNames[id] = name;
            }

            // Variables produced by some process (direction:"out"). A Parameter
            // variable that is ALSO produced is an OUTPUT parameter (ModelBuilder's
            // 'P' badge on a derived output) — it must NOT be listed in inputs, or
            // a describe→update round-trip turns it into a bogus input and severs
            // its output-parameter status. It's emitted on the producing step's
            // output declaration with "parameter": true instead.
            var producedVarIds = new HashSet<string>();
            foreach (var p in processes)
            {
                if (p?["params"] is not JsonObject pParams) continue;
                foreach (var slot in pParams)
                {
                    if (slot.Value is JsonObject so &&
                        TryGetString(so["direction"]) == "out")
                    {
                        var outId = TryGetString(so["element_id"]);
                        if (outId != null) producedVarIds.Add(outId);
                    }
                }
            }

            // Build inputs list (Parameter variables not produced by any process)
            var inputs = new JsonArray();
            foreach (var v in variables)
            {
                var id = TryGetString(v?["id"]);
                if (id == null) continue;
                if (TryGetString(v?["connection_type"]) != "Parameter") continue;
                if (producedVarIds.Contains(id)) continue; // output parameter — emitted on its step

                var input = new JsonObject
                {
                    ["name"] = varNames[id]
                };

                // Only emit "type" when datatype is explicitly declared. Pro
                // OMITS datatype on Parameter variables whose type is slot-
                // derived (e.g., a Field parameter wired into CalculateField.
                // field — Pro derives type+dependency from the system_tool's
                // slot definition at load time). Previously we fell back to
                // "GPString" when datatype was absent, which misrepresented
                // slot-derived params as plain strings to agents. Agents then
                // echoed "GPString" back via update_model, baking an explicit
                // type that overrode Pro's slot inference and broke validation
                // (ERROR 000860: "Zone field is not the type of Field").
                //
                // Read tool.content first (Pro's authoritative public-interface
                // declaration — has full datatype info even when slot-derived),
                // fall back to the variable's datatype for explicit-typed
                // params written by older clients.
                var contentDatatypeNode = toolContent?["params"]?[varNames[id]]?["datatype"];
                var datatypeNode = contentDatatypeNode ?? v?["datatype"];
                var typeStr = TryGetString(datatypeNode?["type"]);
                if (typeStr != null)
                {
                    input["type"] = typeStr;

                    // For GPComposite, surface the nested subtype list so
                    // agents can round-trip it via update_model's
                    // "compositeTypes" field without losing the structure.
                    if (string.Equals(typeStr, "GPComposite", StringComparison.OrdinalIgnoreCase))
                    {
                        var subs = datatypeNode?["datatypes"]?.AsArray();
                        if (subs != null && subs.Count > 0)
                        {
                            var compositeArr = new JsonArray();
                            foreach (var sub in subs)
                            {
                                var subType = TryGetString(sub?["type"]);
                                if (!string.IsNullOrEmpty(subType))
                                    compositeArr.Add(subType);
                            }
                            if (compositeArr.Count > 0)
                                input["compositeTypes"] = compositeArr;
                        }
                    }
                }

                // Read parameter dependencies from tool.content. These declare
                // which other input params a Field-like parameter validates
                // against (e.g., TieLine_ID_Field "depends" on TieLineCorridors).
                // Surfacing this lets agents preserve dependencies on round-trip
                // and create new Field params correctly.
                var depsNode = toolContent?["params"]?[varNames[id]]?["depends"]?.AsArray();
                if (depsNode != null && depsNode.Count > 0)
                {
                    var depsArr = new JsonArray();
                    foreach (var dep in depsNode)
                    {
                        var depName = TryGetString(dep);
                        if (!string.IsNullOrEmpty(depName))
                            depsArr.Add(depName);
                    }
                    if (depsArr.Count > 0)
                        input["dependencies"] = depsArr;
                }

                var value = TryGetString(v?["value"]);
                if (value != null)
                    input["default"] = value;

                var title = TryGetString(v?["title"]);
                var displayName = ResolveName(title, varNames[id]);
                if (displayName != varNames[id])
                    input["displayName"] = displayName;

                inputs.Add(input);
            }

            // Build steps list (Processes)
            var steps = new JsonArray();
            foreach (var p in processes)
            {
                var processId = TryGetString(p?["id"]);
                var title = TryGetString(p?["title"]);
                // Same four-shape detection as WalkModel — keep them in lockstep.
                var systemTool = TryGetString(p?["system_tool"]);
                var modelTool  = TryGetString(p?["model_tool"]);
                var toolType   = TryGetString(p?["tool_type"]);
                var toolPath   = TryGetString(p?["path"]);
                string tool; string kind;
                if (systemTool != null) { tool = systemTool;             kind = "gpTool"; }
                else if (toolType == "ScriptTool") { tool = toolPath ?? "unknown"; kind = "scriptTool"; }
                else if (toolType == "ModelTool")  { tool = toolPath ?? "unknown"; kind = "nestedModel"; }
                else if (modelTool != null)        { tool = modelTool;              kind = "iterator"; }
                else                                { tool = "unknown";             kind = "unknown"; }

                var step = new JsonObject
                {
                    ["name"] = ResolveName(title, $"Step_{processId}"),
                    ["tool"] = tool,
                    ["kind"] = kind
                };

                // Translate parameters
                var paramsNode = p?["params"];
                if (paramsNode != null)
                {
                    var parameters = new JsonObject();
                    foreach (var param in paramsNode.AsObject())
                    {
                        var paramVal = param.Value;
                        if (paramVal == null) continue;

                        if (paramVal is JsonObject paramObj)
                        {
                            var direction = TryGetString(paramObj["direction"]);
                            var elementId = TryGetString(paramObj["element_id"]);
                            var value = TryGetString(paramObj["value"]);

                            // Multi-input slots (Merge.inputs etc.) store element_id
                            // as a JSON array — surface ALL names, or a round-trip
                            // silently deletes every input but the first.
                            JsonArray? multiRefNames = null;
                            if (paramObj["element_id"] is JsonArray idArr)
                            {
                                var names = idArr.Select(n => TryGetString(n))
                                    .Where(s => !string.IsNullOrEmpty(s))
                                    .Select(s => varNames.GetValueOrDefault(s!, $"var_{s}"))
                                    .ToList();
                                if (names.Count > 1)
                                {
                                    multiRefNames = new JsonArray();
                                    foreach (var n in names) multiRefNames.Add(n);
                                }
                            }

                            if (direction == "out" && elementId != null)
                            {
                                var outputName = varNames.GetValueOrDefault(elementId, $"output_{elementId}");
                                var outputType = varMap.ContainsKey(elementId)
                                    ? TryGetString(varMap[elementId]["datatype"]?["type"]) ?? "DEFeatureClass"
                                    : "DEFeatureClass";
                                var outputObj = new JsonObject
                                {
                                    ["output"] = outputName,
                                    ["type"] = outputType
                                };
                                // Surface the variable's stored path (if any). Without this,
                                // an output declared with an explicit path in the original
                                // model is reported as pathless on round-trip, and the
                                // write side has no way to preserve it.
                                var outputValue = varMap.ContainsKey(elementId)
                                    ? TryGetString(varMap[elementId]["value"])
                                    : null;
                                if (outputValue != null)
                                    outputObj["value"] = outputValue;
                                // Output parameter ('P' badge on a derived output):
                                // round-trips via this flag; the writer restores
                                // param_name + connection_type on the variable.
                                if (varMap.ContainsKey(elementId) &&
                                    TryGetString(varMap[elementId]["connection_type"]) == "Parameter")
                                    outputObj["parameter"] = true;
                                parameters[param.Key] = outputObj;
                            }
                            else if (multiRefNames != null)
                            {
                                parameters[param.Key] = new JsonObject { ["ref"] = multiRefNames };
                            }
                            else if (elementId != null)
                            {
                                parameters[param.Key] = new JsonObject
                                {
                                    ["ref"] = varNames.GetValueOrDefault(elementId, $"var_{elementId}")
                                };
                            }
                            else if (value != null)
                            {
                                parameters[param.Key] = value;
                            }
                            else
                            {
                                // Complex param - serialize as-is
                                parameters[param.Key] = paramVal.DeepClone();
                            }
                        }
                        else
                        {
                            // Literal string value
                            parameters[param.Key] = paramVal.DeepClone();
                        }
                    }
                    step["parameters"] = parameters;
                }

                // Include environment settings if present
                var envNode = p?["environments"];
                if (envNode != null)
                {
                    var environments = new JsonObject();
                    foreach (var env in envNode.AsObject())
                    {
                        if (env.Value is JsonObject envObj)
                        {
                            var elementId = TryGetString(envObj["element_id"]);
                            var value = TryGetString(envObj["value"]);
                            if (elementId != null)
                                environments[env.Key] = new JsonObject { ["ref"] = varNames.GetValueOrDefault(elementId, $"var_{elementId}") };
                            else if (value != null)
                                environments[env.Key] = value;
                        }
                    }
                    if (environments.Count > 0)
                        step["environments"] = environments;
                }

                // Preconditions: ordering-only dependencies (no data link). Emitted
                // as variable names; the writer maps them back to element ids. Lost
                // preconditions silently re-order execution, so round-trip them.
                var preNode = p?["precondition"] ?? p?["preconditions"];
                if (preNode != null)
                {
                    var preArr = new JsonArray();
                    if (preNode is JsonArray pa)
                    {
                        foreach (var pn in pa)
                        {
                            var pid2 = TryGetString(pn);
                            if (!string.IsNullOrEmpty(pid2))
                                preArr.Add(varNames.GetValueOrDefault(pid2!, $"var_{pid2}"));
                        }
                    }
                    else
                    {
                        var pid2 = TryGetString(preNode);
                        if (!string.IsNullOrEmpty(pid2))
                            preArr.Add(varNames.GetValueOrDefault(pid2!, $"var_{pid2}"));
                    }
                    if (preArr.Count > 0)
                        step["preconditions"] = preArr;
                }

                steps.Add(step);
            }

            // Build description — tool.content stores "$rc:description" as a
            // pointer into tool.content.rc's map; resolve it the same way we
            // resolve variable titles so the round-trip returns the real text.
            var descRaw = TryGetString(toolContent?["description"]);
            var description = descRaw != null ? ResolveName(descRaw, descRaw) : "";

            var result = new JsonObject
            {
                ["name"] = modelName,
                ["description"] = description,
                ["inputs"] = inputs,
                ["steps"] = steps
            };

            return result.ToJsonString(JsonOpts);
        }

        /// <summary>
        /// Parses the .atbx model into a graph suitable for step-by-step execution.
        /// Unlike <see cref="DescribeModel"/>, this preserves variable IDs and the
        /// raw param structure (refs vs. literals vs. outputs) so that the executor
        /// can resolve refs against a runtime variable map. Processes are returned
        /// in topological order (a process appears after every process whose output
        /// it consumes).
        ///
        /// Iterators / nested-model processes (anything that has model_tool but no
        /// system_tool) are flagged as IsIterator so the executor can reject them
        /// with a clear message — step-by-step semantics don't apply to iteration.
        /// </summary>
        public static ModelGraph WalkModel(string atbxPath, string modelName)
        {
            using var zip = ZipFile.OpenRead(atbxPath);
            var modelNode = ReadJsonEntry<JsonNode>(zip, $"{modelName}.tool/tool.model")
                ?? throw new Exception($"Model '{modelName}' not found in toolbox");
            var rcNode = ReadJsonEntry<JsonNode>(zip, $"{modelName}.tool/tool.content.rc");

            var displayNames = new Dictionary<string, string>();
            if (rcNode?["map"] is JsonNode map)
                foreach (var kv in map.AsObject())
                    displayNames[kv.Key] = TryGetString(kv.Value) ?? "";

            string Resolve(string? titleRef, string fallback)
            {
                if (titleRef != null && titleRef.StartsWith("$rc:"))
                {
                    var key = titleRef[4..];
                    if (displayNames.TryGetValue(key, out var name)) return name;
                }
                return fallback;
            }

            // ---- Variables ----
            var variables = new Dictionary<string, ModelVariable>();
            foreach (var v in modelNode["variables"]?.AsArray() ?? new JsonArray())
            {
                var id = TryGetString(v?["id"]);
                if (id == null) continue;
                var paramName = TryGetString(v?["param_name"]);
                var title = TryGetString(v?["title"]);
                var name = paramName ?? Resolve(title, $"var_{id}");
                variables[id] = new ModelVariable
                {
                    Id = id,
                    Name = name,
                    Type = TryGetString(v?["datatype"]?["type"]),
                    StoredValue = TryGetString(v?["value"]),
                    IsParameter = TryGetString(v?["connection_type"]) == "Parameter",
                    IsDerived = TryGetString(v?["derived"]) == "true",
                };
            }

            // ---- Processes (unsorted) + producing-process map ----
            var producers = new Dictionary<string, string>(); // variable id → process id that outputs it
            var processList = new List<ModelProcess>();
            foreach (var p in modelNode["processes"]?.AsArray() ?? new JsonArray())
            {
                var pid = TryGetString(p?["id"]);
                if (pid == null) continue;
                var systemTool = TryGetString(p?["system_tool"]);
                var modelTool  = TryGetString(p?["model_tool"]);
                var toolType   = TryGetString(p?["tool_type"]);
                var toolPath   = TryGetString(p?["path"]);
                // Process header takes one of four shapes:
                //   {system_tool:"alias.toolName"}             → built-in GP tool
                //   {tool_type:"ScriptTool", path:"Name"}      → custom script tool by name
                //   {tool_type:"ModelTool",  path:"Name"}      → nested model by name
                //   {model_tool:"..."}                         → legacy iterator (and very old nested-model encoding)
                ToolKind kind;
                string tool;
                if (systemTool != null) { tool = systemTool;             kind = ToolKind.GpTool; }
                else if (toolType == "ScriptTool") { tool = toolPath ?? "unknown"; kind = ToolKind.ScriptTool; }
                else if (toolType == "ModelTool")  { tool = toolPath ?? "unknown"; kind = ToolKind.NestedModel; }
                else if (modelTool != null)        { tool = modelTool;              kind = ToolKind.Iterator; }
                else                                { tool = "unknown";             kind = ToolKind.Unknown; }
                var name = Resolve(TryGetString(p?["title"]), $"Step_{pid}");

                // Parse params preserving JSON insertion order (= tool-declared slot
                // order). Case-insensitive keys: agent-authored definitions may use
                // non-canonical casing ("In_Features") and the executor's signature
                // walk must still find them.
                var paramsDict = new Dictionary<string, ModelParam>(StringComparer.OrdinalIgnoreCase);
                var paramsNode = p?["params"];
                if (paramsNode is JsonObject paramsObj)
                {
                    foreach (var slot in paramsObj)
                    {
                        var pv = slot.Value;
                        if (pv is JsonObject pvo)
                        {
                            var direction = TryGetString(pvo["direction"]);
                            var elementId = TryGetString(pvo["element_id"]);
                            var literal = TryGetString(pvo["value"]);

                            // element_id may be a JSON ARRAY when multiple inputs
                            // feed one slot (Merge.inputs etc.) — capture all ids,
                            // not just the first, or round-trip + execution silently
                            // drop the extra inputs.
                            List<string>? elementIds = null;
                            if (pvo["element_id"] is JsonArray idArr)
                            {
                                elementIds = idArr.Select(n => TryGetString(n))
                                    .Where(s => !string.IsNullOrEmpty(s))
                                    .Select(s => s!)
                                    .ToList();
                                if (elementIds.Count < 2) elementIds = null;
                            }

                            if (direction == "out" && elementId != null)
                            {
                                paramsDict[slot.Key] = new ModelParam { OutputVariableId = elementId };
                                producers[elementId] = pid;
                            }
                            else if (elementId != null)
                            {
                                paramsDict[slot.Key] = new ModelParam
                                {
                                    RefVariableId = elementId,
                                    RefVariableIds = elementIds
                                };
                            }
                            else if (literal != null)
                            {
                                paramsDict[slot.Key] = new ModelParam { LiteralValue = literal };
                            }
                            else
                            {
                                paramsDict[slot.Key] = new ModelParam { RawValue = pv.DeepClone() };
                            }
                        }
                        else if (pv is JsonValue jv)
                        {
                            paramsDict[slot.Key] = new ModelParam { LiteralValue = TryGetString(jv) ?? "" };
                        }
                        else
                        {
                            paramsDict[slot.Key] = new ModelParam { LiteralValue = "" };
                        }
                    }
                }

                // Preconditions: explicit ordering edges with no data link. Stored
                // as a process-level element-id list (single id or array). Without
                // these the topo sort can run a consumer before the selection /
                // AddField / Delete step it depends on.
                var preconditionIds = new List<string>();
                var preNode = p?["precondition"] ?? p?["preconditions"];
                if (preNode is JsonArray preArr)
                {
                    foreach (var pn in preArr)
                    {
                        var pidRef = TryGetString(pn);
                        if (!string.IsNullOrEmpty(pidRef)) preconditionIds.Add(pidRef!);
                    }
                }
                else if (preNode != null)
                {
                    var pidRef = TryGetString(preNode);
                    if (!string.IsNullOrEmpty(pidRef)) preconditionIds.Add(pidRef!);
                }

                // Per-step environment overrides (extent, cellSize, mask, ...).
                // Same {element_id}/{value} slot shapes as params. RunModelCore
                // merges these over the default run environments per step.
                Dictionary<string, ModelParam>? envDict = null;
                if (p?["environments"] is JsonObject envObj)
                {
                    envDict = new Dictionary<string, ModelParam>(StringComparer.OrdinalIgnoreCase);
                    foreach (var env in envObj)
                    {
                        if (env.Value is JsonObject evo)
                        {
                            var envRef = TryGetString(evo["element_id"]);
                            var envVal = TryGetString(evo["value"]);
                            if (envRef != null)
                                envDict[env.Key] = new ModelParam { RefVariableId = envRef };
                            else if (envVal != null)
                                envDict[env.Key] = new ModelParam { LiteralValue = envVal };
                            else
                                envDict[env.Key] = new ModelParam { RawValue = evo.DeepClone() };
                        }
                        else if (env.Value is JsonValue evv)
                        {
                            envDict[env.Key] = new ModelParam { LiteralValue = TryGetString(evv) ?? "" };
                        }
                    }
                    if (envDict.Count == 0) envDict = null;
                }

                processList.Add(new ModelProcess
                {
                    Id = pid,
                    Name = name,
                    Tool = tool,
                    Kind = kind,
                    // Step-by-step execution doesn't have semantics for anything
                    // outside GpTool. The executor's iterator-reject path covers
                    // all non-GP kinds; keep IsIterator true for them so existing
                    // callers (and the executor's FirstOrDefault check) stay correct.
                    IsIterator = kind != ToolKind.GpTool,
                    Params = paramsDict,
                    PreconditionVariableIds = preconditionIds,
                    Environments = envDict,
                });
            }

            // ---- Topological sort ----
            // Edge: process P depends on process Q if any input ref of P resolves
            // to a variable produced by Q. Kahn's algorithm — stable order.
            var depCount = new Dictionary<string, int>();
            var consumers = new Dictionary<string, List<string>>(); // process id → ids of processes that depend on it
            foreach (var proc in processList)
            {
                depCount[proc.Id] = 0;
                consumers[proc.Id] = new List<string>();
            }
            foreach (var proc in processList)
            {
                // Collect every variable this process depends on: all param refs
                // (including each id of a multi-input slot) plus preconditions.
                var dependsOnVarIds = proc.Params.Values
                    .SelectMany(pm => pm.AllRefIds)
                    .Concat(proc.PreconditionVariableIds)
                    .Concat(proc.Environments?.Values.SelectMany(pm => pm.AllRefIds)
                            ?? Enumerable.Empty<string>());

                foreach (var refId in dependsOnVarIds)
                {
                    if (producers.TryGetValue(refId, out var producerId) &&
                        producerId != proc.Id)
                    {
                        depCount[proc.Id]++;
                        consumers[producerId].Add(proc.Id);
                    }
                }
            }

            var sorted = new List<ModelProcess>();
            var ready = new Queue<ModelProcess>(processList.Where(p => depCount[p.Id] == 0));
            while (ready.Count > 0)
            {
                var proc = ready.Dequeue();
                sorted.Add(proc);
                foreach (var consumerId in consumers[proc.Id])
                {
                    if (--depCount[consumerId] == 0)
                        ready.Enqueue(processList.First(p => p.Id == consumerId));
                }
            }

            // If sorted.Count < processList.Count, there's a cycle. ModelBuilder
            // forbids cycles in normal authoring, but if one ever appears we'd
            // rather surface it than silently drop processes.
            if (sorted.Count < processList.Count)
            {
                var dropped = processList.Where(p => !sorted.Any(s => s.Id == p.Id))
                    .Select(p => p.Name);
                throw new Exception(
                    $"Model '{modelName}' has a dependency cycle — could not topologically sort. " +
                    $"Unreachable processes: {string.Join(", ", dropped)}");
            }

            return new ModelGraph { Variables = variables, Processes = sorted };
        }

        #endregion

        #region Create Operations

        /// <summary>
        /// Creates a new empty .atbx toolbox file. Refuses to overwrite an
        /// existing toolbox unless <paramref name="overwrite"/> is explicitly
        /// true — FileMode.Create would silently truncate the ZIP and destroy
        /// every model inside it, which is unrecoverable data loss from a
        /// name collision an agent can easily make.
        /// </summary>
        public static void CreateToolbox(string path, string displayName, bool overwrite = false)
        {
            if (File.Exists(path) && !overwrite)
                throw new Exception(
                    $"Toolbox already exists: {path}. It may contain models — creating it again " +
                    "would destroy them. Pass overwrite=true only if you intend to replace it, " +
                    "or use list_models to inspect the existing toolbox.");

            var alias = new string(displayName.Where(c => char.IsLetterOrDigit(c)).ToArray()) + "atbx";

            var manifest = new JsonObject
            {
                ["version"] = "1.0",
                ["alias"] = alias,
                ["displayname"] = "$rc:title",
                ["toolsets"] = new JsonObject
                {
                    ["<root>"] = new JsonObject
                    {
                        ["tools"] = new JsonArray()
                    }
                }
            };

            var rc = new JsonObject
            {
                ["map"] = new JsonObject
                {
                    ["title"] = displayName
                }
            };

            using var fileStream = new FileStream(path, FileMode.Create);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Create);
            WriteJsonEntry(zip, "toolbox.content", manifest);
            WriteJsonEntry(zip, "toolbox.content.rc", rc);
        }

        /// <summary>
        /// Creates a new model in an existing toolbox from a simplified definition.
        /// </summary>
        public static void CreateModel(string atbxPath, string definitionJson)
        {
            var def = JsonNode.Parse(definitionJson)
                ?? throw new Exception("Invalid model definition JSON");

            var modelName = def["name"]?.GetValue<string>()
                ?? throw new Exception("Model definition must have a 'name' field");

            // Generate all internal files from the simplified definition
            var (toolModel, toolContent, toolContentRc, diagram, diagramXml) =
                GenerateModelFiles(def, modelName);

            // Write to the .atbx ZIP via temp + atomic-rename. See
            // WriteAtbxAtomically for the rationale (canvas-open deadlock).
            WriteAtbxAtomically(atbxPath, zip =>
            {
                // CRITICAL: Read the existing manifest BEFORE any writes. In
                // ZipArchive Update mode, reading a pre-existing entry after
                // writing to the archive can silently return an empty stream.
                var existingManifestJson = ReadEntryTextOrDefault(zip, "toolbox.content",
                    "{\"version\":\"1.0\",\"toolsets\":{\"<root>\":{\"tools\":[]}}}");

                var folder = $"{modelName}.tool";

                // Remove existing entries if overwriting
                RemoveEntryIfExists(zip, $"{folder}/tool.model");
                RemoveEntryIfExists(zip, $"{folder}/tool.content");
                RemoveEntryIfExists(zip, $"{folder}/tool.content.rc");
                RemoveEntryIfExists(zip, $"{folder}/tool.model.diagram");
                RemoveEntryIfExists(zip, $"{folder}/tool.model.diagram.xml");
                RemoveEntryIfExists(zip, "toolbox.content");

                WriteStringEntry(zip, $"{folder}/tool.model", toolModel);
                WriteStringEntry(zip, $"{folder}/tool.content", toolContent);
                WriteStringEntry(zip, $"{folder}/tool.content.rc", toolContentRc);
                WriteStringEntry(zip, $"{folder}/tool.model.diagram", diagram);
                WriteStringEntry(zip, $"{folder}/tool.model.diagram.xml", diagramXml);

                // Compute updated manifest from the pre-read content, then write.
                var updatedManifest = AddToolToManifestJson(existingManifestJson, modelName);
                WriteStringEntry(zip, "toolbox.content", updatedManifest.ToJsonString(JsonOpts));
            });
        }

        /// <summary>
        /// Surgical write: sets (or clears) the default value of a model input
        /// parameter without regenerating the whole model. Touches only the one
        /// variable's <c>value</c> field inside <c>tool.model</c>; every other
        /// variable, every process, the diagram, and every other ZIP entry
        /// stay byte-identical. Designed so a one-field edit cannot re-trigger
        /// any of the round-trip behaviors that <see cref="UpdateModel"/>
        /// inherits from <see cref="GenerateModelFiles"/> (slot canonicalization,
        /// `_2` suffix on name collisions, etc.).
        ///
        /// Pass an empty <paramref name="defaultValue"/> to clear an existing
        /// default. Throws if the parameter doesn't exist or isn't a Parameter
        /// variable (e.g., caller targeted a derived output by mistake).
        /// </summary>
        public static void SetParameterDefault(string atbxPath, string modelName, string parameterName, string defaultValue)
        {
            var entryPath = $"{modelName}.tool/tool.model";
            WriteAtbxAtomically(atbxPath, zip =>
            {
                // Read first, then mutate, then remove + rewrite. Same pattern as
                // CreateModel: in Update mode, reading a pre-existing entry after
                // writing has corrupted entries in our past — read everything we
                // need up front.
                var modelText = ReadEntryTextOrDefault(zip, entryPath, "");
                if (string.IsNullOrWhiteSpace(modelText))
                    throw new Exception($"Model '{modelName}' not found in toolbox");
                var modelNode = JsonNode.Parse(modelText)
                    ?? throw new Exception($"Model '{modelName}' tool.model is not valid JSON");

                var variables = modelNode["variables"]?.AsArray()
                    ?? throw new Exception($"Model '{modelName}' has no variables array");

                JsonNode? target = null;
                foreach (var v in variables)
                {
                    if (v == null) continue;
                    if (TryGetString(v["param_name"]) == parameterName)
                    {
                        target = v;
                        break;
                    }
                }
                if (target == null)
                    throw new Exception(
                        $"No input parameter named '{parameterName}' in model '{modelName}'. " +
                        $"Available parameter names: {string.Join(", ", variables.Where(v => TryGetString(v?["connection_type"]) == "Parameter").Select(v => TryGetString(v?["param_name"]) ?? "?"))}");
                if (TryGetString(target["connection_type"]) != "Parameter")
                    throw new Exception(
                        $"Variable '{parameterName}' exists but is not a model Parameter — " +
                        $"set_parameter_default only modifies exposed input parameters. " +
                        $"Use set_step_parameter for step-level values.");

                target["value"] = defaultValue ?? "";

                RemoveEntryIfExists(zip, entryPath);
                WriteStringEntry(zip, entryPath, modelNode.ToJsonString(JsonOpts));
            });
        }

        /// <summary>
        /// Surgical write: sets the value of one parameter on one step without
        /// regenerating the whole model. Same byte-identical-everything-else
        /// guarantee as <see cref="SetParameterDefault"/>.
        ///
        /// <paramref name="paramValue"/> is a JSON value with one of three shapes:
        ///   <c>{"ref": "VariableName"}</c> → wires the param to that variable
        ///     (looked up by name); writes <c>{"element_id": &lt;id&gt;}</c>.
        ///   <c>{"value": "literal"}</c> or a bare string → writes
        ///     <c>{"value": ...}</c>.
        /// Output declarations (<c>{"output": ..., "type": ..., "value": ...}</c>)
        /// are NOT accepted here — adding or removing a step's output reshapes
        /// the graph; that's <c>add_step</c>/<c>remove_step</c> territory.
        /// </summary>
        public static void SetStepParameter(string atbxPath, string modelName, string stepName, string paramKey, string paramValueJson)
        {
            var entryPath = $"{modelName}.tool/tool.model";
            var rcPath    = $"{modelName}.tool/tool.content.rc";
            WriteAtbxAtomically(atbxPath, zip => SetStepParameterInZip(zip, entryPath, rcPath, modelName, stepName, paramKey, paramValueJson));
        }

        private static void SetStepParameterInZip(ZipArchive zip, string entryPath, string rcPath, string modelName, string stepName, string paramKey, string paramValueJson)
        {
            var modelText = ReadEntryTextOrDefault(zip, entryPath, "");
            if (string.IsNullOrWhiteSpace(modelText))
                throw new Exception($"Model '{modelName}' not found in toolbox");
            var modelNode = JsonNode.Parse(modelText)
                ?? throw new Exception($"Model '{modelName}' tool.model is not valid JSON");
            var rcText = ReadEntryTextOrDefault(zip, rcPath, "{}");
            var rcNode = JsonNode.Parse(rcText);

            // Build a name → rc-key map so we can resolve a process whose title
            // is a $rc reference back to its rendered display name.
            var displayNames = new Dictionary<string, string>();
            if (rcNode?["map"] is JsonObject rcMap)
                foreach (var kv in rcMap)
                    displayNames[kv.Key] = TryGetString(kv.Value) ?? "";

            string ResolveTitle(string? titleRef, string fallback)
            {
                if (titleRef != null && titleRef.StartsWith("$rc:"))
                {
                    var key = titleRef[4..];
                    if (displayNames.TryGetValue(key, out var name)) return name;
                }
                return fallback;
            }

            var processes = modelNode["processes"]?.AsArray()
                ?? throw new Exception($"Model '{modelName}' has no processes array");

            // Fallback display name must be "Step_<id>" to match what
            // DescribeModel/WalkModel show for title-less processes — otherwise
            // an agent copies "Step_5" from describe_model and we'd only match "5".
            JsonObject? targetProcess = null;
            foreach (var p in processes)
            {
                if (p is not JsonObject po) continue;
                var name = ResolveTitle(TryGetString(po["title"]), $"Step_{TryGetString(po["id"]) ?? "?"}");
                if (name == stepName) { targetProcess = po; break; }
            }
            if (targetProcess == null)
                throw new Exception(
                    $"No step named '{stepName}' in model '{modelName}'. " +
                    $"Available step names: {string.Join(", ", processes.Select(p => ResolveTitle(TryGetString(p?["title"]), $"Step_{TryGetString(p?["id"]) ?? "?"}")))}");

            var paramsObj = targetProcess["params"]?.AsObject()
                ?? throw new Exception($"Step '{stepName}' has no params object");
            if (!paramsObj.ContainsKey(paramKey))
                throw new Exception(
                    $"Step '{stepName}' has no parameter '{paramKey}'. " +
                    $"Available: {string.Join(", ", paramsObj.Select(kv => kv.Key))}");

            // Parse the user-supplied param value JSON. Accept either an object
            // shape {ref:...} / {value:...} or a bare string (treated as value).
            var input = JsonNode.Parse(paramValueJson)
                ?? throw new Exception("paramValue must be a non-null JSON value");

            JsonNode newSlot;
            if (input is JsonObject inObj)
            {
                if (inObj.ContainsKey("output"))
                    throw new Exception(
                        "set_step_parameter does not accept output declarations — " +
                        "adding or changing a step's derived output reshapes the model graph. " +
                        "Use add_step / remove_step for that.");

                if (inObj["ref"] is JsonNode refNode)
                {
                    var refName = TryGetString(refNode)
                        ?? throw new Exception("'ref' must be a string variable name");
                    var variables = modelNode["variables"]?.AsArray()
                        ?? throw new Exception("Model has no variables array");
                    string? refId = null;
                    foreach (var v in variables)
                    {
                        if (v == null) continue;
                        var pname = TryGetString(v["param_name"]);
                        if (pname == refName) { refId = TryGetString(v["id"]); break; }
                        // Also check rc-resolved title for derived outputs.
                        var resolved = ResolveTitle(TryGetString(v["title"]), pname ?? "");
                        if (resolved == refName) { refId = TryGetString(v["id"]); break; }
                    }
                    if (refId == null)
                        throw new Exception($"ref '{refName}' does not match any variable in the model");
                    newSlot = new JsonObject { ["element_id"] = refId };
                }
                else if (inObj["value"] is JsonNode valNode)
                {
                    newSlot = new JsonObject { ["value"] = TryGetString(valNode) ?? "" };
                }
                else
                {
                    throw new Exception("paramValue object must contain either 'ref' or 'value'");
                }
            }
            else if (input is JsonValue jv)
            {
                // TryGetString handles JSON numbers/bools (agents pass 100, true)
                newSlot = new JsonObject { ["value"] = TryGetString(jv) ?? "" };
            }
            else
            {
                throw new Exception("paramValue must be a JSON object or a string literal");
            }

            paramsObj[paramKey] = newSlot;

            RemoveEntryIfExists(zip, entryPath);
            WriteStringEntry(zip, entryPath, modelNode.ToJsonString(JsonOpts));
        }

        /// <summary>
        /// Updates an existing model's definition by replacing it entirely.
        /// </summary>
        public static void UpdateModel(string atbxPath, string modelName, string definitionJson)
        {
            // Verify model exists
            using (var checkZip = ZipFile.OpenRead(atbxPath))
            {
                if (checkZip.GetEntry($"{modelName}.tool/tool.model") == null)
                    throw new Exception($"Model '{modelName}' not found in toolbox");
            }

            // Parse definition, ensure name matches
            var def = JsonNode.Parse(definitionJson)
                ?? throw new Exception("Invalid model definition JSON");
            def["name"] = modelName;

            var (toolModel, toolContent, toolContentRc, diagram, diagramXml) =
                GenerateModelFiles(def, modelName);

            WriteAtbxAtomically(atbxPath, zip =>
            {
                var folder = $"{modelName}.tool";

                RemoveEntryIfExists(zip, $"{folder}/tool.model");
                RemoveEntryIfExists(zip, $"{folder}/tool.content");
                RemoveEntryIfExists(zip, $"{folder}/tool.content.rc");
                RemoveEntryIfExists(zip, $"{folder}/tool.model.diagram");
                RemoveEntryIfExists(zip, $"{folder}/tool.model.diagram.xml");

                WriteStringEntry(zip, $"{folder}/tool.model", toolModel);
                WriteStringEntry(zip, $"{folder}/tool.content", toolContent);
                WriteStringEntry(zip, $"{folder}/tool.content.rc", toolContentRc);
                WriteStringEntry(zip, $"{folder}/tool.model.diagram", diagram);
                WriteStringEntry(zip, $"{folder}/tool.model.diagram.xml", diagramXml);
            });
        }

        /// <summary>
        /// Generates all internal .atbx model files from a simplified definition.
        /// Returns (tool.model, tool.content, tool.content.rc, tool.model.diagram, tool.model.diagram.xml)
        /// </summary>
        private static (string, string, string, string, string) GenerateModelFiles(
            JsonNode def, string modelName)
        {
            var description = def["description"]?.GetValue<string>() ?? "";
            var inputs = def["inputs"]?.AsArray() ?? new JsonArray();
            var steps = def["steps"]?.AsArray() ?? new JsonArray();

            int nextId = 1;
            var nameToId = new Dictionary<string, string>();
            var rcMap = new Dictionary<string, string>();
            var variables = new JsonArray();
            var processes = new JsonArray();
            var contentParams = new JsonObject();
            var diagramNodes = new List<(string id, string text, string shape, double x, double y)>();
            var diagramLinks = new List<(string fromId, string toId)>();

            double currentX = 50;
            double currentY = 100;
            const double xSpacing = 250;
            const double ySpacing = 120;
            const double nodeWidth = 120;
            const double nodeHeight = 50;

            // Create variables for each input parameter
            foreach (var input in inputs)
            {
                var name = input?["name"]?.GetValue<string>() ?? $"Input{nextId}";
                // type is OPTIONAL — when omitted, Pro derives the parameter's
                // type from the system_tool slot it wires into. Writing an
                // explicit datatype overrides slot inference and (for Field
                // params) breaks validation. Only echo back what the caller
                // sent. See DescribeModel for the matching read-side change.
                var type = TryGetString(input?["type"]);
                var defaultVal = TryGetString(input?["default"]); // numbers OK (e.g., default: 100)
                var displayName = TryGetString(input?["displayName"]) ?? name;
                var dependencies = input?["dependencies"]?.AsArray();
                var compositeTypes = input?["compositeTypes"]?.AsArray();
                var id = nextId++.ToString();

                nameToId[name] = id;

                var variable = new JsonObject
                {
                    ["id"] = id,
                    ["title"] = $"$rc:model.element{id}",
                    ["altered"] = "true",
                    ["connection_type"] = "Parameter",
                    ["param_name"] = name
                };

                // Slot-derived params — Pro re-derives type+dependency at load
                // time from the system_tool slot the variable wires into.
                // Writing an explicit datatype on the variable overrides that
                // resolution and breaks validation. Two kinds of slot-derived:
                //   1. Field params declared via "dependencies" — Pro reads
                //      "depends" from tool.content and pulls Field type from
                //      the dependent input's table.
                //   2. GPComposite slots (CalculateField.in_table etc.) —
                //      Pro reads the full composite spec from tool.content
                //      and validates incoming values against the slot's type
                //      list at runtime.
                bool hasDeps = dependencies != null && dependencies.Count > 0;
                bool isComposite = string.Equals(type, "GPComposite", StringComparison.OrdinalIgnoreCase);
                bool isSlotDerived = hasDeps || isComposite;

                if (type != null && !isSlotDerived)
                    variable["datatype"] = new JsonObject { ["type"] = type };
                if (defaultVal != null)
                    variable["value"] = defaultVal;

                variables.Add(variable);
                rcMap[$"model.element{id}"] = displayName;

                // tool.content params: write datatype only when we have grounds
                // to set it. Defaulting to "GPString" silently breaks params
                // that wire into typed slots (e.g., GPTableView, GPFeatureLayer)
                // — the validator rejects "Test_TieLines" as "not a Table View"
                // instead of resolving the layer reference. Omitting datatype
                // lets Pro infer from the system_tool slot (safest default).
                //   - explicit type → write as given (composite expanded with
                //     subtypes when "compositeTypes" is supplied)
                //   - dependencies declared (no type) → assume Field param
                //   - neither → omit, let Pro resolve from slot
                var contentParam = new JsonObject
                {
                    ["displayname"] = $"$rc:{name.ToLowerInvariant()}.title"
                };
                if (type != null)
                    contentParam["datatype"] = BuildDataTypeJson(type, compositeTypes);
                else if (hasDeps)
                    contentParam["datatype"] = new JsonObject { ["type"] = "Field" };
                if (defaultVal != null)
                    contentParam["value"] = defaultVal;

                // Optional "depends" array — declares which other input params
                // a Field-typed parameter resolves against. Pro infers this
                // automatically when the variable wires into a system_tool's
                // dependent slot (e.g., CalculateField.field implicitly depends
                // on in_table), but emitting it explicitly when the caller asks
                // produces output that round-trips identically through
                // describe_model → update_model.
                if (dependencies != null && dependencies.Count > 0)
                {
                    var depsArr = new JsonArray();
                    foreach (var dep in dependencies)
                    {
                        var depName = TryGetString(dep);
                        if (!string.IsNullOrEmpty(depName))
                            depsArr.Add(depName);
                    }
                    if (depsArr.Count > 0)
                        contentParam["depends"] = depsArr;
                }
                contentParams[name] = contentParam;
                rcMap[$"{name.ToLowerInvariant()}.title"] = displayName;

                // Diagram node (ellipse for variables)
                diagramNodes.Add((id, displayName, "Ellipse", currentX, currentY));
                currentX += xSpacing;
            }

            // ---- Pass 1 over steps: validate kinds, canonicalize output slots,
            // and reserve ids for every process + output variable. Doing this
            // BEFORE resolving any refs lets a step reference an output declared
            // by a LATER step (agents reorder steps freely; stored order is not
            // required to be dependency order). Id assignment order (process id,
            // then that step's outputs in parameter order) matches the previous
            // single-pass behavior exactly, so valid models generate identical
            // files.
            var stepPlans = new List<StepPlan>();
            foreach (var step in steps)
            {
                var pStepName = TryGetString(step?["name"]) ?? $"Step{nextId}";
                var pTool = TryGetString(step?["tool"]) ?? "unknown";
                var pKind = TryGetString(step?["kind"]) ?? "gpTool";

                // The writer can faithfully represent only these three kinds.
                // Iterators (and unknown kinds) used to fall through to the
                // system_tool branch, silently corrupting the model — update_model
                // would "succeed" and Pro would fail to load the result.
                bool kindOk =
                    pKind.Equals("gpTool", StringComparison.OrdinalIgnoreCase) ||
                    pKind.Equals("scriptTool", StringComparison.OrdinalIgnoreCase) ||
                    pKind.Equals("nestedModel", StringComparison.OrdinalIgnoreCase);
                if (!kindOk)
                    throw new Exception(
                        $"Step '{pStepName}' has kind '{pKind}', which create_model/update_model cannot " +
                        "faithfully write (the step would be silently corrupted into a bogus system_tool). " +
                        "Models containing iterators must be edited with the surgical tools " +
                        "(set_step_parameter / set_parameter_default) or in Pro's ModelBuilder.");

                var pParameters = step?["parameters"]?.AsObject();

                // Canonicalize the output-slot key for this tool. When the
                // user supplies an output under a non-canonical key (e.g.,
                // `out_features` on management.CalculateGeometryAttributes
                // whose real slot is `updated_features`), Pro's load-time
                // normalizer rewrites the slot and stamps its default UI
                // label ("Updated Features") as the variable name, orphaning
                // every downstream `ref` to the user-supplied output name.
                // Rewriting to the canonical slot up front means Pro never
                // runs the normalizer and the user-supplied output name
                // stays put. Only acts when the tool is in OutputSlots AND
                // the canonical key isn't already occupied (if both exist
                // the user has a conflicting payload — leave it for Pro to
                // flag rather than silently merging).
                var canonOutResolved = GpToolCatalog.ResolveOutputSlot(pTool);
                if (pParameters != null && canonOutResolved is { } canonOut)
                {
                    string? wrongKey = null;
                    foreach (var p in pParameters)
                    {
                        if (p.Value is JsonObject po && po["output"] != null
                            && !string.Equals(p.Key, canonOut.Slot, StringComparison.OrdinalIgnoreCase))
                        {
                            wrongKey = p.Key;
                            break;
                        }
                    }
                    if (wrongKey != null && !pParameters.ContainsKey(canonOut.Slot))
                    {
                        var node = pParameters[wrongKey]?.DeepClone();
                        pParameters.Remove(wrongKey);
                        if (node != null) pParameters[canonOut.Slot] = node;
                    }
                }

                var plan = new StepPlan
                {
                    Step = step,
                    Name = pStepName,
                    Tool = pTool,
                    Kind = pKind,
                    Parameters = pParameters,
                    Environments = step?["environments"]?.AsObject(),
                    ProcessId = nextId++.ToString()
                };
                rcMap[$"model.element{plan.ProcessId}"] = pStepName;

                // Reserve output-variable ids and create the variables now so
                // later (or earlier) steps can ref them by name in pass 2.
                if (pParameters != null)
                {
                    foreach (var param in pParameters)
                    {
                        if (param.Value is not JsonObject paramObj || paramObj["output"] == null)
                            continue;

                        var outputName = TryGetString(paramObj["output"]) ?? $"Output{nextId}";
                        var outputType = TryGetString(paramObj["type"]) ?? "DEFeatureClass";
                        // GPComposite is Pro's multi-type slot wrapper —
                        // appropriate on INPUT params but on a derived OUTPUT
                        // variable it hard-crashes Pro on .atbx open. Coerce to
                        // the tool's canonical concrete DE* type when known.
                        if (string.Equals(outputType, "GPComposite", StringComparison.OrdinalIgnoreCase)
                            && GpToolCatalog.ResolveOutputSlot(pTool) is { } outCoerce)
                        {
                            outputType = outCoerce.Type;
                        }
                        var outputValue = TryGetString(paramObj["value"]);
                        // "parameter": true marks an OUTPUT PARAMETER (ModelBuilder's
                        // 'P' badge on a derived output) — restore param_name +
                        // connection_type so the model's public interface survives
                        // a describe→update round-trip.
                        bool isOutputParam = false;
                        if (paramObj["parameter"] is JsonValue pv2)
                        {
                            if (pv2.TryGetValue<bool>(out var b2)) isOutputParam = b2;
                            else isOutputParam = string.Equals(TryGetString(pv2), "true", StringComparison.OrdinalIgnoreCase);
                        }

                        var outputId = nextId++.ToString();
                        nameToId[outputName] = outputId;

                        var outputVar = new JsonObject
                        {
                            ["id"] = outputId,
                            ["title"] = $"$rc:model.element{outputId}",
                            ["datatype"] = new JsonObject { ["type"] = outputType }
                        };
                        if (outputValue != null)
                            outputVar["value"] = outputValue;
                        else
                            outputVar["derived"] = "true";
                        if (isOutputParam)
                        {
                            outputVar["param_name"] = outputName;
                            outputVar["connection_type"] = "Parameter";
                            var outContent = new JsonObject
                            {
                                ["displayname"] = $"$rc:{outputName.ToLowerInvariant()}.title",
                                ["direction"] = "out",
                                ["type"] = "derived",
                                ["datatype"] = new JsonObject { ["type"] = outputType }
                            };
                            contentParams[outputName] = outContent;
                            rcMap[$"{outputName.ToLowerInvariant()}.title"] = outputName;
                        }
                        variables.Add(outputVar);
                        rcMap[$"model.element{outputId}"] = outputName;

                        plan.Outputs.Add((param.Key, outputId, outputName));
                    }
                }

                stepPlans.Add(plan);
            }

            // ---- Pass 2: build process objects with all refs resolvable ----
            currentX = 50;
            currentY += ySpacing;
            int stepRow = 0;

            foreach (var plan in stepPlans)
            {
                var stepName = plan.Name;
                var tool = plan.Tool;
                var kind = plan.Kind;
                var parameters = plan.Parameters;
                var environments = plan.Environments;
                var step = plan.Step;
                var processId = plan.ProcessId;

                var processParams = new JsonObject();

                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        var paramVal = param.Value;
                        if (paramVal == null)
                        {
                            processParams[param.Key] = "";
                            continue;
                        }

                        if (paramVal is JsonObject paramObj)
                        {
                            // Multi-input reference: {"ref": ["A", "B", ...]} —
                            // written back as a JSON element_id ARRAY, the native
                            // ATBX shape for Merge/Union/Append-style slots.
                            if (paramObj["ref"] is JsonArray refArr)
                            {
                                var idArr = new JsonArray();
                                foreach (var rn in refArr)
                                {
                                    var rName = TryGetString(rn)
                                        ?? throw new Exception($"Step '{stepName}': ref array entries must be strings");
                                    if (!nameToId.TryGetValue(rName, out var rId))
                                        throw new Exception($"Reference '{rName}' not found. Available: {string.Join(", ", nameToId.Keys)}");
                                    idArr.Add(rId);
                                    diagramLinks.Add((rId, processId));
                                }
                                processParams[param.Key] = new JsonObject { ["element_id"] = idArr };
                                continue;
                            }

                            // Reference to another variable
                            var refName = TryGetString(paramObj["ref"]);
                            if (refName != null)
                            {
                                if (!nameToId.TryGetValue(refName, out var refId))
                                    throw new Exception($"Reference '{refName}' not found. Available: {string.Join(", ", nameToId.Keys)}");

                                processParams[param.Key] = new JsonObject
                                {
                                    ["element_id"] = refId
                                };
                                diagramLinks.Add((refId, processId));
                                continue;
                            }

                            // Output declaration — variable already created and id
                            // reserved in pass 1; just wire the slot.
                            var outputName = TryGetString(paramObj["output"]);
                            if (outputName != null)
                            {
                                var planned = plan.Outputs.FirstOrDefault(o => o.SlotKey == param.Key);
                                if (planned.OutputId == null)
                                    throw new Exception($"Internal error: output '{outputName}' on step '{stepName}' was not planned in pass 1");

                                processParams[param.Key] = new JsonObject
                                {
                                    ["direction"] = "out",
                                    ["element_id"] = planned.OutputId
                                };
                                diagramLinks.Add((processId, planned.OutputId));
                                continue;
                            }

                            // Value object
                            var value = TryGetString(paramObj["value"]); // numbers/bools coerced
                            if (value != null)
                            {
                                processParams[param.Key] = new JsonObject { ["value"] = value };
                                continue;
                            }

                            // Pass through as-is
                            processParams[param.Key] = paramVal.DeepClone();
                        }
                        else
                        {
                            // Literal scalar value — agents pass numbers/bools too
                            var strVal = TryGetString(paramVal) ?? "";
                            processParams[param.Key] = new JsonObject { ["value"] = strVal };
                        }
                    }
                }

                // Process header shape varies by step kind:
                //   gpTool      → {system_tool: "alias.toolName"}
                //   scriptTool  → {tool_type: "ScriptTool", path: "Name"}
                //   nestedModel → {tool_type: "ModelTool",  path: "Name"}
                // Path values are names relative to the host toolbox, no
                // extension. WalkModel mirrors the inverse mapping.
                var process = new JsonObject
                {
                    ["id"] = processId,
                    ["title"] = $"$rc:model.element{processId}",
                    ["params"] = processParams
                };
                if (string.Equals(kind, "scriptTool", StringComparison.OrdinalIgnoreCase))
                {
                    process["tool_type"] = "ScriptTool";
                    process["path"] = tool;
                }
                else if (string.Equals(kind, "nestedModel", StringComparison.OrdinalIgnoreCase))
                {
                    process["tool_type"] = "ModelTool";
                    process["path"] = tool;
                }
                else
                {
                    process["system_tool"] = tool;
                }

                // Handle environments
                if (environments != null)
                {
                    var envObj = new JsonObject();
                    foreach (var env in environments)
                    {
                        if (env.Value is JsonObject envValObj)
                        {
                            var refName = envValObj["ref"]?.GetValue<string>();
                            if (refName != null && nameToId.TryGetValue(refName, out var refId))
                                envObj[env.Key] = new JsonObject { ["element_id"] = refId };
                        }
                        else if (env.Value != null)
                        {
                            envObj[env.Key] = new JsonObject { ["value"] = TryGetString(env.Value) ?? "" };
                        }
                    }
                    if (envObj.Count > 0)
                        process["environments"] = envObj;
                }

                // Preconditions: variable names → element ids. Round-trips the
                // ordering-only dependencies that describe_model surfaces as
                // "preconditions"; without this they'd be silently stripped.
                if (step?["preconditions"] is JsonArray preNames && preNames.Count > 0)
                {
                    var preIds = new JsonArray();
                    foreach (var pn in preNames)
                    {
                        var pName = TryGetString(pn);
                        if (string.IsNullOrEmpty(pName)) continue;
                        if (!nameToId.TryGetValue(pName!, out var pId))
                            throw new Exception($"Precondition '{pName}' on step '{stepName}' not found. Available: {string.Join(", ", nameToId.Keys)}");
                        preIds.Add(pId);
                    }
                    if (preIds.Count > 0)
                        process["precondition"] = preIds;
                }

                processes.Add(process);

                // Diagram: process node (RoundRect)
                double px = currentX + (stepRow % 3) * xSpacing;
                double py = currentY + (stepRow / 3) * ySpacing * 2;
                diagramNodes.Add((processId, stepName, "RoundRect", px, py));

                // Output variable nodes (one per declared output)
                int outIdx = 0;
                foreach (var (_, outId, outName) in plan.Outputs)
                {
                    diagramNodes.Add((outId, outName, "Ellipse",
                        px + xSpacing * 0.6, py + outIdx * ySpacing * 0.5));
                    outIdx++;
                }

                stepRow++;
            }

            // Build tool.model
            var toolModel = new JsonObject
            {
                ["version"] = "1.0",
                ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                ["variables"] = variables,
                ["processes"] = processes
            };

            // Build tool.content
            var toolContent = new JsonObject
            {
                ["type"] = "ModelTool",
                ["displayname"] = "$rc:title",
                ["description"] = "$rc:description",
                ["app_ver"] = "13.4",
                ["product"] = "100",
                ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            };
            if (contentParams.Count > 0)
                toolContent["params"] = contentParams;

            // Build tool.content.rc
            rcMap["title"] = modelName;
            rcMap["description"] = description;
            var toolContentRc = new JsonObject
            {
                ["map"] = JsonNode.Parse(JsonSerializer.Serialize(rcMap))!
            };

            // Build tool.model.diagram
            var diagramMeta = new JsonObject
            {
                ["version"] = "1.0",
                ["scale"] = "100",
                ["cx"] = "400",
                ["cy"] = "300",
                ["x"] = "400",
                ["y"] = "300",
                ["dx"] = ((int)(currentX + xSpacing * 4)).ToString(),
                ["dy"] = ((int)(currentY + ySpacing * 4)).ToString()
            };

            // Build tool.model.diagram.xml
            var diagramXml = GenerateDiagramXml(diagramNodes, diagramLinks);

            return (
                toolModel.ToJsonString(JsonOpts),
                toolContent.ToJsonString(JsonOpts),
                toolContentRc.ToJsonString(JsonOpts),
                diagramMeta.ToJsonString(JsonOpts),
                diagramXml
            );
        }

        /// <summary>
        /// Pass-1 plan for one step in GenerateModelFiles: ids reserved, output
        /// variables created, parameters canonicalized — pass 2 resolves refs.
        /// </summary>
        private sealed class StepPlan
        {
            public JsonNode? Step { get; init; }
            public string Name { get; init; } = "";
            public string Tool { get; init; } = "";
            public string Kind { get; init; } = "";
            public JsonObject? Parameters { get; init; }
            public JsonObject? Environments { get; init; }
            public string ProcessId { get; init; } = "";
            public List<(string SlotKey, string OutputId, string OutputName)> Outputs { get; } = new();
        }

        #endregion

        #region Diagram Generation

        private static string GenerateDiagramXml(
            List<(string id, string text, string shape, double x, double y)> nodes,
            List<(string fromId, string toId)> links)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Diagram Version=\"17\">");
            sb.AppendLine("<Nodes>");

            foreach (var (id, text, shape, x, y) in nodes)
            {
                var nodeType = shape == "Ellipse" ? "0" : "1"; // 0=variable, 1=process
                sb.AppendLine($"<Node Class=\"std:ShapeNode\" Version=\"1\" Id=\"{id}\">");
                sb.AppendLine($"<Bounds>{x}, {y}, 120, 50</Bounds>");
                sb.AppendLine("<ZIndex>1</ZIndex>");
                sb.AppendLine("<LayerIndex>-1</LayerIndex>");
                sb.AppendLine("<Locked>False</Locked>");
                sb.AppendLine("<Visible>True</Visible>");
                sb.AppendLine("<Weight>1</Weight>");
                sb.AppendLine("<IgnoreLayout>False</IgnoreLayout>");
                sb.AppendLine($"<Tag Type=\"1\">{EscapeXml(text)}</Tag>");
                sb.AppendLine($"<Id Type=\"1\">{EscapeXml(text)}</Id>");
                sb.AppendLine($"<Text>{EscapeXml(text)}</Text>");
                sb.AppendLine("<TextColor>#FF000000</TextColor>");
                sb.AppendLine("<FontFamily>Segoe UI</FontFamily>");
                sb.AppendLine("<FontSize>11</FontSize>");
                sb.AppendLine("<FontStyle>Normal</FontStyle>");
                sb.AppendLine("<FontWeight>Normal</FontWeight>");
                sb.AppendLine("<TextAlignment>2</TextAlignment>");
                sb.AppendLine("<TextVerticalAlignment>1</TextVerticalAlignment>");
                sb.AppendLine("<Obstacle>True</Obstacle>");
                sb.AppendLine("<AllowIncomingLinks>True</AllowIncomingLinks>");
                sb.AppendLine("<AllowOutgoingLinks>True</AllowOutgoingLinks>");
                sb.AppendLine("<EnabledHandles>511</EnabledHandles>");
                sb.AppendLine("<Expanded>True</Expanded>");
                sb.AppendLine("<Expandable>False</Expandable>");
                sb.AppendLine("<HandlesStyle>9</HandlesStyle>");
                sb.AppendLine($"<Shape Id=\"{shape}\" />");
                sb.AppendLine($"<NodeType>{nodeType}</NodeType>");
                sb.AppendLine("<Status>1</Status>");
                sb.AppendLine("<ErrorFlag>False</ErrorFlag>");
                sb.AppendLine("<IsValid>True</IsValid>");
                sb.AppendLine("<HasError>False</HasError>");
                sb.AppendLine("</Node>");
            }

            sb.AppendLine("</Nodes>");
            sb.AppendLine("<Links>");

            int linkId = 10000;
            foreach (var (fromId, toId) in links)
            {
                sb.AppendLine($"<Link Class=\"std:DiagramLink\" Version=\"1\" Id=\"{linkId++}\">");
                sb.AppendLine("<ZIndex>0</ZIndex>");
                sb.AppendLine("<LayerIndex>-1</LayerIndex>");
                sb.AppendLine($"<Origin Id=\"{fromId}\" />");
                sb.AppendLine($"<Destination Id=\"{toId}\" />");
                sb.AppendLine("<BaseShape>Arrow</BaseShape>");
                sb.AppendLine("<HeadShape>Arrow</HeadShape>");
                sb.AppendLine("</Link>");
            }

            sb.AppendLine("</Links>");
            sb.AppendLine("</Diagram>");
            return sb.ToString();
        }

        private static string EscapeXml(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;")
                       .Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        #endregion

        #region ZIP Helpers

        private static T? ReadJsonEntry<T>(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) return default;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<T>(json);
        }

        private static void WriteJsonEntry(ZipArchive zip, string entryName, JsonNode node)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(node.ToJsonString(JsonOpts));
        }

        private static void WriteStringEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        private static void RemoveEntryIfExists(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName);
            entry?.Delete();
        }

        /// <summary>
        /// Reads the text content of a ZIP entry, or returns <paramref name="defaultValue"/>
        /// if the entry is missing or empty. Splitting this out (instead of reading inline
        /// at the call site) makes the read-before-write ordering obvious in CreateModel.
        /// </summary>
        private static string ReadEntryTextOrDefault(ZipArchive zip, string entryName, string defaultValue)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) return defaultValue;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? defaultValue : text;
        }

        /// <summary>
        /// Performs all .atbx mutations against an in-memory copy of the file,
        /// then atomically swaps the result over the live file. Avoids holding
        /// the live file lock during the heavy ZipArchive Update operations,
        /// which deadlocks against Pro when the file (or a model it contains)
        /// is referenced by an open ModelBuilder canvas tab — including
        /// indirect references via scriptTool / nestedModel steps in other
        /// canvas-open models. Verified 2026-06-08 by Desktop's diagnostic:
        /// closing all MB canvas tabs makes the deadlock disappear entirely.
        /// </summary>
        // Serializes all .atbx writes process-wide. The pipe server handles
        // connections concurrently, so two write ops could otherwise interleave
        // their read-mutate-replace cycles on the same file and lose one of the
        // mutations (or trip File.Replace on the other's temp file).
        private static readonly object _atbxWriteLock = new();

        private static void WriteAtbxAtomically(string atbxPath, Action<ZipArchive> mutate)
        {
            lock (_atbxWriteLock)
            {
                WriteAtbxAtomicallyCore(atbxPath, mutate);
            }
        }

        private static void WriteAtbxAtomicallyCore(string atbxPath, Action<ZipArchive> mutate)
        {
            var originalBytes = File.ReadAllBytes(atbxPath);

            using var ms = new MemoryStream();
            ms.Write(originalBytes, 0, originalBytes.Length);
            ms.Position = 0;

            using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
            {
                mutate(zip);
            }

            var newBytes = ms.ToArray();

            // Stage to a sibling temp file in the same directory so File.Replace
            // can succeed (it requires source + destination on the same volume).
            var dir = Path.GetDirectoryName(atbxPath) ?? ".";
            var tempPath   = Path.Combine(dir, Path.GetFileName(atbxPath) + ".tmp." + Guid.NewGuid().ToString("N"));
            var backupPath = Path.Combine(dir, Path.GetFileName(atbxPath) + ".bak." + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(tempPath, newBytes);

                try
                {
                    // Atomic swap. Works even when Pro has the .atbx open for
                    // read because Replace uses MoveFileEx semantics that don't
                    // require exclusive access on the destination.
                    File.Replace(tempPath, atbxPath, backupPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new Exception(
                        $"Could not replace '{Path.GetFileName(atbxPath)}' — most likely Pro " +
                        $"has it (or a model referencing it) open in a ModelBuilder canvas. " +
                        $"Close all ModelBuilder canvas tabs and retry. ({ex.GetType().Name}: {ex.Message})",
                        ex);
                }

                // File.Replace leaves the backup on disk; clean it up.
                try { File.Delete(backupPath); } catch { /* best-effort */ }
            }
            finally
            {
                try { if (File.Exists(tempPath))   File.Delete(tempPath);   } catch { }
                try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
            }
        }

        /// <summary>
        /// Pure function: given a manifest JSON string, returns an updated manifest
        /// JsonNode with <paramref name="toolName"/> added to the &lt;root&gt; toolset's
        /// tools list (idempotent — no-op if already present).
        /// </summary>
        private static JsonNode AddToolToManifestJson(string manifestJson, string toolName)
        {
            var manifest = JsonNode.Parse(manifestJson)!;
            var tools = manifest["toolsets"]?["<root>"]?["tools"]?.AsArray();
            if (tools == null)
            {
                manifest["toolsets"] = new JsonObject
                {
                    ["<root>"] = new JsonObject { ["tools"] = new JsonArray { toolName } }
                };
                return manifest;
            }

            foreach (var t in tools)
            {
                if (TryGetString(t) == toolName)
                    return manifest; // already listed
            }
            tools.Add(toolName);
            return manifest;
        }

        #endregion
    }

    /// <summary>
    /// A single variable in the model graph — either an exposed Parameter (model
    /// input/output) or an intermediate (process output consumed downstream).
    /// </summary>
    internal class ModelVariable
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Type { get; init; }
        public string? StoredValue { get; init; }
        public bool IsParameter { get; init; }
        public bool IsDerived { get; init; }
    }

    /// <summary>
    /// A single parameter slot on a process. Exactly one of the variable-id /
    /// literal fields is meaningful per instance.
    /// </summary>
    internal class ModelParam
    {
        public string? RefVariableId { get; init; }     // input: {element_id: X} (first id when multiple)
        // ATBX stores element_id as a JSON ARRAY when multiple inputs feed one
        // slot (Merge.inputs, Union.in_features, Append.inputs, ...). All ids,
        // in stored order; null/absent for single-ref slots. RefVariableId is
        // always RefVariableIds[0] when this is set.
        public List<string>? RefVariableIds { get; init; }
        public string? OutputVariableId { get; init; }  // output: {direction: out, element_id: X}
        public string? LiteralValue { get; init; }      // {value: "X"} or bare string
        public JsonNode? RawValue { get; init; }        // complex pass-through (e.g., environments dict)

        /// <summary>All referenced variable ids: the multi-ref list when present, else the single ref.</summary>
        public IEnumerable<string> AllRefIds =>
            RefVariableIds ?? (RefVariableId != null
                ? new List<string> { RefVariableId }
                : (IEnumerable<string>)Array.Empty<string>());
    }

    /// <summary>
    /// What kind of tool a process invokes. The bridge can step-execute
    /// <see cref="GpTool"/> directly; the other kinds need Pro's ribbon to run.
    /// <see cref="Iterator"/> is the legacy <c>model_tool</c> path that
    /// pre-dated <c>tool_type</c>; both ModelBuilder iterators and (older)
    /// nested-model references land there.
    /// </summary>
    internal enum ToolKind
    {
        GpTool,
        ScriptTool,
        NestedModel,
        Iterator,
        Unknown
    }

    /// <summary>
    /// A single process (tool invocation) in the model. Params is ordered by JSON
    /// insertion — which empirically matches the GP tool's declared slot order.
    /// </summary>
    internal class ModelProcess
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Tool { get; init; } = "";
        public ToolKind Kind { get; init; } = ToolKind.GpTool;
        public bool IsIterator { get; init; }
        public Dictionary<string, ModelParam> Params { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Variable ids this process is precondition-ordered after (no data link).</summary>
        public List<string> PreconditionVariableIds { get; init; } = new();
        /// <summary>Per-step GP environment overrides, keyed by env name; null when none.</summary>
        public Dictionary<string, ModelParam>? Environments { get; init; }
    }

    /// <summary>
    /// Topo-sorted walk of a ModelBuilder model: variables keyed by id and
    /// processes in dependency order.
    /// </summary>
    internal class ModelGraph
    {
        public Dictionary<string, ModelVariable> Variables { get; init; } = new();
        public List<ModelProcess> Processes { get; init; } = new();
    }
}
