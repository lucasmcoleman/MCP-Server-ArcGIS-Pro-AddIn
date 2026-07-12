---
name: operating-live-pro
description: Use when launching, restarting, or diagnosing a live ArcGIS Pro session driven by the bridge — Pro won't respond, "Shutting down..." hangs, execute_python/Python GP calls hang or return a warm-up retry error, wrong Pro instance gets driven, BridgePinException, list_bridges/select_bridge behavior, GP tools silently return fewer/wrong features after a selection, or the Start MCP Bridge ribbon button's claim needs sanity-checking.
---

# Operating a Live ArcGIS Pro Session

## Overview
ArcGIS Pro is a slow-starting, stateful host process — the bridge is a guest inside it, not something you control directly. Every interaction (launch, restart, timing, routing) must respect Pro's lifecycle instead of forcing it, because forcing it (killing the process, calling Python too early, writing to a canvas-open file) causes damage that only a full Pro restart repairs.

## When to use
- Launching Pro for a bridge session, or after a rebuild/redeploy.
- `execute_python`, `CalculateValue`-backed calls, or `.pyt` steps hang or return a "warming up" error.
- A tool call seems to hit the wrong Pro instance, or you see `BridgePinException`.
- A GP tool silently returns fewer/wrong features than expected against a named layer.
- Pro won't close, or `list_bridges`/`ping` results look inconsistent with what's actually running.
- Deciding whether to trust the ribbon's "Start MCP Bridge" button.

## When NOT to use
- Pure `.atbx` file-layer work (reading/writing ModelBuilder files via `AtbxManager`, running `AtbxTests`) needs Pro **installed**, not **running** — skip this skill entirely, no live session involved.
- Authoring model steps, signatures, or parameter interfaces — that's a modeling/authoring concern, not a live-session concern.

## Quick reference

| Symptom | Cause | Fix |
|---|---|---|
| Pro launched via `Start-Process` fails Python init ("input line is too long") | Agent shell's PATH is corrupt/oversized, poisons conda activation | Launch via `explorer.exe` only (below) |
| `execute_python` / `CalculateValue` / `.pyt` call hangs forever, never returns | Called within ~180s of Pro launch, or a `.pyt` path was called in-proc | Wait for the warmup gate; never call `.pyt` paths in-proc |
| Every later Python GP call queues forever, but native GP tools still work | GP Python lane got permanently wedged by an early call | Only a Pro restart clears it — no in-session fix |
| GP tool run against a layer *name* returns fewer/wrong features, no error | Leftover selection from an earlier `select_by_attribute`/`select_by_location` restricts the input | `clear_selection` before the GP step |
| Requests go to the wrong Pro instance / edit the wrong project | Unpinned routing followed most-recently-started Pro, or pin didn't match | `list_bridges` to see routing; pin via `ARCGIS_PROJECT` or `select_bridge` |
| `BridgePinException` | `ARCGIS_PROJECT` (or a `select_bridge` override) set to a project no live bridge has open | Confirm the pinned project is actually open; check spelling/`.aprx` |
| "Start MCP Bridge" ribbon button says bridge is running | Button is a no-op `MessageBox` — always says this | Never trust it; check with a real op (see Diagnosing below) |
| Pro stuck at "Shutting down..." | GP call pending, or Pro silently refuses `CloseMainWindow` | Check the window title before assuming the close worked; ask the user, don't force-kill |
| Reopened ModelBuilder canvas still shows pre-write state after a bridge `.atbx` write | Pro doesn't auto-reload an open canvas after an out-of-band write | File on disk is correct — tell the user to reopen the tab |

## Launching Pro

Launch **only** via Explorer, never a shell's `Start-Process`:

```powershell
& "C:\Windows\explorer.exe" "<path-to-your>.aprx"
```

The agent shell's PATH is corrupt on this machine (unexpanded `%PATH%`, over the cmd length limit); a process launched with `Start-Process` inherits that broken environment and conda activation for arcpy's Python fails ("input line is too long", CLAUDE.md:125). Explorer hands Pro the user's clean logon environment instead.

