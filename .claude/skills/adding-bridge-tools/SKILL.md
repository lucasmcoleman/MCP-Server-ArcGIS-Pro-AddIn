---
name: adding-bridge-tools
description: Use when a capability doesn't exist yet — an MCP tool call has no bridge op behind it, a needed Pro SDK action (map/layer/layout/GP) has no "pro.X" case in ProBridgeService.HandleAsync, or you're about to work around a missing feature with execute_python/run_gp_tool instead of adding it properly. Also use when a new tool needs GpToolCatalog.Signatures/OutputSlots entries, or when deciding whether a capability gap is a one-off probe vs. a recurring tool worth adding.
---

# Adding Bridge Tools

## Overview
Capability gaps are the costliest failure class in this repo's history — "minor missing features" the owner assumed were included cost hours of workaround each, while adding the tool properly costs about one. The core rule: when you hit a missing capability that will recur, stop working around it with `execute_python`/`run_gp_tool` improvisation and add it as a real op.

## When to use
- An MCP tool call fails or doesn't exist for something Pro's SDK can do (layer, map, layout, GP, editing).
- You find yourself calling `execute_python` or `bridge_op` more than once for the same capability.
- A new tool needs to write GP tool parameters and hits a slot-mismatch (values land in the wrong arcpy argument).
- You're about to add a field to `IpcRequest`/`IpcResponse` or a new serialized return type.

## When NOT to use
- One-off, never-to-repeat probe of Pro state — use `mcp__arcgis__bridge_op` directly and stop; don't build a tool around it.
- The op already exists Pro-side (case exists in `HandleAsync`) but has no MCP wrapper — that's a 10-minute wrapper add per the decision gate below, not the full recipe.
- The capability requires something off the UI thread that ArcGIS Pro's SDK itself can't do off `QueuedTask.Run` — no amount of bridge plumbing fixes an SDK limitation.
- Pure output-formatting/response-shape changes with no new Pro-side action — that's a `ProTools.cs`-only edit, no dispatcher case needed.
- Anything that touches the in-proc Python GP lane inside the first ~3 minutes of a Pro session — it will wedge permanently regardless of how well the tool is written (see `python_gp_warmup_wedge.md` memory / CLAUDE.md "Things that bite").

## Decision gate (do this before writing any code)
1. **Does the op already exist Pro-side?** `grep -n "case \"pro\." AddIn/APBridgeAddIn/ProBridgeService.cs` — if your capability is already a case in `HandleAsync`, you only need step 3 below (an MCP wrapper), not the full recipe.
2. **Is this a one-off?** If you'll never need it again, call `mcp__arcgis__bridge_op` with a raw op/arg pair (or `Invoke-BridgeOp.ps1` from a shell) and move on. Do not build a tool for a single use.
3. **Recurring capability?** Do the full 4-step recipe below.

## Quick reference

| Step | File | What you add |
|---|---|---|
| 1 | `AddIn/APBridgeAddIn/ProBridgeService.cs` — `HandleAsync` switch (~line 170-1160) | `case "pro.foo": return await HandleFoo(req.Args);` |
| 2 | `AddIn/APBridgeAddIn/ProBridgeService.cs` | `HandleFoo(...)` method returning `IpcResponse`, SDK calls wrapped in `QueuedTask.Run` |
| 3 | `McpServer/ArcGisMcpServer/Tools/ProTools.cs` | `[McpServerTool, Description(...)]` method: build args dict → `_client!.OpAsync("pro.foo", args)` → `FormatResult(r, "pro.foo")` |
| 4 | both build scripts | rebuild Add-In (MSBuild) + MCP server (`build-mcp-server.ps1`), then `restart-dev-cycle.ps1` for full redeploy |

If the op is GP-execution-related and you see a slot-mismatch (a value lands in the wrong arcpy parameter), also add `GpToolCatalog.Signatures` (and `OutputSlots` if it has a derived output) in the same change — see "GP catalog" below.

## The 4-step recipe

