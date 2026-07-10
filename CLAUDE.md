# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

An MCP (Model Context Protocol) server that lets MCP clients drive ArcGIS Pro in real time. Two independently-built artifacts compose the system:

- **`McpServer/ArcGisMcpServer/`** — .NET 8 stdio/HTTP MCP server, single-file publish (~21 MB). One `[McpServerTool]`-decorated static method per public tool. Each method translates the tool call into a bridge op via `BridgeClient.OpAsync("pro.X", args)` and `FormatResult`s the response.
- **`AddIn/APBridgeAddIn/`** — ArcGIS Pro 3.6+ Add-In (a `.esriAddinX` ZIP bundle). Hosts a named-pipe server inside the Pro process and dispatches incoming ops to handlers in `ProBridgeService.cs`.

The two halves are coupled only by the IPC contract (line-delimited JSON `IpcRequest`/`IpcResponse` over `ArcGisProBridge_<PID>`) plus the discovery registry at `%LOCALAPPDATA%\ArcGisMcpBridge\<PID>.json`.

## Big-picture architecture

```
MCP client  ⇄  ArcGisMcpServer.exe  ⇄  (named pipe)  ⇄  APBridgeAddIn (in Pro)
```

- **`ProTools.cs`** is the agent-facing surface. Each `[McpServerTool, Description(...)]` method names a tool, declares typed parameters, calls one bridge op, returns a JSON string. Add a new agent-facing tool here.
- **`ProBridgeService.cs`** is the Pro-side dispatcher. `HandleAsync` switches on `req.Op` (e.g., `"pro.runModel"`); each case calls a `HandleX` method that returns an `IpcResponse`. Add a new Pro-side capability here, then add a matching MCP tool in `ProTools.cs`.
- **`AtbxManager.cs`** (~1.6K lines) owns ModelBuilder `.atbx` read/write. `.atbx` files are ZIPs containing per-tool folders (`{toolName}.tool/tool.model` + content + diagram); `WalkModel` parses one into a typed `ModelGraph` (variables + topo-sorted processes), `GenerateModelFiles` writes the JSON files from a simplified definition. Every `.atbx` write — bulk (`CreateModel`, `UpdateModel`) and surgical (`SetParameterDefault`, `SetStepParameter`) — routes through `WriteAtbxAtomically` (see "Things that bite").
- **`GpToolCatalog.cs`** is a shared static the executor AND the writer both consult. `Signatures` maps `"alias.tool"` → ordered slot names (positional). `OutputSlots` maps each known tool to its canonical output slot key + default concrete `DE*` type. The writer uses `OutputSlots` to canonicalize non-canonical user-supplied keys (e.g., `out_features` → `updated_features` on `CalculateGeometryAttributes`) and to coerce `GPComposite` output declarations to the concrete `DE*` type Pro requires.
- **`BridgeDiscovery.cs`** + **`BridgeRegistry.cs`** implement per-PID routing. Each Pro instance writes its registry entry on Add-In load; `BridgeDiscovery.Discover()` runs on every MCP request and cleans up dead PIDs. Selection: if `ARCGIS_PROJECT` is set the pin is **strict** — match (extension/path-tolerant, vs both `projectName` and `projectPath`) or throw `BridgePinException`, surfaced as a structured error; a pinned server never falls back to another instance. Unpinned: most-recently-started live bridge → legacy `ArcGisProBridgePipe` fallback. This is what makes Pro restarts transparent mid-session and lets multiple agents each own a Pro instance (one agent per instance, pinned via `.mcp.json` env). `list_bridges` (server-local tool) shows live instances + current routing; `select_bridge` sets a runtime routing override (same strict semantics) so one unpinned agent can switch between instances — refused when the env pin is set (operator isolation wins). The Add-In registers `projectName` WITH the `.aprx` extension — matching must normalize.

### `run_model` is special

The bridge does NOT delegate ModelBuilder execution to Pro's own engine. `HandleRunModel` → `RunModelCore` parses the `.atbx` into a `ModelGraph`, topologically sorts processes, and calls `Geoprocessing.ExecuteToolAsync` once per step with refs resolved against a runtime variable map. This bypasses Pro's whole-chain pre-validation, which would otherwise reject intermediate inputs that haven't materialized yet (first-run failures).

Key invariants the executor relies on:

