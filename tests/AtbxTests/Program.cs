// Out-of-process round-trip tests for the AtbxManager model engine and the
// SystemToolboxCatalog. Runs without ArcGIS Pro — the ModelBuilder file layer
// is pure System.* (ZIP + JSON). Exit code 0 = all pass.
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