### 1. Dispatcher case
`ProBridgeService.HandleAsync` (`AddIn/APBridgeAddIn/ProBridgeService.cs:167`) is a switch on `req.Op`. Real example, `pro.clearSelection` (line 633):
```csharp
case "pro.clearSelection":
    return await HandleClearSelection(req.Args);
```
Simple ops are sometimes inlined directly in the case block instead of a separate method (see `pro.countFeatures` at line 195) — either is fine; prefer a separate `HandleX` method once the body exceeds a few lines.

### 2. Handler method
Real example, `HandleClearSelection` (`ProBridgeService.cs:1458`):
```csharp
private static async Task<IpcResponse> HandleClearSelection(Dictionary<string, string>? args)
{
    string? layerName = null;
    args?.TryGetValue("layer", out layerName);

    var result = await QueuedTask.Run<(bool ok, string? error, int cleared, string? layerCleared)>(() =>
    {
        // ... all Pro SDK / map / layer access happens inside this delegate ...
        return (true, null, clearedCount, null);
    });

    if (!result.ok) return new(false, result.error, null);
    return new(true, null, new { cleared = result.cleared, layer = result.layerCleared });
}
```
Any SDK call touching the map, a layer, or the view MUST be inside `QueuedTask.Run` — Pro's SDK is not thread-safe off the UI/CIM thread.

### 3. MCP tool wrapper
Real example, `ClearSelection` (`McpServer/ArcGisMcpServer/Tools/ProTools.cs:192`):
```csharp
[McpServerTool, Description(
    "Clear feature selections in the active map. ...")]
public static async Task<string> ClearSelection(
    [Description("Optional: name of a specific layer...")] string? layer = null,
    [Description("Optional: name of the map to operate on. Default: active map.")] string? map = null)
{
    var args = new Dictionary<string, string>();
    if (!string.IsNullOrWhiteSpace(layer)) args["layer"] = layer;
    if (!string.IsNullOrWhiteSpace(map)) args["map"] = map;
    var r = await _client!.OpAsync("pro.clearSelection", args);
    return FormatResult(r, "pro.clearSelection");
}
```
Write the `Description` thoroughly — the costliest failure class on record is an agent not knowing a capability exists because the tool description undersold it.

### 4. Rebuild and deploy
Add-In: MSBuild only (not `dotnet build`) — see `releasing-and-deploying` skill for the full command. MCP server: `pwsh ./build-mcp-server.ps1` (refuses if `ArcGisMcpServer.exe` is running — exit your MCP client first). For a change touching both halves, run `pwsh ./restart-dev-cycle.ps1` (Pro must be closed).

## Hard rules

