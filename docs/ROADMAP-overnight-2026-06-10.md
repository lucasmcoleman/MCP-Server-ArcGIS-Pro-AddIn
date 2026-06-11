# Overnight Deep-Dive Roadmap — 2026-06-10

Synthesized from a 25-agent audit (5 review dimensions with adversarial verification,
4 capability-gap analysts incl. SDK research + competitive scan) plus a full inline
read of the codebase. Self-approved per Lucas's overnight authorization.

## Audit verdict in one paragraph

The architecture is sound and unusually well-commented: clean separation between the
MCP exe and the Pro Add-In, per-PID discovery handles Pro restarts transparently, the
canvas-deadlock atomic-write pattern is correct, and the step-by-step model executor
is a genuinely clever workaround for Pro's whole-chain pre-validation. The two big
truths the audit surfaced: (1) the ModelBuilder read-modify-write path silently
corrupts models that use multi-input connections, preconditions, iterators, or
output-parameters — fine for bridge-authored models, dangerous for human-authored
ones; (2) the tool surface covers maybe a third of what "an LLM entirely drives
ArcGIS Pro" requires — the missing pillars are vision (screenshots), arbitrary arcpy
(Python escape hatch), symbology/labeling, feature editing (update/delete), camera
control, map/project bootstrap, GP tool schema discovery, and catalog browsing.

## Batch A — Correctness fixes (bridge-side)
- [x] A1 Pipe server: concurrent multi-instance (long op no longer blocks ping/status)
- [x] A2 JSON scalar coercion (numbers/bools) in run_gp_tool / run_model / model defs
- [x] A3 %Var With Spaces% substitution in model literals
- [x] A4 run_gp_tool returns returnValue + output values
- [ ] A5 create_toolbox: refuse to truncate an existing .atbx (CRITICAL data loss)
- [ ] A6 Multi-input element_id arrays (Merge/Union/Append) — full round-trip + exec
- [ ] A7 Preconditions: topo-sort edges + round-trip preservation
- [ ] A8 Per-step environments applied during run_model
- [ ] A9 Iterator/unknown steps: writer throws instead of silently corrupting
- [ ] A10 Derived-output-as-parameter round-trip fidelity
- [ ] A11 SanitizeGdbName collision suffixes
- [ ] A12 Params dict case-insensitive; completedSteps payload fix; step-name fallback
- [ ] A13 No-project guards; async-job failures logged; get_run_status incremental messages
- [ ] A14 BridgeClient: bridge-down → structured actionable error (not generic IOException)
- [ ] A15 PipeOptions.CurrentUserOnly both ends; create_project overwrite safety check
- [ ] A16 ProTools culture-invariant double formatting

## Batch B — Dynamic GP tool schemas (kills the Signatures whack-a-mole)
- [ ] B1 SystemToolboxCatalog: parse Pro's installed system toolboxes
      (C:\Program Files\ArcGIS\Pro\Resources\ArcToolBox\toolboxes\*.tbx directories,
      same tool.content JSON format AtbxManager already parses; JSON declaration order
      IS arcpy positional order — verified against management.Project & Buffer)
- [ ] B2 Executor + writer consult GpToolCatalog (hand-pinned, wins) → SystemToolboxCatalog
- [ ] B3 describe_gp_tool MCP tool (params, types, direction, optional, domains, defaults)
- [ ] B4 search_gp_tools (keyword search across ~1700 system tools)

## Batch C — Python escape hatch (the force multiplier)
- [ ] C1 bridge.pyt deployed to %LOCALAPPDATA%\ArcGisMcpBridge\ (ExecutePython tool:
      b64 code in, JSON result out, stdout via redirect_stdout, traceback captured)
- [ ] C2 pro.executePython via ExecuteToolAsync(pyt path) — in-process, CURRENT project
- [ ] C3 execute_python MCP tool (sync; long jobs via existing async-job pattern later)

## Batch D — Vision + camera
- [ ] D1 capture_map_view: MapView.Active.Export(PNGFormat) (+ optional extent)
- [ ] D2 zoom_to_extent (envelope + wkid), zoom_to_scale, zoom_to_selected
- [ ] D3 bookmarks: list / zoom-to / create

## Batch E — Editing CRUD completion
- [ ] E1 update_features (where|oids → attribute dict) via EditOperation.Modify
- [ ] E2 delete_features (explicit where|oids required)
- [ ] E3 add_polyline_features
- [ ] E4 save_edits / discard_edits / has_edits

## Batch F — Map management & layer properties
- [ ] F1 create_map, open_map_view (activate)
- [ ] F2 set_basemap
- [ ] F3 set_definition_query / clear (+ surfaced in get_layer_properties)
- [ ] F4 set_layer_transparency
- [ ] F5 set_labeling (enable/disable + expression)
- [ ] F6 map param on add_layer_from_file/url

## Batch G — Layout furniture
- [ ] G1 add_legend / add_north_arrow / add_scale_bar
- [ ] G2 add_layout_text
- [ ] G3 set_map_frame_extent (zoom frame to layer/extent)

## Batch H — Symbology (native CIM)
- [ ] H1 set_layer_symbology: simple (color/outline), unique-values, graduated-colors
- [ ] H2 get_layer_symbology summary

## Batch I — Catalog & data discovery
- [ ] I1 list_gdb_contents (feature classes / tables / rasters in any GDB)
- [ ] I2 describe_dataset (schema of any dataset path, no map membership needed)

## MCP-server-side (rides along with each batch)
- New tools for every new op; description fixes (cross-references, run_gp_tool
  sentinel/value-table syntax, run_model duration warning)
- "Not found" errors list available candidates
- CLAUDE.md drift fix (publish is self-contained+trimmed)

## Deliberately NOT tonight
- QueryDef joins / query_table (execute_python covers aggregation; revisit)
- Charts, reports, 3D/scenes, versioning, geocoding, Portal search
- HTTPS for HTTP mode (reverse proxy already terminates TLS)
- batch_commands pipelining
