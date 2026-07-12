---
name: validating-gis-outputs
description: Use when judging whether a run_model/run_gp_tool/execute_python result is CORRECT rather than merely "didn't error" — verifying a screening or siting model's output before handoff, explaining a feature-count or geometry mismatch against a hand-built Pro baseline, deciding if a CRS/field/selection discrepancy is a real bug or a benign divergence, or auditing GDB outputs after mcp-bridge.log stayed silent.
---

# Validating GIS Outputs

## Overview
The bar for a bridge-run result is scientific soundness, not bit-parity with Pro's own ModelBuilder/GP engine — a divergence from how Pro would have run it is "not a bug by definition as long as the results are still scientifically sound" (repo owner). Treat every divergence as a finding to explain, not an automatic defect, and never trust "no error" as proof of correctness — `mcp-bridge.log` only records failures (see Common mistakes), so absence of a log entry proves nothing.

## When to use
- A `run_model` / `run_gp_tool` / `execute_python` call returned success and you need to know whether the *output* is right, not just whether the call completed.
- A step's output feature count looks suspicious (near-zero, or near-100% of input survives a filter that should screen most of it out).
- A model result differs from a previously hand-built Pro baseline and you need to decide bug vs. explainable divergence.
- Downstream steps are failing on missing fields/wrong geometry type/wrong CRS and you need to trace which upstream step introduced it.
- You're about to hand off a deliverable and want a last-mile sanity pass.

## When NOT to use
- The client/project spec defines explicit acceptance criteria (thresholds, required fields, tolerances) — the spec wins over anything in this skill.
- Cartographic or layout quality (symbology, layout composition, map readability) — not covered here.
- Performance/runtime questions (how long a run took, timeout tuning) — not covered here.
- Authoring or fixing a model/workflow so it runs without error — that's the model-authoring skill's job; this skill starts only after a run completes.

## Quick reference: post-run checklist

| # | Check | Tool | Red flag |
|---|-------|------|----------|
| 1 | Outputs exist in the target GDB | `list_gdb_contents` | Expected FC/table absent — log silence does NOT mean success |
| 2 | Feature counts are plausible | `count_features` | A screening step drops ~100% or ~0% of input rows |
| 3 | Required fields exist | `list_fields` | Downstream step's expected field missing or misnamed |
| 4 | CRS matches the analysis | `describe_dataset` | Horizontal CRS differs from what the analysis assumes (a compound/vertical stamp difference alone is cosmetic — see false alarms) |
| 5 | Extent/geometry is sane | `get_current_extent`, `zoom_to_layer` + `capture_map_view` | Extent is empty, at origin (0,0), or in the wrong hemisphere; visual capture shows obviously wrong shapes |

## Procedure: the post-run validation pass

Run all five checks below every time, in order — each one is cheap and catches a different failure mode:

1. **Confirm existence first.** `list_gdb_contents` on the project's default GDB (or the model's explicit output GDB). `mcp-bridge.log` (`AddIn/APBridgeAddIn/ProBridgeService.cs:4067` `LogException`, `:4096` `LogNonSuccess`) only appends on an exception or a `{success:false}` response — both write to `<project home>/mcp-bridge.log`. A clean run leaves **zero** log entries, so the only proof of success is the output actually landing in the GDB.
2. **Sanity-check counts.** `count_features` on every material output, compared against its input(s). A step meant to *screen* candidates that lets through ~100% of input, or one that drops to ~0%, deserves suspicion before you trust it — go find out why (wrong WHERE clause, wrong join cardinality, leftover selection — see State hygiene below).
3. **Confirm the schema the next consumer needs.** `list_fields` on any output another step, another model, or a human deliverable will read by field name. A field silently missing or misnamed here is a "minor missing feature" class of failure — it surfaces hours later as a KeyError or a blank column, not immediately.
4. **Confirm CRS.** `describe_dataset` returns the spatial reference WKID. A different horizontal CRS than the analysis assumes is a real problem (distances/areas will be wrong). A compound/vertical CRS *stamp* difference alone, by itself, is cosmetic (see false alarms below) — don't chase it as if it were the same class of bug.
5. **Visual sanity pass.** `get_current_extent` to confirm the active view's extent isn't degenerate, `zoom_to_layer` on the output, then `capture_map_view` for a quick look. Cheap insurance against a geometry-producing step that ran "successfully" but produced garbage shapes (self-intersections, points at 0,0, a single feature covering the whole world).

## Baseline comparison doctrine

When a hand-built Pro baseline exists, **exact match is the gold standard, and it has been achieved**: the BankSiting driver (10/10 steps, 6 `.pyt` wrappers executed out-of-proc) produced `Candidate_Bank_Sites` = 1,330 features — an exact match to the single-process arcpy baseline (verified 2026-07-10, master at `55d7646`). Treat that as proof the executor CAN reproduce Pro's own engine bit-for-bit when nothing legitimate should differ.

When a run diverges from a baseline, don't default to "bug" or "fine" — **explain it**:

