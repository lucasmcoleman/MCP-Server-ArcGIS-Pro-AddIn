// Out-of-process round-trip tests for the AtbxManager model engine and the
// SystemToolboxCatalog. Runs without ArcGIS Pro — the ModelBuilder file layer
// is pure System.* (ZIP + JSON). Exit code 0 = all pass.
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using APBridgeAddIn.ModelBuilder;

int passed = 0, failed = 0;
var failures = new List<string>();

void Check(bool condition, string name, string? detail = null)
{
    if (condition) { passed++; Console.WriteLine($"  PASS  {name}"); }
    else { failed++; failures.Add(name); Console.WriteLine($"  FAIL  {name}{(detail != null ? " — " + detail : "")}"); }
}

T? GetProp<T>(JsonNode? node, string path) where T : JsonNode
{
    var current = node;
    foreach (var part in path.Split('.'))
        current = current?[part];
    return current as T;
}

var workDir = Path.Combine(Path.GetTempPath(), "atbx-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(workDir);
var tbx = Path.Combine(workDir, "RoundTrip.atbx");

try
{
    Console.WriteLine("== create_toolbox guard ==");
    AtbxManager.CreateToolbox(tbx, "RoundTrip");
    Check(File.Exists(tbx), "toolbox created");
    try
    {
        AtbxManager.CreateToolbox(tbx, "RoundTrip");
        Check(false, "create_toolbox refuses overwrite", "no exception thrown");
    }
    catch (Exception ex)
    {
        Check(ex.Message.Contains("already exists"), "create_toolbox refuses overwrite");
    }
    AtbxManager.CreateToolbox(tbx, "RoundTrip", overwrite: true);
    Check(true, "create_toolbox overwrite=true succeeds");

    Console.WriteLine("== create_model: multi-ref, forward ref, preconditions, env, output param ==");
    // Step order is deliberately NOT dependency order: the Buffer step (first)
    // references MergedOut, produced by the Merge step (second) — exercises the
    // two-pass writer. The Merge step takes TWO inputs (multi-ref array).
    // The Buffer step carries a precondition, a per-step extent environment,
    // a numeric literal, and a GPComposite output marked as a model parameter.
    var definition = """
    {
      "name": "RoundTrip",
      "description": "round trip test model",
      "inputs": [
        { "name": "InA", "type": "GPFeatureLayer" },
        { "name": "InB", "type": "GPFeatureLayer" },
        { "name": "BufDist", "type": "GPLinearUnit", "default": "100 Meters" }
      ],
      "steps": [
        {
          "name": "Buffer Step",
          "tool": "analysis.Buffer",
          "parameters": {
            "in_features": { "ref": "MergedOut" },
            "out_feature_class": { "output": "FinalOut", "type": "GPComposite", "parameter": true },
            "buffer_distance_or_field": { "ref": "BufDist" },
            "dissolve_option": "ALL",
            "method": 100
          },
          "environments": { "extent": "0 0 10 10" },
          "preconditions": [ "MergedOut" ]
        },
        {
          "name": "Merge Step",
          "tool": "management.Merge",
          "parameters": {
            "inputs": { "ref": ["InA", "InB"] },
            "output": { "output": "MergedOut", "type": "DEFeatureClass" }
          }
        }
      ]
    }
    """;
    AtbxManager.CreateModel(tbx, definition);
    Check(true, "create_model with forward ref + multi-ref + output param");

    var described = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "RoundTrip"))!;

    // Inputs must NOT contain the output parameter FinalOut
    var inputNames = described["inputs"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToList();
    Check(inputNames.SequenceEqual(new[] { "InA", "InB", "BufDist" }),
        "inputs exclude output parameter", string.Join(",", inputNames));

    var steps = described["steps"]!.AsArray();
    var bufferStep = steps.FirstOrDefault(s => s!["name"]!.GetValue<string>() == "Buffer Step");
    var mergeStep = steps.FirstOrDefault(s => s!["name"]!.GetValue<string>() == "Merge Step");
    Check(bufferStep != null && mergeStep != null, "both steps present after round-trip");

    // Multi-ref array preserved
    var mergeInputs = GetProp<JsonArray>(mergeStep, "parameters.inputs.ref");
    Check(mergeInputs != null && mergeInputs.Count == 2 &&
          mergeInputs[0]!.GetValue<string>() == "InA" && mergeInputs[1]!.GetValue<string>() == "InB",
        "multi-input ref array round-trips", mergeStep?["parameters"]?["inputs"]?.ToJsonString());

    // Output parameter flag preserved; GPComposite coerced to a concrete type
    var outDecl = GetProp<JsonObject>(bufferStep, "parameters.out_feature_class");
    Check(outDecl?["parameter"]?.GetValue<bool>() == true, "output 'parameter: true' round-trips",
        outDecl?.ToJsonString());
    Check(outDecl?["type"]?.GetValue<string>() == "DEFeatureClass",
        "GPComposite output coerced to DEFeatureClass", outDecl?["type"]?.ToJsonString());

    // Precondition round-trips as the variable name
    var preArr = GetProp<JsonArray>(bufferStep, "preconditions");
    Check(preArr != null && preArr.Count == 1 && preArr[0]!.GetValue<string>() == "MergedOut",
        "preconditions round-trip", bufferStep?["preconditions"]?.ToJsonString());

    // Step environment round-trips
    var envVal = GetProp<JsonObject>(bufferStep, "environments")?["extent"];
    Check(envVal != null && envVal.GetValue<string>() == "0 0 10 10",
        "per-step environment round-trips", bufferStep?["environments"]?.ToJsonString());

    // Numeric literal coerced (method: 100 → "100")
    var methodVal = bufferStep?["parameters"]?["method"];
    Check(methodVal != null && methodVal.GetValue<string>() == "100",
        "numeric JSON literal coerced to string", methodVal?.ToJsonString());

    Console.WriteLine("== update_model idempotence (describe → update → describe) ==");
    AtbxManager.UpdateModel(tbx, "RoundTrip", described.ToJsonString());
    var redescribed = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "RoundTrip"))!;
    Check(JsonNode.DeepEquals(described, redescribed),
        "describe→update→describe is stable", "structures differ");
    // Membership + order re-asserted AFTER update (not just after create) —
    // the interface-mutation bug lived exactly in this gap.
    var reNames = redescribed["inputs"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToList();
    Check(reNames.SequenceEqual(new[] { "InA", "InB", "BufDist" }),
        "post-update inputs keep membership and order", string.Join(",", reNames));

    Console.WriteLine("== interface fidelity: optional / exposed / parameterOrder ==");
    // Local helper: rewrite one ZIP entry through a text transform. Reads the
    // old text BEFORE deleting/writing (ZipArchive Update-mode rule).
    void DoctorEntry(string atbx, string entryName, Func<string, string> transform)
    {
        using var zip = ZipFile.Open(atbx, ZipArchiveMode.Update);
        var entry = zip.GetEntry(entryName) ?? throw new Exception($"missing entry {entryName}");
        string text;
        using (var sr = new StreamReader(entry.Open())) text = sr.ReadToEnd();
        entry.Delete();
        using var sw = new StreamWriter(zip.CreateEntry(entryName).Open());
        sw.Write(transform(text));
    }

    string ReadEntryJsonText(string atbx, string entryName)
    {
        using var zip = ZipFile.OpenRead(atbx);
        using var sr = new StreamReader(zip.GetEntry(entryName)!.Open());
        return sr.ReadToEnd();
    }

    // A model with an optional input and a derived-output parameter.
    var optDef = """
    {
      "name": "OptModel", "description": "interface fidelity test",
      "inputs": [
        { "name": "MainIn", "type": "GPFeatureLayer" },
        { "name": "Filter", "type": "GPSQLExpression", "optional": true, "default": "1=1" }
      ],
      "steps": [
        {
          "name": "Buf",
          "tool": "analysis.Buffer",
          "parameters": {
            "in_features": { "ref": "MainIn" },
            "out_feature_class": { "output": "BufOut", "type": "DEFeatureClass", "parameter": true },
            "buffer_distance_or_field": "100 Meters"
          }
        }
      ]
    }
    """;
    AtbxManager.CreateModel(tbx, optDef);

    var optContent = JsonNode.Parse(ReadEntryJsonText(tbx, "OptModel.tool/tool.content"))!;
    Check(optContent["params"]?["Filter"]?["type"]?.GetValue<string>() == "optional",
        "optional input writes tool.content type:optional",
        optContent["params"]?["Filter"]?.ToJsonString());
    Check(optContent["params"]?["MainIn"]?["type"] == null,
        "required input carries no tool.content type flag");

    var optDesc = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "OptModel"))!;
    var filterIn = optDesc["inputs"]!.AsArray().First(i => i!["name"]!.GetValue<string>() == "Filter");
    Check(filterIn!["optional"]?.GetValue<bool>() == true, "describe surfaces optional:true",
        filterIn.ToJsonString());
    var mainIn = optDesc["inputs"]!.AsArray().First(i => i!["name"]!.GetValue<string>() == "MainIn");
    Check(mainIn!["optional"] == null, "required input has no optional flag in describe");
    var optOrder = optDesc["parameterOrder"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    Check(optOrder.SequenceEqual(new[] { "MainIn", "Filter", "BufOut" }),
        "parameterOrder covers full interface incl. derived output", string.Join(",", optOrder));

    AtbxManager.UpdateModel(tbx, "OptModel", optDesc.ToJsonString());
    var optRedesc = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "OptModel"))!;
    Check(JsonNode.DeepEquals(optDesc, optRedesc), "optional-flag model round-trip stable");

    // Simulate a Pro-authored file: (a) tool.content param order differs from
    // tool.model variable order (dialog order is authoritative); (b) a stray
    // auto-named variable ("28") carries connection_type Parameter in
    // tool.model but has NO tool.content entry — Pro does not expose it.
    DoctorEntry(tbx, "OptModel.tool/tool.content", text =>
    {
        var c = JsonNode.Parse(text)!.AsObject();
        var ps = c["params"]!.AsObject();
        var rebuilt = new JsonObject();
        foreach (var k in new[] { "Filter", "BufOut", "MainIn" })
        {
            var n = ps[k]!;
            ps.Remove(k);
            rebuilt[k] = n;
        }
        c["params"] = rebuilt;
        return c.ToJsonString();
    });
    DoctorEntry(tbx, "OptModel.tool/tool.model", text =>
    {
        var m = JsonNode.Parse(text)!.AsObject();
        m["variables"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "99",
            ["param_name"] = "28",
            ["connection_type"] = "Parameter",
            ["datatype"] = new JsonObject { ["type"] = "GPDouble" },
            ["value"] = "0.5"
        });
        return m.ToJsonString();
    });

    var proDesc = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "OptModel"))!;
    var proInputs = proDesc["inputs"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToList();
    Check(proInputs.SequenceEqual(new[] { "Filter", "MainIn", "28" }),
        "describe orders inputs by tool.content; strays last", string.Join(",", proInputs));
    var strayIn = proDesc["inputs"]!.AsArray().First(i => i!["name"]!.GetValue<string>() == "28");
    Check(strayIn!["exposed"]?.GetValue<bool>() == false,
        "stray Parameter variable marked exposed:false", strayIn.ToJsonString());
    var proOrder = proDesc["parameterOrder"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    Check(proOrder.SequenceEqual(new[] { "Filter", "BufOut", "MainIn" }),
        "parameterOrder mirrors doctored tool.content order", string.Join(",", proOrder));

    // The Pro-authored interface must survive update_model verbatim.
    AtbxManager.UpdateModel(tbx, "OptModel", proDesc.ToJsonString());
    var afterContent = JsonNode.Parse(ReadEntryJsonText(tbx, "OptModel.tool/tool.content"))!;
    var afterKeys = afterContent["params"]!.AsObject().Select(kv => kv.Key).ToList();
    Check(afterKeys.SequenceEqual(new[] { "Filter", "BufOut", "MainIn" }),
        "update_model preserves tool.content param order", string.Join(",", afterKeys));
    Check(!afterKeys.Contains("28"), "stray variable NOT promoted into public interface");
    Check(afterContent["params"]!["Filter"]!["type"]?.GetValue<string>() == "optional",
        "optionality preserved through Pro-authored round-trip");
    var afterModel = JsonNode.Parse(ReadEntryJsonText(tbx, "OptModel.tool/tool.model"))!;
    var strayVar = afterModel["variables"]!.AsArray()
        .FirstOrDefault(v => v?["param_name"]?.GetValue<string>() == "28");
    Check(strayVar != null &&
          strayVar["connection_type"]!.GetValue<string>() == "Parameter" &&
          strayVar["value"]!.GetValue<string>() == "0.5",
        "stray variable preserved in tool.model (flag + default)", strayVar?.ToJsonString());

    var proRedesc = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "OptModel"))!;
    Check(JsonNode.DeepEquals(proDesc, proRedesc), "Pro-authored-shape round-trip idempotent");

    // Executor view: GetToolSignature reads tool.content, so the stray must be
    // invisible there too, in preserved order.
    var optSig = AtbxManager.GetToolSignature(tbx, "OptModel");
    Check(optSig != null && optSig.Select(s => s.Name).SequenceEqual(new[] { "Filter", "BufOut", "MainIn" }),
        "GetToolSignature reflects preserved order, no stray",
        optSig == null ? "null" : string.Join(",", optSig.Select(s => s.Name)));

    Console.WriteLine("== pythonScriptTool classification (5th process shape) ==");
    // Pro stores .pyt-hosted script tools as tool_type "PythonScriptTool" with
    // a relative path into the .pyt — previously classified unknown/unknown,
    // making describe output unwritable for these models.
    var pytDef = """
    {
      "name": "PytModel", "description": "pyt step round-trip",
      "inputs": [ { "name": "SrcIn", "type": "GPFeatureLayer" } ],
      "steps": [
        {
          "name": "Rank and Aggregate",
          "tool": "..\\MitigationScriptTools.pyt\\SiteRankAggregate",
          "kind": "pythonScriptTool",
          "parameters": {
            "Scored_Parcels": { "ref": "SrcIn" },
            "Candidate_Sites": { "output": "CandOut", "type": "DEFeatureClass" }
          }
        }
      ]
    }
    """;
    AtbxManager.CreateModel(tbx, pytDef);
    var pytDesc = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "PytModel"))!;
    var pytStep = pytDesc["steps"]!.AsArray()[0]!;
    Check(pytStep["kind"]!.GetValue<string>() == "pythonScriptTool",
        ".pyt step kind survives round-trip (not unknown)", pytStep["kind"]!.ToJsonString());
    Check(pytStep["tool"]!.GetValue<string>() == "..\\MitigationScriptTools.pyt\\SiteRankAggregate",
        ".pyt relative path survives round-trip", pytStep["tool"]!.ToJsonString());
    var pytModelJson = JsonNode.Parse(ReadEntryJsonText(tbx, "PytModel.tool/tool.model"))!;
    var pytProc = pytModelJson["processes"]!.AsArray()[0]!;
    Check(pytProc["tool_type"]!.GetValue<string>() == "PythonScriptTool",
        "writer stores tool_type PythonScriptTool verbatim", pytProc["tool_type"]!.ToJsonString());
    AtbxManager.UpdateModel(tbx, "PytModel", pytDesc.ToJsonString());
    var pytRedesc = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "PytModel"))!;
    Check(JsonNode.DeepEquals(pytDesc, pytRedesc), ".pyt-step model round-trip idempotent");
    var pytGraph = AtbxManager.WalkModel(tbx, "PytModel");
    Check(pytGraph.Processes.Count == 1 && pytGraph.Processes[0].Kind == ToolKind.PythonScriptTool,
        "WalkModel classifies PythonScriptTool",
        pytGraph.Processes.Count == 1 ? pytGraph.Processes[0].Kind.ToString() : "count!=1");

    Console.WriteLine("== WalkModel: topo order, multi-ref edges, env parse ==");
    var graph = AtbxManager.WalkModel(tbx, "RoundTrip");
    Check(graph.Processes.Count == 2, "two processes walked");
    Check(graph.Processes[0].Name == "Merge Step" && graph.Processes[1].Name == "Buffer Step",
        "topo sort places Merge before Buffer (data edge + precondition)",
        string.Join(" -> ", graph.Processes.Select(p => p.Name)));
    var mergeProc = graph.Processes[0];
    var inputsParam = mergeProc.Params["INPUTS"]; // case-insensitive lookup
    Check(inputsParam.RefVariableIds is { Count: 2 }, "WalkModel captures both multi-ref ids");
    var bufferProc = graph.Processes[1];
    Check(bufferProc.Environments != null && bufferProc.Environments.ContainsKey("extent"),
        "WalkModel parses per-step environments");
    Check(bufferProc.PreconditionVariableIds.Count == 1, "WalkModel parses preconditions");

    Console.WriteLine("== iterator guard ==");
    var iterDef = """
    {
      "name": "IterModel", "description": "x", "inputs": [],
      "steps": [ { "name": "It", "tool": "something", "kind": "iterator", "parameters": {} } ]
    }
    """;
    try
    {
        AtbxManager.CreateModel(tbx, iterDef);
        Check(false, "writer rejects iterator kind", "no exception");
    }
    catch (Exception ex)
    {
        Check(ex.Message.Contains("iterator", StringComparison.OrdinalIgnoreCase),
            "writer rejects iterator kind");
    }

    Console.WriteLine("== surgical writes ==");
    AtbxManager.SetParameterDefault(tbx, "RoundTrip", "BufDist", "250 Meters");
    var afterDefault = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "RoundTrip"))!;
    var bufDist = afterDefault["inputs"]!.AsArray().First(i => i!["name"]!.GetValue<string>() == "BufDist");
    Check(bufDist!["default"]!.GetValue<string>() == "250 Meters", "set_parameter_default applies");

    AtbxManager.SetStepParameter(tbx, "RoundTrip", "Buffer Step", "dissolve_option", "\"NONE\"");
    var afterStep = JsonNode.Parse(AtbxManager.DescribeModel(tbx, "RoundTrip"))!;
    var dissolve = afterStep["steps"]!.AsArray()
        .First(s => s!["name"]!.GetValue<string>() == "Buffer Step")!["parameters"]!["dissolve_option"];
    Check(dissolve!.GetValue<string>() == "NONE", "set_step_parameter applies");

    Console.WriteLine("== SystemToolboxCatalog (live Pro install) ==");
    var bufferSig = SystemToolboxCatalog.GetSignature("analysis.Buffer");
    var expectedBuffer = new[] { "in_features", "out_feature_class", "buffer_distance_or_field",
        "line_side", "line_end_type", "dissolve_option", "dissolve_field", "method" };
    Check(bufferSig != null && bufferSig.SequenceEqual(expectedBuffer, StringComparer.OrdinalIgnoreCase),
        "Buffer signature from system toolboxes matches arcpy order",
        bufferSig == null ? "null" : string.Join(",", bufferSig));

    var mergeSig = SystemToolboxCatalog.GetSignature("management.Merge");
    Check(mergeSig != null && mergeSig[0].Equals("inputs", StringComparison.OrdinalIgnoreCase),
        "Merge signature resolved dynamically (not hand-pinned)",
        mergeSig == null ? "null" : string.Join(",", mergeSig));

    var addFieldSchema = SystemToolboxCatalog.GetSchema("management.AddField");
    var fieldTypeParam = addFieldSchema?.Params.FirstOrDefault(p => p.Name == "field_type");
    Check(fieldTypeParam?.DomainValues != null && fieldTypeParam.DomainValues.Contains("TEXT"),
        "AddField field_type coded domain extracted",
        fieldTypeParam?.DomainValues == null ? "null" : string.Join(",", fieldTypeParam.DomainValues));

    var projectSig = GpToolCatalog.ResolveSignature("management.Project");
    Check(projectSig != null && projectSig[3] == "transform_method",
        "hand-pinned signatures still win via ResolveSignature");

    var search = SystemToolboxCatalog.SearchTools("PairwiseBuffer", 5);
    Check(search.Count >= 1 && search[0].ToolId.Contains("PairwiseBuffer"),
        "SearchTools finds PairwiseBuffer");

    var outSlot = SystemToolboxCatalog.GetOutputSlot("management.Merge");
    Check(outSlot is { } os && os.Slot == "output", "Merge output slot derived dynamically",
        outSlot?.Slot);

    Console.WriteLine("== nested-execution helpers: ResolveToolReference + GetToolSignature ==");
    // Bare name → same toolbox.
    var (sameBox, sameTool) = AtbxManager.ResolveToolReference(tbx, "RoundTrip");
    Check(sameBox == tbx && sameTool == "RoundTrip", "bare name resolves to same toolbox");

    // Pro-style relative ref: base is the .atbx treated as a directory
    // (first '..' exits the atbx, second exits its containing folder).
    // tbx = {workDir}\RoundTrip.atbx → ..\.. → parent of workDir.
    var rel = @"..\..\" + Path.GetFileName(workDir) + @"\Other.tbx\NBPop";
    var (relBox, relTool) = AtbxManager.ResolveToolReference(tbx, rel);
    Check(relTool == "NBPop" &&
          string.Equals(relBox, Path.Combine(workDir, "Other.tbx"), StringComparison.OrdinalIgnoreCase),
        "relative ref resolves against atbx-as-directory", relBox);

    // Absolute ref passes through.
    var abs = Path.Combine(workDir, "Abs.atbx", "T1");
    var (absBox, absTool) = AtbxManager.ResolveToolReference(tbx, abs);
    Check(absTool == "T1" &&
          string.Equals(absBox, Path.Combine(workDir, "Abs.atbx"), StringComparison.OrdinalIgnoreCase),
        "absolute ref passes through", absBox);

    // GetToolSignature reads a model tool's declared param order from its
    // tool.content — the same path the executor uses for script tools and
    // nested models. RoundTrip's params: InA, InB, BufDist, FinalOut(out).
    var toolSig = AtbxManager.GetToolSignature(tbx, "RoundTrip");
    Check(toolSig != null && toolSig.Count == 4 &&
          toolSig[0].Name == "InA" && toolSig[1].Name == "InB" &&
          toolSig[2].Name == "BufDist" && toolSig[3].Name == "FinalOut",
        "GetToolSignature returns declared param order",
        toolSig == null ? "null" : string.Join(",", toolSig.Select(s => s.Name)));
    Check(toolSig != null && toolSig[3].IsOutput && !toolSig[0].IsOutput,
        "GetToolSignature flags output direction");

    // Unknown tool / non-ZIP toolbox → null (caller dense-packs).
    Check(AtbxManager.GetToolSignature(tbx, "NoSuchTool") == null,
        "GetToolSignature null for missing tool");
    var fakeTbx = Path.Combine(workDir, "legacy.tbx");
    File.WriteAllBytes(fakeTbx, new byte[] { 1, 2, 3 }); // binary, not a ZIP
    Check(AtbxManager.GetToolSignature(fakeTbx, "Any") == null,
        "GetToolSignature null for binary .tbx");
}
finally
{
    try { Directory.Delete(workDir, true); } catch { }
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
if (failed > 0)
{
    Console.WriteLine("Failures: " + string.Join("; ", failures));
    Environment.Exit(1);
}
