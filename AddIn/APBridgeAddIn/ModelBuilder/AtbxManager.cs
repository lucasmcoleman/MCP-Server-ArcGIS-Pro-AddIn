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

            // Build inputs list (Parameter variables)
            var inputs = new JsonArray();
            foreach (var v in variables)
            {
                var id = TryGetString(v?["id"]);
                if (id == null) continue;
                if (TryGetString(v?["connection_type"]) != "Parameter") continue;

                var input = new JsonObject
                {
                    ["name"] = varNames[id]
                };

                // Only emit "type" when datatype is explicitly declared on the
                // variable. Pro OMITS datatype on Parameter variables whose type
                // is slot-derived (e.g., a Field parameter wired into
                // CalculateField.field — Pro derives type+dependency from the
                // system_tool's slot definition at load time). Previously we
                // fell back to "GPString" when datatype was absent, which
                // misrepresented slot-derived params as plain strings to agents.
                // Agents then echoed "GPString" back via update_model, baking
                // an explicit type that overrode Pro's slot inference and broke
                // validation (ERROR 000860: "Zone field is not the type of Field").
                var typeStr = TryGetString(v?["datatype"]?["type"]);
                if (typeStr != null)
                    input["type"] = typeStr;

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
                var tool = TryGetString(p?["system_tool"])
                    ?? TryGetString(p?["model_tool"])
                    ?? "unknown";

                var step = new JsonObject
                {
                    ["name"] = ResolveName(title, $"Step_{processId}"),
                    ["tool"] = tool
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

                            if (direction == "out" && elementId != null)
                            {
                                var outputName = varNames.GetValueOrDefault(elementId, $"output_{elementId}");
                                var outputType = varMap.ContainsKey(elementId)
                                    ? TryGetString(varMap[elementId]["datatype"]?["type"]) ?? "DEFeatureClass"
                                    : "DEFeatureClass";
                                parameters[param.Key] = new JsonObject
                                {
                                    ["output"] = outputName,
                                    ["type"] = outputType
                                };
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

        #endregion

        #region Create Operations

        /// <summary>
        /// Creates a new empty .atbx toolbox file.
        /// </summary>
        public static void CreateToolbox(string path, string displayName)
        {
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

            // Write to the .atbx ZIP
            using var fileStream = new FileStream(atbxPath, FileMode.Open, FileAccess.ReadWrite);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update);

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

            using var fileStream = new FileStream(atbxPath, FileMode.Open, FileAccess.ReadWrite);
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Update);

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
                var defaultVal = input?["default"]?.GetValue<string>();
                var displayName = input?["displayName"]?.GetValue<string>() ?? name;
                var dependencies = input?["dependencies"]?.AsArray();
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
                // Datatype on the model-graph variable: only when explicit AND
                // no dependencies declared. Pro omits datatype on graph
                // variables for slot-derived params (Field, etc.) so the
                // system_tool's slot definition resolves type+dependency at
                // load time. Writing an explicit datatype overrides that
                // resolution and breaks validation.
                bool hasDeps = dependencies != null && dependencies.Count > 0;
                if (type != null && !hasDeps)
                    variable["datatype"] = new JsonObject { ["type"] = type };
                if (defaultVal != null)
                    variable["value"] = defaultVal;

                variables.Add(variable);
                rcMap[$"model.element{id}"] = displayName;

                // tool.content params: write datatype only when we have
                // grounds to set it. Defaulting to "GPString" silently breaks
                // params that wire into typed slots (e.g., GPTableView,
                // GPFeatureLayer) — the validator rejects "Test_TieLines" as
                // "not a Table View" instead of resolving the layer reference.
                // Omitting datatype lets Pro infer from the system_tool slot
                // the variable wires into (the safest default).
                //   - explicit type → write as given
                //   - dependencies declared (no type) → assume Field param
                //   - neither → omit, let Pro resolve from slot
                var contentParam = new JsonObject
                {
                    ["displayname"] = $"$rc:{name.ToLowerInvariant()}.title"
                };
                if (type != null)
                    contentParam["datatype"] = new JsonObject { ["type"] = type };
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

            // Process each step
            currentX = 50;
            currentY += ySpacing;
            int stepRow = 0;

            foreach (var step in steps)
            {
                var stepName = step?["name"]?.GetValue<string>() ?? $"Step{nextId}";
                var tool = step?["tool"]?.GetValue<string>() ?? "unknown";
                var parameters = step?["parameters"]?.AsObject();
                var environments = step?["environments"]?.AsObject();

                var processId = nextId++.ToString();
                rcMap[$"model.element{processId}"] = stepName;

                var processParams = new JsonObject();
                string? outputVarId = null;

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
                            // Reference to another variable
                            var refName = paramObj["ref"]?.GetValue<string>();
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

                            // Output declaration
                            var outputName = paramObj["output"]?.GetValue<string>();
                            if (outputName != null)
                            {
                                var outputType = paramObj["type"]?.GetValue<string>() ?? "DEFeatureClass";
                                var outputId = nextId++.ToString();
                                nameToId[outputName] = outputId;

                                // Create the output variable
                                var outputVar = new JsonObject
                                {
                                    ["id"] = outputId,
                                    ["title"] = $"$rc:model.element{outputId}",
                                    ["derived"] = "true",
                                    ["datatype"] = new JsonObject { ["type"] = outputType }
                                };
                                variables.Add(outputVar);
                                rcMap[$"model.element{outputId}"] = outputName;

                                processParams[param.Key] = new JsonObject
                                {
                                    ["direction"] = "out",
                                    ["element_id"] = outputId
                                };

                                outputVarId = outputId;
                                diagramLinks.Add((processId, outputId));
                                continue;
                            }

                            // Value object
                            var value = paramObj["value"]?.GetValue<string>();
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
                            // Literal string value
                            var strVal = paramVal.GetValue<string>();
                            processParams[param.Key] = new JsonObject { ["value"] = strVal };
                        }
                    }
                }

                var process = new JsonObject
                {
                    ["id"] = processId,
                    ["title"] = $"$rc:model.element{processId}",
                    ["system_tool"] = tool,
                    ["params"] = processParams
                };

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
                            envObj[env.Key] = new JsonObject { ["value"] = env.Value.GetValue<string>() };
                        }
                    }
                    if (envObj.Count > 0)
                        process["environments"] = envObj;
                }

                processes.Add(process);

                // Diagram: process node (RoundRect)
                double px = currentX + (stepRow % 3) * xSpacing;
                double py = currentY + (stepRow / 3) * ySpacing * 2;
                diagramNodes.Add((processId, stepName, "RoundRect", px, py));

                // Output variable node if created
                if (outputVarId != null)
                {
                    diagramNodes.Add((outputVarId, nameToId.First(kv => kv.Value == outputVarId).Key,
                        "Ellipse", px + xSpacing * 0.6, py));
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
}
