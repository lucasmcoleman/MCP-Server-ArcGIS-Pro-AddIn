# Changelog

All notable changes to this project. Format inspired by [Keep a Changelog](https://keepachangelog.com/), grouped into the rounds in which work landed. Dates are when the work was committed to `master`.

This project is a hardened fork/evolution of [nicogis/MCP-Server-ArcGIS-Pro-AddIn](https://github.com/nicogis/MCP-Server-ArcGIS-Pro-AddIn). The "Pre-rounds" section below covers initial scaffolding and the ModelBuilder integration that preceded the systematic hardening rounds.

---

## [Post-Cycle-B Hardening] — 2026-04-27

This round addressed real-world friction surfaced when an agent built a complex model (`FatalFlawScreening`, ~30 steps, 13 inputs) against the bridge. Several latent bugs in `run_model` parameter handling and ModelBuilder schema generation had been masked because the previous test models were simpler. Also closes a thread-affinity gap on `save_project` and locale/Y-coord issues that escaped earlier rounds.

### Added
- **`pro.getViewDiagnostics` MCP wrapper** — the bridge handler existed since Round 3 Cycle A but lacked an MCP-side wrapper, so agents couldn't actually invoke it. Now exposed as `mcp__arcgis__get_view_diagnostics`. ([5ad7858](../../commit/5ad7858))
- **`update_model` / `create_model` schema: optional `dependencies` field on input parameters** — when an agent declares `{ "name": "FieldName", "dependencies": ["LayerParam"] }`, the writer emits a Pro-native `Field`-typed parameter with `depends: ["LayerParam"]` in `tool.content` and slot-derived datatype on the model graph variable (no explicit `datatype` on the variable itself, matching what Pro writes when you drag-create a Field parameter from a tool slot). Closes the last gap in programmatic ModelBuilder model creation: agents can now express Field parameters that validate against a feature/table input layer, instead of falling back to `GPString` (which Pro then rejects with `ERROR 000860: Zone field is not the type of Field`). Verified by round-trip + execution test against a `PairwiseDissolve` model.

### Fixed
- **`run_model` — defaults didn't flow through; missing params now use arcpy `"#"` sentinel** — Empty string was treated by arcpy as an *explicit* empty value, causing `ERROR 000735: Value is required` for required params and overriding declared defaults for optional ones. Symptom: running a model with most params unspecified produced 10+ `Value is required` errors even when all those params had declared defaults in the `.atbx` (REST URLs, `"500 Feet"`, etc.). The `"#"` sentinel is arcpy's long-standing convention for "use the parameter's declared default." Empty-string-as-explicit-value intent is preserved (if the user passes `{"FieldName": ""}` the dict still contains the key with empty value, and that `""` passes through unchanged). ([da94901](../../commit/da94901))
- **`run_model` — named-parameter dict produced positional shifts when caller order ≠ model declared order** — Agents pass parameters as a JSON object dict, but ModelBuilder binds positionally (arcpy convention). Without an explicit reorder, dict insertion order became the implicit positional order, and any mismatch (especially when the model has parameters the user didn't supply, like `Output_Workspace`) shifted every subsequent value into the wrong slot. Symptom: an arcpy error referencing a parameter NAME the user never typed, with a value that was meant for a different parameter. Now: read the model's declared parameter order via `AtbxManager.DescribeModel` and remap. ([7fa87a5](../../commit/7fa87a5))
- **`describe_model` — read-side: lied about Field parameters as `GPString`** — Pro omits `datatype` on Parameter variables in `tool.model` whose type is slot-derived (Field params, etc.) — Pro re-derives type+dependency from the `system_tool` slot at load time. Our `SimplifyModel` was falling back to `"GPString"` when `datatype` was absent, which misrepresented slot-derived params as plain strings to AI agents. Agents then echoed `"GPString"` back via `update_model`, baking an explicit type that overrode Pro's slot inference and broke validation. Fixed: `type` field is now omitted from describe output when datatype is absent on the variable (signaling slot-derived). Also reads `depends` from `tool.content` and surfaces as `dependencies` array, so round-tripping preserves Field-parameter dependencies.
- **`save_project` — thread-affinity exception** — `Project.Current.SaveAsync()` requires the WPF GUI thread; calling it from `QueuedTask.Run` raised `"The calling thread cannot access this object because a different thread owns it."` Same fix pattern as F1/F2: wrap in `Application.Current.Dispatcher.InvokeAsync(() => Project.Current.SaveAsync())` and unwrap the nested Task. Also surfaced silent save-first failures in `HandleCreateProject`/`HandleOpenProject` that had been swallowing the same root cause. ([28af4bf](../../commit/28af4bf))
- **`add_map_frame_to_layout` — Y-coordinate convention mismatch** — The MCP tool description tells agents that `xInches`/`yInches` are measured from the page top-left (the screen-coords convention universal in web/UI work). Pro SDK layout coords are bottom-up — y=0 is the page bottom, increasing toward the top. Without inversion, an agent passing `y=1.0` expecting "near the top" silently got a frame near the bottom. Now the handler inverts internally: `sdkYmin = pageHeight - y - h; sdkYmax = pageHeight - y;`. ([75e9e9b](../../commit/75e9e9b))
- **Locale-dependent number parsing across `create_layout`, `add_map_frame_to_layout`, `export_layout`** — `double.TryParse(string)` and `int.TryParse(string)` use the *current culture*'s decimal separator. On non-US locales where `,` is the decimal separator, the bare overload silently fails to parse `"11.5"` and falls through to default values without error. Now: `NumberStyles.Float` + `CultureInfo.InvariantCulture` everywhere user numeric strings cross the bridge. ([e408667](../../commit/e408667))

---



### Added
- **`pro.addLayerFromFile`** — load shapefiles, file-geodatabase feature classes (composite paths like `path/to.gdb/FeatureClass`), and rasters from a local path. Closes the URL-only ingestion gap. ([dc02b19](../../commit/dc02b19), MCP wrapper [c085033](../../commit/c085033))
- **`pro.createLayout`** — create a blank layout with configurable size/orientation (default letter-landscape). ([dc02b19](../../commit/dc02b19))
- **`pro.addMapFrameToLayout`** — wire an existing map into a layout via a map-frame element at a given page rectangle. The step that turns `create_layout`'s blank canvas into a renderable layout. ([dc02b19](../../commit/dc02b19))
- **`pro.listMaps`** — enumerate all maps in the project (complements `get_active_map_name` which only returns the active one). ([dc02b19](../../commit/dc02b19))
- **`pro.saveProject`** — explicit `Project.Current.SaveAsync()`. Useful as a pre-op safety rail or for persisting batch edits. ([dc02b19](../../commit/dc02b19))
- **`restart-dev-cycle.ps1`** — one-shot helper that verifies Pro/MCP-server are closed, wipes the per-user AssemblyCache, and rebuilds the MCP server exe. Eliminates manual coordination between Add-In + MCP-server change cycles. ([97364fb](../../commit/97364fb))

> Status as of latest commit: 5 handlers + 5 MCP wrappers committed and deployed; runtime verification deferred to user.

---

## [Round 3 Cycle A] — 2026-04-23

### Added
- **`pro.getViewDiagnostics`** — exposes raw `Map.SpatialReference`, `Extent.SpatialReference`, `Camera` (X/Y/Z/Scale/Heading/Pitch/Roll), and `Map.CalculateFullExtent()` separately. First-class diagnostic for projection/extent debugging. ([03a2335](../../commit/03a2335))
- **`pro.getProjectInfo`** — project-level metadata: name, .aprx path, home folder, default geodatabase, default toolbox, counts of maps/layouts/toolboxes, active map info. Lets agents orient before operating. ([3462640](../../commit/3462640), MCP wrapper [edb2d48](../../commit/edb2d48))
- **`pro.clearSelection`** — first-class clear-selection. With no args, clears every feature layer; with a layer name, clears just that one (errors on missing layer). Replaces the `run_gp_tool("management.SelectLayerByAttribute", [...CLEAR_SELECTION...])` workaround. ([3462640](../../commit/3462640), MCP wrapper [edb2d48](../../commit/edb2d48))

### Fixed
- **G1 — extent values can exceed SR valid bounds** — `MapView.Extent` returns the literal geometric viewport rectangle centered on the camera at the current scale, which can extend past ±180°/±90° when zoomed out far enough that the rectangle is bigger than Earth. Pro doesn't clamp to the SR valid domain. Now: for geographic SRs, clamp `xmin/ymin/xmax/ymax` to `±180/±90` and report `clampedToSrValidRange: true` only when at least one bound was actually trimmed. ([f9e5579](../../commit/f9e5579), polish [afc635a](../../commit/afc635a))
- **R3-2 — timeouts no longer trigger 4 retries × full timeout duration** — A genuine handler hang won't be resolved by retrying for 8 more minutes. `BridgeClient` now returns a structured `{success:false, error:"timeout: ..."}` response immediately on timeout, bypassing the retry loop. Transient connection errors (broken pipe, pipe-not-yet-up) still retry with backoff. ([3bfe6c1](../../commit/3bfe6c1))
- **NaN/Infinity in JSON responses** — Pro SDK returns `NaN` for properties that don't apply to the current view mode (`Camera.Z` in 2D, etc.). Default `System.Text.Json` throws `ArgumentException` mid-serialization. `SendAsync` now uses `JsonNumberHandling.AllowNamedFloatingPointLiterals` so these values serialize as `"NaN"`/`"Infinity"` strings instead of breaking the response. ([e4eb41d](../../commit/e4eb41d))

### Superseded
- **G1 first attempt — reproject Extent to Map SR** ([7389d0c](../../commit/7389d0c)) was a no-op because both SRs report as the same WGS84 even when the numeric values clearly aren't in WGS84 space. The diagnostic at `03a2335` revealed the real root cause (geometric rectangle exceeding SR domain), and `f9e5579` is the correct fix. The original commit is left in history for traceability.

---

## [Round 2] — 2026-04-23

### Added
- **Logger gap closure** — `RunAsync` now calls `LogNonSuccess(req, resp.Error)` after `HandleAsync` returns when `!resp.Ok`. Previously only thrown exceptions reached `mcp-bridge.log`; structured `{success:false}` returns from F5/F6-style failure paths left no audit trail. New `LogNonSuccess` helper mirrors `LogException` structure. ([a584d12](../../commit/a584d12))

### Fixed
- **G2 — typed-return MCP tools collapsed structured errors to generic** — `GetActiveMapName`, `ListLayers`, `CountFeatures`, `ZoomToLayer` used `throw new Exception(r.Error)` on bridge failure. The MCP SDK swallows thrown exception messages, leaving only `"An error occurred invoking 'X'"`. Unified to `Task<string>` + `FormatResult` so bridge errors reach the agent as structured JSON. Slight response-shape change on success: `count_features` now returns `{"count": 51}` instead of bare `51`, etc. ([c711551](../../commit/c711551))
- **G3 — `pro.zoomToLayer` returned `true` on missing layer** — F4-class silent-success. Now throws `InvalidOperationException` ⇒ structured error. ([bd364c0](../../commit/bd364c0))
- **G4 — `pro.runGPTool` returned `"GP tool failed: "` empty body when `result.Messages` was empty** — F5-class empty-error-body. Now produces a tool-name-aware fallback message. ([619d723](../../commit/619d723))
- **G5 — `BridgeClient.RequestTimeoutMs` raised from 30s to 120s** — `create_project` on a fresh template can take 60+ seconds (template copy, .gdb init, UI initialization). The 30s default cut off mid-operation, returning a generic error while Pro kept grinding. 120s default handles slow Pro ops; per-tool override still available via `ARCGIS_MCP_REQUEST_TIMEOUT_MS`. ([832a133](../../commit/832a133))
- **G7 — MCP server held stale pipe state across Pro restarts** — `BridgeDiscovery.Discover()` was called exactly once at MCP server startup; the result was pinned in the singleton `BridgeClient`. When Pro restarted with a new PID, every subsequent tool call tried to connect to the dead pipe. `BridgeClient` now takes a `Func<string>` resolver and re-invokes it inside `SendOnceAsync`, so per-request rediscovery follows Pro across restarts automatically. ([27672f8](../../commit/27672f8))

---

## [Round 1] — 2026-04-23

Original audit identified seven concrete bugs in `ProBridgeService.cs`. All fixed and verified via the Phase 1–8 regression suite.

### Fixed
- **F1 — `HandleCreateProject` thread-affinity exception + modal "Save?" dialog** — `Project.CreateAsync` requires the WPF GUI thread. `QueuedTask.Run` puts work on the MCT, which raises `"The calling thread cannot access this object because a different thread owns it."`. Final fix: wrap body in `await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Project.CreateAsync(settings))` and unwrap the nested `Task`. Also calls `Project.Current.SaveAsync()` first to suppress the modal "Save?" dialog. ([927c086](../../commit/927c086) → [970c07f](../../commit/970c07f) → [7e822ea](../../commit/7e822ea))
- **F2 — `HandleOpenProject` thread-affinity** — same class as F1; same fix pattern. ([7a63be3](../../commit/7a63be3) → [970c07f](../../commit/970c07f) → [7e822ea](../../commit/7e822ea))
- **F3 — `HandleOpenLayout` GUI-thread exception** — `FrameworkApplication.Panes.CreateLayoutPaneAsync` is GUI-thread-only, not MCT. Layout-item lookup stays in `QueuedTask.Run`; the pane creation moves to `FrameworkApplication.Current.Dispatcher.InvokeAsync`. ([20197f5](../../commit/20197f5))
- **F4 — `HandleCountFeatures` returned `0` silently for missing layers** — `if (fl == null) return 0;` collapsed two distinct cases (layer exists with 0 features, layer doesn't exist) into the same response. Agents cascaded wrong actions off the silent zero. Now throws `InvalidOperationException($"Layer not found: {layerName}")`. ([df6d3fc](../../commit/df6d3fc))
- **F5 — `HandleRunModel` returned `"Model execution failed: "` with empty body** — when arcpy failed before emitting messages, `string.Join("; ", result.Messages)` produced an empty string. Now defensive: filter for `GPMessageType.Error`, fall back to a tool-name-aware "no messages" string. ([ce028e1](../../commit/ce028e1))
- **F6 — `HandleExportLayout` returned `success:true` for writes that didn't land** — `layout.Export(ef)` doesn't always throw on permission failure (UAC VirtualStore quietly redirects writes to System32, leaving the file at `%LOCALAPPDATA%\VirtualStore\...` instead of the requested path). Now wraps `Export` in try/catch AND post-checks `File.Exists(output)`; returns `{success:false, error:"file was not written"}` if the export silently no-op'd. ([50d3a20](../../commit/50d3a20))
- **F7 — `HandleRunGPTool` raised `InvalidOperationException` on nested-array params** — GP tools that take value-tables (`management.CalculateGeometryAttributes`, `management.JoinField`, `analysis.SpatialJoin` field-map, etc.) passed parameters as `[["field", "property"], ...]`. The handler called `.GetValue<string>()` on each element, which throws on `JsonArray`. New `FlattenGpParam(JsonNode)` helper recursively flattens two-level arrays into arcpy's value-table string syntax (`"f1 v1;f2 v2"`). ([3333950](../../commit/3333950))

---

## [Pre-rounds] — earlier 2026-04

Initial implementation, ModelBuilder integration, IPC resilience, container packaging.

### Added
- **ModelBuilder tools** — `create_toolbox`, `create_model`, `describe_model`, `update_model`, `run_model`, `list_models`. Includes `AtbxManager.cs` for `.atbx` ZIP marshalling (the file format is a ZIP of JSON model definitions, **not** SQLite as the file extension suggests). ([11e8943](../../commit/11e8943))
- **Project/layer/layout tools** — `create_project`, `open_project`, `add_layer_from_url`, `list_layouts`, `open_layout`, `list_layout_elements`, `set_layout_text`, `export_layout`. ([a5f5c84](../../commit/a5f5c84))
- **Per-Pro-instance pipe routing** — Each Pro instance binds `ArcGisProBridge_<PID>` and writes a registry entry at `%LOCALAPPDATA%\ArcGisMcpBridge\<PID>.json`. Replaces the legacy single hard-coded `ArcGisProBridgePipe`. Multiple Pros can each have their own bridge. ([0eb417c](../../commit/0eb417c))
- **Single-file MCP server publish** — `.mcp.json` points at `McpServer/ArcGisMcpServer/publish/ArcGisMcpServer.exe` instead of `dotnet run`. Faster cold start; published via `build-mcp-server.ps1`. ([0eb417c](../../commit/0eb417c))
- **IPC retry & timeout** — `BridgeClient` now retries failed pipe calls with exponential backoff and enforces a per-request timeout. Defaults overridable via `ARCGIS_MCP_*` env vars. ([5a579c8](../../commit/5a579c8))
- **Container image** — `McpServer/ArcGisMcpServer/Dockerfile` for packaging/distribution. (Note: named pipes don't traverse Linux container boundaries; image is for Windows-container hosts that share the pipe namespace.) ([5a579c8](../../commit/5a579c8))
- **Surface bridge errors** — error text from the Add-In propagates to MCP responses instead of being swallowed. ([a10ab9c](../../commit/a10ab9c))

### Fixed
- **`MakeEnvironmentArray` named-arg call** — was being called with a positional `Dictionary<string,object>`, which bound to the first parameter (`workspace`) and produced cryptic `RuntimeBinderException` about `MapMember`. Fixed to use named-argument syntax. ([8c1d58b](../../commit/8c1d58b))
- **JSON serializer for `JsonNode`** — naive `new JsonSerializerOptions { WriteIndented = true }` threw `"TypeInfoResolver not specified"` when serializing custom JsonValue types. Now derives from `JsonSerializerOptions.Default` to inherit `DefaultJsonTypeInfoResolver`. ([a79f528](../../commit/a79f528))
- **GP runs hit `ERROR 000210` on output already exists** — `overwriteOutput` is now enabled for model/GP runs so programmatic invocation is idempotent-friendly. ([505c222](../../commit/505c222))
- **ATBX manager: three bugs found in first end-to-end test** — fixed during initial validation. ([3b36dd7](../../commit/3b36dd7))

---

## Verification status

| Round | Verified | Method |
|---|---|---|
| Pre-rounds | yes | initial validation |
| Round 1 (F1–F7) | yes | Phase 1–8 regression suite via MCP routing |
| Round 2 Cycle A (G3, G4, Logger) | yes | direct-pipe smoke tests |
| Round 2 Cycle B (G2, G5, G7) | yes | post-rebuild MCP routing |
| Round 3 Cycle A (G1, R3-2, getViewDiagnostics, NaN serializer) | yes | direct-pipe + post-rebuild MCP |
| Round 3 Cycle A (R3-3, R3-4) | yes | post-rebuild MCP routing |
| Round 3 Cycle B (R3-5..R3-8) | **deferred** | code compiles clean on both sides; runtime verification awaiting user |

---

## Fix-pattern lineage (cheatsheet)

Future contributors should recognize these proven patterns. Each row shows where the pattern was first introduced — rather than reinventing, copy the idiom.

| Pattern | First introduced in |
|---|---|
| WPF Dispatcher + nested-Task unwrap for project ops | F1/F2 ([7e822ea](../../commit/7e822ea)) |
| GUI-thread dispatch for layout panes | F3 ([20197f5](../../commit/20197f5)) |
| Throw `InvalidOperationException` on missing ref | F4 ([df6d3fc](../../commit/df6d3fc)) |
| Defensive GP error message with tool-name fallback | F5 ([ce028e1](../../commit/ce028e1)) / G4 ([619d723](../../commit/619d723)) |
| `try { sdk(); } catch + File.Exists` post-check for silent-success | F6 ([50d3a20](../../commit/50d3a20)) |
| `FlattenGpParam` two-level arcpy value-tables | F7 ([3333950](../../commit/3333950)) |
| `Task<string>` + `FormatResult` for all MCP wrappers | G2 ([c711551](../../commit/c711551)) |
| Per-request `Func<string>` pipe rediscovery | G7 ([27672f8](../../commit/27672f8)) |
| Timeout returns structured response, doesn't retry | R3-2 ([3bfe6c1](../../commit/3bfe6c1)) |
| `LogNonSuccess` for `{success:false}` audit trail | Logger gap ([a584d12](../../commit/a584d12)) |
| Geographic-SR clamp to ±180/±90 | G1 ([f9e5579](../../commit/f9e5579)) |
| `JsonNumberHandling.AllowNamedFloatingPointLiterals` for NaN doubles | NaN serializer ([e4eb41d](../../commit/e4eb41d)) |