Exception: with the user's **explicit permission** for a specific automation task (e.g. overnight unattended runs), `& "C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe" "<aprx>"` from a shell may be used — but expect it to still stick on Pro's Start Page or a sign-in modal that needs a human click. This is not the default path.

## Never kill Pro — ask the user

Never `taskkill`/`Stop-Process` ArcGIS Pro to force a restart. Ask the user to close and reopen it themselves (CLAUDE.md, "Never auto-kill ArcGIS Pro"). Reasons this matters in practice:

- Pro's own shutdown can hang at "Shutting down..." and silently refuses `CloseMainWindow` while a GP call is pending (CLAUDE.md:126) — check the window title before assuming a close attempt worked; don't compound a hang by force-killing on top of it.
- A forced kill risks losing unsaved edits and leaves project locks / a dirty AssemblyCache behind.

After the user confirms Pro is back up, confirm which `.aprx` is actually open — call `get_project_info` (`ProTools.cs:330`) or `list_toolboxes` (`ProTools.cs:532`) rather than assuming the project you expect is the one that loaded.

## Timing after launch — don't call Python too early

Two different readiness clocks apply after Pro launches, and they are **not the same**:

- **Bridge reads and native-GP `run_model` (including out-of-proc `.pyt` dispatch)**: usable much earlier, empirically ~60–95s after launch (CLAUDE.md, "Things that bite"). Don't stall non-Python work waiting for the full window below.
- **In-proc Python lane** (`execute_python`, anything riding `management.CalculateValue`, script tools): hard-gated for 180 seconds of Pro process uptime. `HandleExecutePython` in `AddIn/APBridgeAddIn/ProBridgeService.Python.cs:35,47-59` measures `Process.GetCurrentProcess().StartTime` and refuses with a "still warming up, retry in ~N seconds" error until that window passes; the first successful call flips `_pythonProven` and the gate never re-engages for that session.

Calling a Python-touching GP op inside that window doesn't just fail cleanly — it can **hang forever and permanently wedge the GP Python lane for the rest of the session** (native GP tools keep working; only a Pro restart clears the wedge). Treat the retry hint as load-bearing, not a suggestion.

Separately: `ExecuteToolAsync` on an out-of-project `.pyt` path never returns in-proc, warm or not — that's structural, not a timing issue. This is why `execute_python` rides `CalculateValue` and why `run_model`'s `.pyt` steps dispatch out-of-proc via `propy.bat` instead. Don't resurrect an in-proc `.pyt` design.

## Selection hygiene

Run `clear_selection` (`ProTools.cs:185-199`, wraps `pro.clearSelection`) before any GP op that takes a layer by **name** if an earlier `select_by_attribute`/`select_by_location` touched that map. A leftover selection silently restricts the tool's input to just the selected features — no error, just quietly wrong output. `clear_selection(layer)` clears one layer; omit `layer` to clear every feature layer and standalone table in the active map.

## Multi-instance routing

Each Pro instance's Add-In registers `%LOCALAPPDATA%\ArcGisMcpBridge\<PID>.json` on load (`BridgeRegistry.Register`, `AddIn/APBridgeAddIn/BridgeRegistry.cs:27-42`), storing `pipeName`, `projectPath`, and `projectName` **with the `.aprx` extension**. `BridgeDiscovery.Discover()` (`McpServer/ArcGisMcpServer/Ipc/BridgeDiscovery.cs:79-104`) resolves routing on every request:

1. **`ARCGIS_PROJECT` env var set** → strict pin. Match is tolerant (bare name / `name.aprx` / full path, case-insensitive, both sides normalized — `BridgeDiscovery.Normalize`, lines 137-145) against either `projectName` or `projectPath`. No match → throws `BridgePinException` (lines 14-30) rather than falling back to any other instance — a pinned server must never silently drive the wrong Pro process.
2. **Unpinned** → most-recently-started live bridge (`OrderByDescending(StartedUtc)`).
3. **Unpinned, nothing live** → falls back to the legacy hard-coded pipe name `ArcGisProBridgePipe`.