- **`GpToolCatalog.Signatures`** static dictionary maps `"toolboxAlias.toolName"` → ordered slot names. Required because Pro stores process params sparsely by name but `ExecuteToolAsync` takes positional value arrays — without this map, dense-packing puts values into the wrong slots (e.g., `false` from `preserve_shape` lands in `transform_method` for `management.Project`). Extend the dictionary in `GpToolCatalog.cs` when a new tool surfaces a slot-mismatch error; if the new tool also has a derived output, add a `GpToolCatalog.OutputSlots` entry in the same change so the writer can canonicalize and coerce its outputs.
- **Output-recording pre-pass** handles in-place selection tools (`SelectLayerByLocation`, `SelectLayerByAttribute`) whose ModelBuilder output is a logical alias for `in_layer` — arcpy has no positional output slot for them, so the signature walk would skip the output param and downstream refs would resolve to empty.
- **`SubstituteModelVars`** replaces `%VarName%` patterns in literal expressions with runtime values. ModelBuilder's own engine does this string substitution before handing expressions to arcpy; the executor must mirror it or `CalculateField` expressions with `%MitigationRatio%` etc. trip Python `SyntaxError`.
- **`SanitizeGdbName`** translates ModelBuilder variable names into valid File Geodatabase FC names when deriving output paths (spaces → `_`, leading-digit prefix → `x_`).
- **Async path:** `HandleRunModelAsync` + `HandleGetRunStatus` use a `ConcurrentDictionary<jobId, RunJob>` with per-job locks. The sync `HandleRunModel` and async path both call `RunModelCore(args, RunJob?)` — pass `null` for sync, a `RunJob` for async progress tracking. Use the async path for models that exceed the MCP client's tool-call ceiling (~4 min for Claude Desktop, longer for Claude Code).

### Diagnostic tip