1. The executor materializes every intermediate output in the GDB (that's *why* it can bypass Pro's whole-chain pre-validation — see CLAUDE.md's "`run_model` is special"). Diff step-by-step: walk the model's steps in order, `count_features`/`describe_dataset` each intermediate, and find the FIRST step where counts or geometry diverge from the baseline.
2. At that step, ask whether the divergence has a defensible cause: leftover selection state on one run but not the other, a different tool-environment default (tolerance, output coordinate system), a tool version difference, or an intentional executor behavior (e.g., the per-step in-place-modify copy-on-contention path used for `.pyt` steps under lock contention — the runner reports `kwargs_used` so you can confirm which copy fed the output).
3. If the cause is defensible and the result is still scientifically sound, **document the divergence and move on** — it is not a bug by definition. If you cannot construct a defensible explanation, treat it as a bug and escalate to the model-authoring/executor side.

## Known false alarms — do not chase these

All confirmed non-defects, verified 2026-05-23 by direct `.atbx` round-trip tests against a scratch toolbox (see project memory `latent_authoring_items_resolved.md`):

- **SummarizeWithin enumerated params** (`keep_all_polygons`, `sum_fields`, `sum_shape`, `shape_unit`) appearing to "drop" in Pro's Model Report. The `.atbx` storage is intact and `run_model` executes with the real values (e.g., `sum_Area_SQUAREFEET` field is present when `sum_shape=ADD_SHAPE_SUM` + `shape_unit=FEET`). The Report UI just doesn't render the stored keyword format.
- **`CalculateField` `code_block`** round-trips correctly through `update_model` + `describe_model` + `run_model` — a multi-line Python function computes correctly at run time even if it looked truncated somewhere in a UI view.
- **`Project` auto-stamping a compound CRS** on steps fed by an intermediate. The writer never emits `in_coor_system`; Pro stamps this on load purely for its own canvas display. `run_model` runs the step correctly regardless.
- **ModelBuilder canvas showing stale state** after a surgical bridge write (`set_parameter_default`, `set_step_parameter`, etc.). The `.atbx` on disk is correct; Pro's open canvas tab doesn't auto-reload until the user reopens that model tab (see `pro_canvas_deadlock` memory / CLAUDE.md "Things that bite").

## State hygiene that silently corrupts results

- **Leftover selections restrict layer-name inputs.** After any `select_by_attribute`/`select_by_location`, a subsequent GP step that references the layer by name (not by catalog path) operates ONLY on the selected subset — silently, with no error. Caught live during the 2026-04-23 capability audit (selecting 3 west-coast states, then risking a Buffer/Dissolve on just those 3). Run `clear_selection` before any downstream step that takes a layer name, and use `get_selected_features` to audit current selection state when a count looks wrong.
- **Layer names vs. catalog paths resolve differently.** Prefer concrete `.gdb` paths over in-map layer names for anything feeding a `.pyt` step — cross-process `.pyt` execution has no selection propagation and no `in_memory` dataset access, so a layer-name reference that depended on selection state or an in-memory intermediate will silently behave differently out-of-proc than in-proc.

## Common mistakes

- **Concluding a run "failed" or "hung" because `mcp-bridge.log` had no new lines.** Happened on the 31-step Aurora (Mitigation_Opportunity) model, 2026-05-22: the log only appends on `LogException`/`LogNonSuccess` (`ProBridgeService.cs:4067`/`:4096`), so the completed run left zero entries and looked stuck from the log alone. The fix was checking the target GDB for the expected final outputs, not re-reading the log.
- **Chasing a Pro Model Report UI "dropped parameter" as a bridge/writer bug.** Burned a full investigation session on 2026-05-23 before direct `.atbx` inspection (bypassing the Report UI entirely) confirmed SummarizeWithin's enumerated params, `CalculateField` code blocks, and Project's compound-CRS stamp were all intact on disk and executed correctly — the Report UI simply doesn't render the stored keyword format. Verify against the raw file or `describe_model`, never against the Report UI display alone.
- **Running a downstream GP tool against a layer name right after `select_by_attribute` without clearing the selection.** Caught live in the 2026-04-23 capability audit before it reached a Buffer/Dissolve step — the failure mode is silent (no error, just a feature count restricted to the prior selection), which is what makes it dangerous.
- **Reading a `.pyt` step's first-attempt ERROR 000464 + retry as data corruption.** It's the documented self-heal (child-side copy-on-contention for in-place-modify tools); confirm which copy fed the output via the runner's `kwargs_used`, don't assume the retry silently changed the answer. BankSiting's 1,330-feature exact match happened WITH this retry firing mid-run.

## Honesty note

This skill is **thin** — it captures only what this repo's history has proven about judging GIS result correctness (the checklist above, the BankSiting baseline-match precedent, and the four confirmed false alarms). It is not a GIS methodology course, and it does not cover statistical validity, sampling design, or domain-specific QA standards. When a future session learns a new result-correctness lesson (a new false alarm, a new class of silent corruption, a new baseline-match precedent), **append it here** as one dated bullet rather than editing the sections above:

- (append new dated lessons below this line)