- **`IpcRequest`/`IpcResponse` are hand-duplicated** on both sides: `AddIn/APBridgeAddIn/IpcModels.cs` vs `McpServer/ArcGisMcpServer/Ipc/IpcModels.cs`. Add a field to one and forget the other and it silently vanishes at runtime (no compile error — they're separate record definitions in separate projects).
- **Tools return JSON strings only, never typed objects.** Any new type you serialize must be added to the `[JsonSerializable(...)]` list on `McpJsonContext` or `IndentedJsonContext` (`McpServer/ArcGisMcpServer/Ipc/IpcModels.cs:52-65`) — the published exe is trimmed, so a missing registration throws at runtime with **no compile-time warning**.
- **`JsonSerializerOptions` must derive from `new(JsonSerializerOptions.Default)`.** A bare `new JsonSerializerOptions { WriteIndented = true }` throws `"TypeInfoResolver not specified"` serializing `JsonNode`/`JsonValueCustomized<T>` — see `BridgeRegistry.cs:23` or `AtbxManager.cs:23` for the correct pattern.
- **Tool parameters are primitives or JSON-encoded strings only** — trim-safe schema reflection for `[McpServerTool]` depends on it.
- **`Geoprocessing.MakeEnvironmentArray` takes named args, never a `Dictionary`.** Passing a `Dictionary<string,object>` positionally binds to the first parameter (`workspace`) and throws a `RuntimeBinderException` mentioning `MapMember` — this was a real bug (commit `8c1d58b`). Use `MakeEnvironmentArray(overwriteoutput: true, workspace: gdb)`.
- **`GeoprocessingProjectItem` lives in `ArcGIS.Desktop.GeoProcessing`** (capital P), not `ArcGIS.Desktop.Core.Geoprocessing`.
- **Any new `.atbx` write path must route through `AtbxManager.WriteAtbxAtomically`** (`AtbxManager.cs:1958`). A direct `FileStream` + `ZipArchive(Update)` on a live `.atbx` deadlocks if Pro holds that model (or one referencing it) open in a ModelBuilder canvas.

## GP catalog (only if your tool executes/writes GP steps)
If a `run_model`/`run_gp_tool` step surfaces a slot-mismatch (e.g. a boolean lands in `transform_method` instead of `preserve_shape`), add an entry to `GpToolCatalog.Signatures` (`AddIn/APBridgeAddIn/ModelBuilder/GpToolCatalog.cs:51`) — ordered slot names for `"alias.toolName"`. If the tool also has a derived output, add the matching `GpToolCatalog.OutputSlots` entry (line 158) in the **same change** so the `.atbx` writer can canonicalize non-canonical output keys and coerce `GPComposite` declarations to the concrete `DE*` type Pro requires. Example pair:
```csharp
["management.Project"] = new[] { "in_dataset", "out_dataset", "out_coor_system",
    "transform_method", "in_coor_system", "preserve_shape", "max_deviation", "vertical" },
...
["management.Project"] = ("out_dataset", "DEFeatureClass"),
```

## Test loop (no MCP client needed)
- `./tools/Invoke-BridgeOp.ps1 -Op pro.foo -Args @{ layer = 'Roads' }` — talks straight to the named pipe, works even while a Claude session holds the published exe lock. Try this FIRST for any new op before wiring the MCP wrapper.
- `./tools/Test-BridgeLive.ps1` after any Add-In deploy — the live smoke battery (~44 checks; exact count varies with conditionals) over the named pipe; Pro must be open, past the ~3-min Python warm-up window.
- `dotnet run --project tests/AtbxTests -c Release` after any `AtbxManager`/`GpToolCatalog`/`SystemToolboxCatalog` change. Despite the file's own comment ("Runs without ArcGIS Pro"), the `SystemToolboxCatalog` section (`tests/AtbxTests/Program.cs:377`, labeled "live Pro install") reads real system toolboxes off disk — Pro must actually be installed for those checks to pass, not just absent from the process list.
- `./tools/Test-McpStdio.ps1 -Tool ping -ServerPath <path>` — its default `-ServerPath` is `McpServer\ArcGisMcpServer\bin\Release\net8.0\ArcGisMcpServer.dll`, NOT the `publish/ArcGisMcpServer.exe` that `.mcp.json` actually points at. Always pass `-ServerPath` explicitly or you're testing a stale/different build than the one Claude Code loads.

## Build/deploy pointers
- Add-In needs VS MSBuild, not `dotnet build` (the Pro SDK targets file uses `CodeTaskFactory`). Full command and deploy path in the `releasing-and-deploying` skill.
- Pro must be closed to copy the `.esriAddinX` bundle over the deployed one.
- `build-mcp-server.ps1` refuses to publish while any `ArcGisMcpServer.exe` is running — exit the MCP client first.
- See `releasing-and-deploying` for cutting an actual release once the tool works locally.

## Common mistakes
- Adding a field to one side's `IpcModels.cs` and not the other — the value silently disappears in the response instead of erroring, because both sides compile fine independently. Always grep both paths when touching the wire shape.
- Forgetting to register a new return type in `McpJsonContext`/`IndentedJsonContext` — works fine under `dotnet build`/`dotnet run` (reflection fallback), then throws at runtime in the trimmed, self-contained published exe. The trim-safety gap only shows up after `build-mcp-server.ps1`, not during dev iteration.
- Calling `MakeEnvironmentArray` with a positional `Dictionary` — binds to `workspace` and throws a confusing `RuntimeBinderException` about `MapMember` that gives no hint the real problem is named-vs-positional args (real historical bug, commit `8c1d58b`).
- Writing a new `.atbx` mutation with a raw `ZipArchive(Update)` instead of `WriteAtbxAtomically` — works in isolated testing, then hangs for the full 4-minute IPC timeout the first time a user has the model open in a ModelBuilder canvas.
- Treating a recurring capability gap as "just use execute_python this once" repeatedly instead of adding the op — this is the documented costliest failure class; each deferral compounds into the next agent's hours of troubleshooting.