`mcp-bridge.log` (lives at the active project's home folder) **only records failures and exceptions**. A successful run leaves no log entry. To verify a long run completed, look for the expected outputs in the project's default GDB rather than searching the log for success markers.

## Build & deploy

The repo has two independently-built artifacts and the deploy gotchas matter:

### Add-In (`AddIn/APBridgeAddIn/`)

**Must use MSBuild from Visual Studio — not `dotnet build`.** The Pro SDK targets file (`Esri.ProApp.SDK.Desktop.targets`) uses `CodeTaskFactory`, which is MSBuild-only.

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  AddIn/APBridgeAddIn/APBridgeAddIn.csproj -p:Configuration=Release
```

Output: `AddIn/APBridgeAddIn/bin/Release/net8.0-windows8.0/APBridgeAddIn.esriAddinX` (a ZIP).

Deploy by copying that file to `C:\Users\<you>\Documents\ArcGIS\AddIns\ArcGISPro\{c56ccfd4-f12a-4916-84c2-64248b3d746c}\APBridgeAddIn.esriAddinX`. The GUID is the `AddInInfo id` from `Config.daml` — stable across builds. Pro must be closed during the copy.

`CS8632` warnings are cosmetic — ignore.

### MCP Server (`McpServer/ArcGisMcpServer/`)

```powershell
pwsh ./build-mcp-server.ps1
```

This invokes `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -o McpServer/ArcGisMcpServer/publish`, producing `publish/ArcGisMcpServer.exe`. (Trimming is why ProTools uses `WithTools<T>()` + source-generated JSON contexts — reflection-based serialization paths get trimmed away.) The script **refuses to publish if any `ArcGisMcpServer.exe` is running** — any attached Claude Code session holds the file lock. Exit the MCP client first.

The `.mcp.json` at the repo root points directly at the published exe, so a fresh build is picked up by the next Claude Code start. Plain `dotnet build` (without the publish script) is fine for syntax validation.

### Full close-restart cycle

For changes that affect both halves:

```powershell
pwsh ./restart-dev-cycle.ps1
```

The script verifies Pro is closed, rebuilds the Add-In, wipes the per-user AssemblyCache (Pro caches extracted DLLs there and may not re-extract on identical-mtime input), deploys the bundle, republishes the MCP exe, mirrors it to `publish-http/` for the HTTP variant, and restarts the `ArcGisMcpServer-HTTP` scheduled task if it was running. Steps are ordered so a build failure leaves the prior deployed state intact.

## Doing common things

### Adding an MCP tool that calls a new Pro op

1. Add `case "pro.foo": return await HandleFoo(req.Args);` to the dispatcher in `ProBridgeService.HandleAsync` (around line 700-1000).
2. Implement `HandleFoo(Dictionary<string,string>? args)` in `ProBridgeService.cs` — return `IpcResponse`. Wrap any Pro SDK calls that touch the map or layers in `QueuedTask.Run`.
3. Add a `[McpServerTool, Description(...)] public static async Task<string> Foo(...)` method to `ProTools.cs` that builds an args dict, calls `_client!.OpAsync("pro.foo", args)`, returns `FormatResult(r, "pro.foo")`.
4. Rebuild both halves and run the full close-restart cycle.

### Working on `run_model` step execution

`HandleRunModel` (thin wrapper) and `HandleRunModelAsync` both call `RunModelCore` in `ProBridgeService.cs`. The per-step loop is the heart of the executor — make changes there carefully. Test with the scratch `test_authoring.atbx` pattern: `create_toolbox` → `create_model` with the failing-shape steps → `describe_model` to verify round-trip → `run_model` to verify execution. The scratch toolbox can be deleted afterward.

When a `run_model` failure surfaces, check the bridge log at the active project's home folder for the actual GP error code — Aurora model debugging history is full of cases where the MCP client gave up before the bridge could return the real error.

The executor now runs `scriptTool` and `nestedModel` steps too: nested models hosted in an `.atbx` are RECURSED through the same step-by-step engine (preserving the first-run fix; cycle + depth-8 guards); script tools (and `.tbx`-hosted nested models, which can't be parsed) dispatch by qualified path via `ExecuteToolAsync("<toolbox>\<tool>")`. Their positional signature comes from `AtbxManager.GetToolSignature` (the target's own `tool.content`; declared order = arcpy calling order, `type:"derived"` params EXCLUDED from the call array per arcpy contract — recorded via the in-place pre-pass + refined from `ReturnValue`). Cross-toolbox `path` refs (`..\..\Other\Box.tbx\Tool`) resolve relative to the .atbx treated as a DIRECTORY (`AtbxManager.ResolveToolReference`). Only `iterator`/unknown kinds are rejected. `pythonScriptTool` steps (`.pyt`-hosted, `tool_type:"PythonScriptTool"` — the 5th process shape) execute OUT-OF-PROC: in-proc `ExecuteToolAsync` on a `.pyt` path never returns, so `RunPytToolAsync` spawns a child arcpy process via propy.bat (launched from Pro, so it inherits Pro's clean environment), calls the tool with kwargs by slot name via `ImportToolbox`, and maps `getOutput(i)` back onto the step's out-direction slots in stored order. Cross-process caveats: no selection propagation, no in_memory datasets — inputs must be concrete paths. `pytMode="skip"` on run_model/start_run_model is partial mode: `.pyt` steps and everything downstream skip (cascade tracked via `skippedOutputVarIds`), reported as `skippedPytSteps`; per-step timeout via `pytTimeoutSeconds` (default 3600). Script-tool steps are Python — the post-launch warm-up wedge applies.

### Adding a `.atbx` write path

If you need a new write operation on an `.atbx` (e.g., `add_step`, `remove_step`, `rewire_connection`), wrap the mutation in `AtbxManager.WriteAtbxAtomically(atbxPath, zip => { ... })`. Never open a live `.atbx` directly with `FileStream` + `ZipArchive(Update)` from a new write path — Pro deadlocks on in-place writes to any `.atbx` containing a model it holds in a ModelBuilder canvas (or any model referenced by a canvas-open model via `scriptTool`/`nestedModel` steps). The helper reads the live file into memory, runs the mutation in-memory, then `File.Replace`s a temp file over the live one, so the bridge never holds the live file lock during the heavy write.

### .atbx format notes

ATBX files are plain ZIPs with a UTF-8 JSON layout — not SQLite. Each model lives in `{modelName}.tool/`:
- `tool.model` — the graph (variables + processes + connections)
- `tool.content` — resource declarations (parameters, environments)
- `tool.content.rc` — string resource map (titles)
- `tool.model.diagram` + `tool.model.diagram.xml` — visual layout
- Toolbox-level: `toolbox.content` + `toolbox.content.rc`

`AtbxManager.WalkModel` is the authoritative reader; `GenerateModelFiles` is the authoritative writer. Both round-trip the simplified JSON definition shape (the same shape `describe_model` returns and `create_model`/`update_model` accept).

**Parameter-interface fidelity (2026-07-10):** `tool.content`'s `params` object is Pro's authoritative public interface — its key order is the dialog/arcpy calling order and per-entry `type:"optional"` carries optionality (absent = Required); `tool.model`'s variables array is creation order and routinely disagrees on Pro-authored models. `describe_model` therefore emits `optional: true` per input, orders `inputs` by tool.content, marks tool.model variables that carry the Parameter flag but have NO tool.content entry `"exposed": false` (Pro-authored strays, e.g. auto-named `28` — they must NOT be promoted into the interface), and emits top-level `parameterOrder` (full interface order, derived outputs interleaved). `GenerateModelFiles` honors all four; definitions without the fields keep the old behavior. Agents editing definitions must preserve these fields verbatim — dropping them makes every parameter Required and re-derives the order (the pre-fix bug: ERROR 000735 cascades at run time). Round-trip fidelity assertions live in AtbxTests, including doctored Pro-authored-shape fixtures — extend them when touching either side.

## Testing

- **`dotnet run --project tests/AtbxTests -c Release`** — out-of-process round-trip suite for the ModelBuilder file layer (compiles AtbxManager + catalogs by source inclusion; no Pro needed). Run after any AtbxManager/GpToolCatalog/SystemToolboxCatalog change.
- **`./tools/Test-BridgeLive.ps1`** — 41-check live smoke battery over the named pipe (Pro must be open, AFTER the Python warm-up window). Run after any Add-In deploy.
- **`./tools/Invoke-BridgeOp.ps1 -Op pro.X -Args @{...}`** — one-off bridge op without the MCP server in the loop. This is how to test new ops when a Claude Code session holds the published exe lock.

## Things that bite

- **Python-backed GP wedges if called too early after Pro launch.** A Python-touching GP call (CalculateValue, CalculateField w/ Python, script tools) in the first ~minutes of a Pro session can hang forever AND permanently wedge the GP Python lane for that session (native tools keep working). `pro.executePython` self-gates for 180s of Pro uptime and returns a retry hint; don't remove that gate. If the lane is wedged, only a Pro restart clears it. The 180s applies ONLY to that in-proc Python lane — bridge reads and native-GP `run_model` (including out-of-proc `.pyt` dispatch) answer much earlier, ~60–95s after launch empirically. Don't stall non-Python work for the full 3 minutes.
- **Out-of-project .pyt paths hang ExecuteToolAsync.** `ExecuteToolAsync(@"C:\path\bridge.pyt\Tool")` never returns in-proc (the identical .pyt runs fine via propy.bat). That's why `execute_python` rides `management.CalculateValue` (system-tool resolution) with base64 code in the code_block. Don't resurrect the deployed-toolbox design.
- **Never launch Pro from an agent shell with `Start-Process`.** Pro inherits the shell's environment; a bloated/corrupt PATH breaks conda activation ("input line is too long") and Python init. Launch via `C:\Windows\explorer.exe "<path>.aprx"` so Pro gets Explorer's clean environment.
- **Pro shutdown can hang at "Shutting down..."** (and silently refuses CloseMainWindow while GP calls are pending). Check `MainWindowTitle` before assuming the close worked.

- **Never auto-kill ArcGIS Pro.** The standard rule is to ask the user to close Pro before any redeploy. The build script refuses to overwrite a running MCP exe; the Add-In bundle is the user's responsibility to copy in. Exception: when the user has explicitly granted permission for a specific task (e.g., overnight automation), launching Pro via `& "C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe" "<aprx path>"` may still get stuck on Pro's Start Page or sign-in dialog — those modals require human clicks.
- **`Geoprocessing.MakeEnvironmentArray` is a named-argument method, not a Dictionary-taker.** Passing `Dictionary<string,object>` positionally binds it to the first parameter (`workspace`) and produces a `RuntimeBinderException` about `MapMember`. Use named-arg syntax: `MakeEnvironmentArray(overwriteoutput: true, workspace: gdb)`.
- **`JsonSerializerOptions` for `JsonNode` serialization** must come from `new(JsonSerializerOptions.Default)` (carries `DefaultJsonTypeInfoResolver`). A bare `new JsonSerializerOptions { WriteIndented = true }` will throw "TypeInfoResolver not specified" when serializing `JsonValueCustomized<T>` instances. New `[McpServerTool]` methods returning JSON should look at existing handlers in `ProTools.cs` for the right pattern.
- **`ZipArchive` Update mode**: read pre-existing entries BEFORE doing any writes. Reading after writing can silently return empty streams (this used to corrupt `update_model` until F-series fixes). The pattern persists inside `WriteAtbxAtomically`'s mutation lambda — the helper runs the mutation against an in-memory `ZipArchive` in `Update` mode, so the read-before-write ordering still matters within the lambda body.
- **`.atbx` writes deadlock against Pro's open ModelBuilder canvas.** An in-place write to a file Pro holds in a canvas, OR a file referenced via `scriptTool`/`nestedModel` by a canvas-open model, hangs for 4 minutes (the bridge's IPC timeout). Resolved by `AtbxManager.WriteAtbxAtomically`: all four existing write paths (`CreateModel`, `UpdateModel`, `SetParameterDefault`, `SetStepParameter`) route through it; any new write path must too. Operational caveat for users: after a surgical write, Pro's open canvas keeps showing the pre-write state until the user reopens that model tab — the file on disk is correct but Pro doesn't auto-reload.
- **`GeoprocessingProjectItem`** lives in `ArcGIS.Desktop.GeoProcessing` (capital `P`), not `ArcGIS.Desktop.Core.Geoprocessing`.
- **Git-bash mangles Windows `/F /IM` flags** when running `taskkill`. Use PowerShell `Stop-Process -Force` / `Start-Process` for any process control. The Bash tool is fine for `git`, `grep`, file ops; PowerShell for Windows-native commands.
- **MSBuild's auto-deploy `RegisterAddIn.exe`** errors with "not recognized" when not on PATH. Benign — the `.esriAddinX` bundle is built before that step runs. Just do the file copy manually or via `restart-dev-cycle.ps1`.
