---
name: debugging-model-runs
description: Use when run_model, start_run_model, or run_gp_tool fails, hangs, times out, or returns an ambiguous result — including ERROR 000735, 000464, 000732, 000840, 000210, a wedged/silent GP Python lane, .pyt steps that seem to never return, get_run_status jobs stuck "running", or when a run's success/failure is unclear because mcp-bridge.log is silent.
---

# Debugging Model Runs

## Overview

`run_model` bypasses Pro's own ModelBuilder engine and executes each step directly via `Geoprocessing.ExecuteToolAsync` (see CLAUDE.md "run_model is special"). This means failures surface as raw GP error codes, not ModelBuilder's UI messages, and **the bridge log only records failures — a successful run leaves no log entry.** Never read log silence as success; never read a client-side timeout as bridge failure. Verify with data, not inference.

## When to use

- `run_model` / `start_run_model` returned an error, timed out client-side, or the MCP client gave up waiting.
- A `get_run_status` job is stuck, or you're unsure whether a long run actually finished.
- You see GP error codes 000735, 000464, 000732, 000840, 000210, or a Python `SyntaxError` inside a `CalculateField` step.
- A `.pyt` (PythonScriptTool) step behaves oddly — wrong output path, selection lost, timeout.
- Every tool suddenly slot-mismatches right after a Pro version upgrade.
- A multi-output custom tool's downstream steps read stale/wrong paths.

## When NOT to use

- The model never ran at all — `create_model`/`update_model`/`describe_model` round-trip problems, wrong parameter order, everything-required-when-it-shouldn't-be, stray parameter promotion. Use **authoring-atbx-models** instead.
- Pro's own **Model Report UI** shows something odd (enumerated-value drops on `SummarizeWithin`, a compound-CRS stamp) but the run actually completed and outputs are correct. These are documented cosmetic non-defects — don't chase them.

## Quick reference — GP error codes