`list_bridges` (`ProTools.cs:1571-1602`) is a pure registry read — it works even with no Pro instance running, and reports each live PID/project/pipe plus which one is currently `selected`. `select_bridge('ProjectName')` (`ProTools.cs:1618-1636`) sets a runtime override with the same strict semantics, letting one **unpinned** agent switch across multiple Pro instances; it clears with no argument. It is **refused** when `ARCGIS_PROJECT` is set — the operator's env pin is the multi-agent isolation guarantee and cannot be overridden from inside a session.

Pattern for multiple simultaneous agents: one Pro instance per agent, each agent's `.mcp.json` setting `"env": {"ARCGIS_PROJECT": "<project>"}` (the repo's own `.mcp.json` at the root ships an empty `env: {}` — fill it in per agent workspace, not here).

## Diagnosing bridge health — don't trust the ribbon button

`Button1.OnClick()` (`AddIn/APBridgeAddIn/Button1.cs:25-30`) is a **no-op**: it always shows `MessageBox.Show("MCP Bridge is running (auto-started with ArcGIS Pro).")`, regardless of whether the bridge is actually alive. Never use it to diagnose anything.

Also don't over-trust `ping` (`ProTools.cs:310-314`) for bridge health — its own description says it validates the MCP server "without depending on ArcGIS Pro"; it never touches the pipe or Pro at all. `list_bridges` is also a registry read only (works with zero live Pro instances). To actually confirm the *live* bridge inside a specific Pro process answers, call an op that round-trips through the pipe — `get_project_info` or `list_toolboxes` are the cheapest real checks. The only recovery from an actually-wedged bridge is a Pro restart (ask the user; see above) — there is no in-session bridge-only restart.

## Canvas staleness after writes

Not a live-session bug: after any bridge `.atbx` write (`WriteAtbxAtomically`), Pro's open ModelBuilder canvas tab keeps showing the pre-write state until the user reopens that tab — the file on disk is already correct. Don't re-run the write or treat this as a failure; tell the user to reopen the tab. See the model-authoring skill for the write path itself.

## Common mistakes

- **Launching Pro with `Start-Process` from an agent shell.** Broke conda activation with "input line is too long" — the shell's PATH is corrupt/oversized on this machine. Always use `explorer.exe "<path>.aprx"`.
- **Calling `execute_python` (or any Python-touching GP tool) within the first ~3 minutes of Pro launch.** During the 2026-06-11 overnight audit this wedged the GP Python lane permanently for that session — native tools kept working but every later Python call queued forever; only a Pro restart cleared it. Respect the warmup gate's retry hint.
- **Trusting the "Start MCP Bridge" ribbon button as a health check.** It's a hardcoded `MessageBox`, not a live probe — it says the bridge is running even when it isn't.
- **Leaving a selection active after `select_by_attribute` before running a downstream GP tool by layer name.** Caught live during the 2026-04-23 capability audit: selecting 3 west-coast states in `USA_States` and then running a GP tool against the layer name would have silently processed only those 3 features. Always `clear_selection` first, or pass a concrete feature-class path instead of a layer name.
- **Force-killing Pro to speed up a rebuild/redeploy cycle.** Explicitly rejected by the user in an earlier session (`taskkill`/`Stop-Process` attempts) — it risks losing unsaved work and leaves project locks / a dirty AssemblyCache behind. Ask the user to restart Pro.
- **Assuming an unpinned agent is still talking to the same Pro instance after a second Pro process launches.** Unpinned routing follows most-recently-started — a second Pro window silently steals routing from the first. Pin via `ARCGIS_PROJECT` (or `select_bridge`) whenever more than one instance might be live.