| Code / symptom | Cause | Fix |
|---|---|---|
| **000735** required parameter empty | Output var's slot wasn't in the tool signature and no pre-pass caught it (`ProBridgeService.cs:3331` — the in-place-output pre-pass exists for this); or an authoring-side optionality bug | Check the "#" sentinel / `optional`+`exposed` fields on the parameter — see **authoring-atbx-models** |
| **000464** schema-lock contention | Pro's GP session holds shared locks on every GDB touched this run; a `.pyt` child needs an exclusive lock | Two layers already handle this automatically: (1) all `.pyt` children share a run-private `pyt_scratch_<stamp>.gdb`, opt out with `pytIsolatedScratch=false`; (2) on a residual 000464 the runner retries once with `isolate_inputs` (child copies contended inputs itself). **Fixed 2026-07-12:** the trigger is now `IsLockContentionError`, matching `"000464"`, `"schema lock"`, or a `cannot acquire...lock` regex (all case-insensitive) instead of a literal `"000464"` substring — differently-worded lock errors now retry too. **Before 2026-07-12 builds:** the check was a literal substring match on `"000464"` only; any other wording failed the step with no retry. This is an Add-In-side fix — confirm the bridge has actually been redeployed before assuming the broader match is live |
| **000732** / **000840** input does not exist / not a Feature Layer | No active map view — layer-name refs in the model can't resolve after a Pro restart; also fires when a relative catalog path (`.\X.gdb\FC`, stored by "Store relative path names" projects) gets passed through unresolved | Open/focus a map tab and retry (bridge adds this hint automatically, `ProBridgeService.cs:3510-3521`); check whether the failing ref is a **layer name vs. a path** — relative catalog paths resolve against the toolbox's own home folder (`ResolveRelative`/`toolboxDir`, `ProBridgeService.cs:2846`), not an arbitrary CWD; also check leftover selections restricting a layer-name input — `clear_selection` first |
| **000210** | Two distinct causes: (1) first write into a not-yet-existing output GDB (fresh toolbox's `scratch.gdb`, or `env.scratchGDB` fallback, comment at `ProBridgeService.cs:3404`); (2) output already exists (comment at `ProBridgeService.cs:3842`) | Already auto-fixed for both — (1) the executor `CreateFileGDB`s any missing parent before the step runs and logs a message either way; (2) the default env enables overwrite (`ProBridgeService.cs:3842`). Don't chase this as a root cause |
| Python `SyntaxError` in `CalculateField` | `%VarName%` not substituted before arcpy sees the expression | Confirm `SubstituteModelVars` covers the param (`ProBridgeService.cs:2892` region) — this mirrors ModelBuilder's own string substitution |

## First moves, in order

1. **MCP client timed out?** The bridge is very likely still running — a client-side timeout is not a bridge failure.
   - Async job: call `get_run_status` with the `jobId` from `start_run_model`.
   - Sync call that just never returned to the client: check for expected outputs directly (step 3) rather than re-issuing the run.
2. **Read `mcp-bridge.log`** at the *active project's* home folder (`Project.Current.HomeFolderPath`, `ProBridgeService.cs:4068`/`4093`, inside `LogException`/`LogNonSuccess`) for the real GP error code. MCP clients routinely give up before the bridge returns the actual error — the log has it even when the client showed nothing useful.
3. **Verify claimed success with data**, not log absence: `list_gdb_contents` / `count_features` against the run's target GDB. The log only records failures (`ProBridgeService.cs:4059-4104` — `LogException`/`LogNonSuccess` are the only writers); a quiet log after a run that "seemed to work" is not proof.

Use `start_run_model` + `get_run_status` for anything that might run longer than ~2 minutes wall-clock (`ProTools.cs:729`) — Claude Desktop's own tool-call ceiling is ~4 minutes and it will report a false timeout on a run the bridge is still executing correctly.

## .pyt (PythonScriptTool) out-of-proc caveats

`.pyt`-hosted steps execute as a separate arcpy process via `propy.bat`, not in-proc — because in-proc `ExecuteToolAsync` on a `.pyt` path never returns. Consequences:

- **No selection propagation** — a selection made in the parent Pro session is invisible to the child.
- **No `in_memory` datasets** — inputs must be concrete paths; the child can't see the parent's memory workspace.
- **Per-step timeout** `pytTimeoutSeconds`, default 3600s (`ProBridgeService.cs:2684`).
- **`pytMode="skip"`** runs a partial model: the `.pyt` step and everything downstream of it cascade-skip (tracked via `skippedOutputVarIds`), reported back as `skippedPytSteps`. Default is `pytMode="execute"`.
- The runner reports `kwargs_used` in its result — after an `isolate_inputs` retry, output mapping follows the **isolated copy**, not the original parent-GDB path (`ProBridgeService.cs:3144`).

## Timing rules

- Bridge reads and native-GP `run_model` (including out-of-proc `.pyt` dispatch) answer **~60-95s** after Pro launch.
- The **in-proc Python lane** (`execute_python`, `CalculateValue`, script tools) self-gates for **180s** (`PythonWarmupSeconds`, `ProBridgeService.Python.cs:35`). A too-early Python GP call can wedge that lane for the **entire session** — only a Pro restart clears it.
- Do not stall non-Python work waiting out the full 180s; only the Python lane needs it.

## Concurrency rule

**Fixed 2026-07-12:** `RunModelCore` now acquires a process-wide `SemaphoreSlim` (`_runModelGate`) for the whole root-level execution before doing any work, and releases it in a `finally` — nested `ExecuteGraphAsync` recursion (nested models) does not re-acquire, so it's held once per root run. A second `run_model`/`start_run_model` call that arrives while a run is in progress now blocks on the gate and gets a `"waiting: another model run is in progress"` message (both the sync response and, for the async path, `get_run_status`'s messages) instead of silently racing the first job.

**Before 2026-07-12 builds — this is an Add-In-side fix, confirm redeploy status before relying on it:** jobs lived in a `ConcurrentDictionary<string, RunJob>` keyed by `jobId` (`ProBridgeService.cs:2612`) — thread-safe as a dictionary, but nothing serialized the underlying GP work. Two concurrent jobs drove the same single arcpy session and either could call process-wide `management.ClearWorkspaceCache` mid-run, pulling the rug out from under the other job's in-flight step. **Never run two `run_model`/`start_run_model` jobs simultaneously against a bridge that predates this fix.**

## Landmines

- **Multi-derived-output steps** (script tools, nested models with 2+ derived outputs): the executor used to only refine derived-output values from the GP result's `ReturnValue` when there was exactly **one** derived output slot; with 2+, only the pre-pass's placeholder fallback value was used for every slot. **Improved 2026-07-12:** when `IGPResult.Values`' count exactly matches the declared derived-output slot count, values now map onto them positionally; on any count mismatch (extra/missing entries) it still falls back to the placeholder values, now with a loud `Warning` message rather than silently guessing. This assumes `IGPResult.Values` and the declared derived-output slots are in the same order — expected since both are declared-order, but an unverified assumption on a host ordering divergence (not live-tested against Pro; this is an Add-In-side fix, confirm redeploy status). **Before 2026-07-12 builds:** the single-output-only limitation applies as originally documented — symptom: a custom multi-output tool "works" but downstream consumers silently read the wrong (or empty) path.
- **Pro-version upgrade landmine:** `SystemToolboxCatalog` self-disables — silently, for the process lifetime — if its startup sanity check fails: it parses `analysis.Buffer` and requires its 8 slots in exact known order (`SystemToolboxCatalog.cs:137-150`). If an ArcGIS Pro upgrade changes `tool.content`'s on-disk shape, this check fails once and the dynamic catalog goes dark for every tool, not just Buffer. Symptom: a sudden wave of slot-mismatch errors across many previously-working tools right after a Pro upgrade — restart Pro won't help mid-process, but a fresh process will re-attempt the check. **Fixed 2026-07-12:** `SystemToolboxCatalog.DisabledReason` now names the specific cause (root not found, enumeration exception + message, or the Buffer sanity-check mismatch), and a `GpToolCatalog.ResolveSignature` miss during `run_model` appends a `Warning` message quoting it — so this landmine now announces itself in the run's messages instead of degrading silently to insertion-order dense-packing. **Before 2026-07-12 builds** (Add-In not yet redeployed): still silent — diagnose by checking whether `analysis.Buffer`'s signature resolves at all, as originally documented.

## Common mistakes

- Treating a Claude Desktop tool-call timeout as a failed run and re-issuing `run_model` — on a bridge with the 2026-07-12 concurrency gate this now just queues behind the still-running first job (see Concurrency rule) rather than racing it, but it's still wasted time and a confusing "waiting" message; check `get_run_status` / outputs first regardless of which behavior applies.
- Assuming a run succeeded because `mcp-bridge.log` has no new entries — the log is failure-only by design; the Aurora and BankSiting validation runs were both confirmed via GDB output presence, never via log silence.
- Calling `execute_python` or any Python-backed GP tool in the first few minutes of a Pro session "just to check something quick" — this can permanently wedge the in-proc Python lane for the rest of the session, requiring a full Pro restart to clear.
- Chasing ERROR 000210 as a real bug after commit `d47c280` — it's auto-fixed; check the run message for "Auto-created missing output GDB" instead of investigating further.
- Assuming a differently-worded lock error will get the 000464 retry on a bridge that predates the 2026-07-12 fix — before that, the check was a literal substring match on `"000464"` in the error text; other lock-contention wordings failed the step with no retry. As of 2026-07-12 (pending Add-In redeploy) `"schema lock"` and `cannot acquire...lock` wordings retry too via `IsLockContentionError`.

This file is intentionally short of the usual 100-250 line target: `run_model` debugging is a narrow, well-bounded failure surface (a handful of GP error codes, one out-of-proc dispatch path, one concurrency rule), and every additional line above already covers a verified, distinct failure mode. Padding it further would mean inventing scenarios rather than documenting real ones.
