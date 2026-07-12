---
name: authoring-atbx-models
description: Use when creating or editing a ModelBuilder .atbx model via create_model, update_model, set_parameter_default, or set_step_parameter — including wiring a new gpTool/scriptTool/nestedModel/pythonScriptTool step, hitting ERROR 000735 "Value is required", every parameter becoming Required after an edit, a step ref resolving to the wrong value, or deciding whether update_model is safe on a Pro-authored model.
---

# Authoring ATBX Models

## Overview

The bridge's `.atbx` writer takes a simplified JSON model definition and round-trips it into ModelBuilder's real file format — but it will faithfully write whatever shape you give it, wrong slot names and duplicate names included, and only arcpy at run time will tell you it's wrong. The core principle: **never guess a tool's parameter slots and run to find out** — verify every layer (signature, definition, round-trip) before advancing to the next.

## When to use

- Building a new model from scratch, or adding/rewiring a step in an existing one.
- `run_model`/`run_gp_tool` fails with ERROR 000735 ("Value is required") on a param that has a default in the .atbx.
- A `describe_model` → edit → `update_model` cycle made every parameter Required, or reordered/dropped one from the interface.
- A step's output is referenced downstream but resolves empty or to the wrong value.
- Deciding whether a hand-authored (Pro) model is safe to push through `update_model`.

## When NOT to use

- A single GP call with no model involved — just call `run_gp_tool` directly (still verify its signature with `describe_gp_tool` first).
- The model contains (or needs) an **iterator** step — the step-by-step executor rejects `Iterator`/`Unknown` kinds outright (`ProBridgeService.cs:2794-2806`); build/run that model in Pro's own ribbon instead.
- The run itself is failing/hanging/timing out on a model that's already correctly authored — that's **debugging-model-runs**, not this skill.
- Judging whether a completed run's output is scientifically correct — that's **validating-gis-outputs**.

## Quick reference

| Situation | Do this |
|---|---|
| Don't know a tool's exact parameter slots | `describe_gp_tool("alias.ToolName")` — returns positional order, in/out, optional, defaults, domains |
| Tool isn't in the hand-pinned catalog | It still resolves — `GpToolCatalog.ResolveSignature`/`ResolveOutputSlot` fall back to `SystemToolboxCatalog` (~1700 parsed system tools); only custom script tools/unlicensed extensions return null |
| Missing/optional param value | Use `"#"`, never `""` — see warning box below |
| Two parameters or step outputs share a name | Rename one — `nameToId` has no duplicate guard, the second write silently wins every ref |
| Editing a `describe_model` result before `update_model` | Preserve `optional`, `exposed`, `parameterOrder` verbatim |
| Non-canonical output key (e.g. `out_features` on CalculateGeometryAttributes) | Leave it; the writer canonicalizes via `GpToolCatalog.OutputSlots` |
| Model has Model Properties-level default environments | Don't `update_model` — use `set_parameter_default`/`set_step_parameter` |
| Literal expression needs a variable's value | `%VarName%` or `%Display Name%` both substitute (space/underscore-normalized match) |
| A step is grayed out (not-ready) in ModelBuilder | It still runs best-effort (`valid=false` semantics mirrored, `ProBridgeService.cs:3523`) |
| Need to override an input at run time | Pass `variableOverrides` (by name) to `run_model`/`start_run_model` |

## The authoring loop

1. **`describe_gp_tool` every tool you will wire** — for each `alias.ToolName` in your plan, get the exact positional slot names and required/optional flags. Do this before writing a single step; do not infer from a similar tool you remember.
2. **Confirm signature coverage.** `GpToolCatalog.Signatures`/`OutputSlots` (`GpToolCatalog.cs`) are hand-pinned and win on conflict; anything not pinned falls through to `SystemToolboxCatalog`, which parses ~1700 installed system tools at runtime. A tool returns null from both only for custom script tools or unlicensed extensions — if `describe_gp_tool` comes back empty, that's your signal something is actually unsupported, not a guess.
3. **Author the definition** using the slot names from step 1, applying the rules below.
4. **`create_model`** (new) or **`update_model`** (existing) — but check the data-loss warning below first if the target is Pro-authored.
5. **`describe_model` and diff the round-trip against your intent** before running anything. Check parameter order, optional flags, and every ref resolves to the id you expect. This is the step agents skip and then debug blind — don't skip it.
6. **Run on scratch inputs first.** Pattern: `create_toolbox` → `create_model` with the step shapes you're testing → `describe_model` to verify the round-trip → `run_model` to verify execution → delete the scratch `.atbx` file when done (no bridge tool deletes it; remove the file directly).
7. **Only then run against real data.**

## Definition rules

- **Every parameter name and every step output name must be unique across the whole model.** `GenerateModelFiles`' `nameToId` map is a plain dictionary indexer with no duplicate guard (`AtbxManager.cs:1286` for inputs, `:1485` for step outputs) — a second write with the same name silently overwrites the first, and every ref to that name from anywhere in the model resolves to the wrong (last) id. There is no error; the model just wires wrong.
- **Preserve `optional`, `exposed`, and `parameterOrder` verbatim** on a describe → edit → update round-trip. `tool.content`'s `params` key order is Pro's authoritative dialog/arcpy calling order; `optional: true` is the only signal that a param isn't Required. Dropping either field on `update_model` makes every parameter Required and re-derives order from scratch — the resulting cascade is ERROR 000735 at run time, not a validation error at write time.
- **Never promote an `"exposed": false` variable into the interface.** That flag marks a Pro-authored stray (has the Parameter flag in `tool.model` but no `tool.content` entry) — round-trip the variable but don't manufacture a public parameter for it.
- **Use canonical output slot keys**, or don't worry about it — the writer canonicalizes non-canonical keys itself (e.g. `out_features` → `updated_features` on `management.CalculateGeometryAttributes`, `GpToolCatalog.cs:109-173`) and coerces `GPComposite` declarations to the concrete `DE*` type Pro requires.
- **`%VarName%` substitution** applies inside literal expressions (e.g. `CalculateField` code) and matches either the variable's underscored `param_name` or its space-containing display label — `%Output Workspace%` and `%Output_Workspace%` both resolve to the same variable.
- **`valid=false` (not-ready) steps still run**, best-effort, mirroring ModelBuilder's own semantics — don't assume a grayed-out step is inert.
- **`run_model`/`start_run_model` accept `variableOverrides`** keyed by variable name, for supplying/overriding inputs without editing the `.atbx`.

## Warning: the "#" sentinel, not ""

arcpy's convention for "this parameter is unsupplied, use the tool's declared default" is the literal string `"#"`. An empty string `""` is NOT equivalent — arcpy reads it as an explicit empty value, which produces ERROR 000735 ("Value is required") for a required parameter and silently overrides a declared default for an optional one.

History: on 2026-04-27, a 15-commit rabbit hole (all within 38 minutes) misdiagnosed this symptom as Field-type parameters "losing ModelBuilder parameter wiring" — first Field-type/name detection, then an `ExecuteModelViaArcpy` fallback that spawned `propy.bat` to call a nonexistent `arcpy.ImportTool()` API, then seven commits of error-string-matching thrash and four of debug logging. All 15 commits were erased with a bare `git reset` to `5ad7858` (recoverable only via `git reflog`, not `git log`). The actual fix, landed ~2.5 hours later at `da94901`, was a 14-line, one-file change at the sentinel layer: send `"#"` for any parameter slot the caller didn't supply, instead of `""` (`ProBridgeService.cs:3444` mirrors the same distinction for unresolved refs — `"#"` for an unsupplied model input, `""` for an intermediate a producer step should have populated but didn't).

**Rule: when a parameter value misbehaves, check the sentinel/encoding layer first.** Do not reach for type-based special-casing (Field-type detection, error-string matching) before ruling out the trivial "#" vs "" distinction.

## Data-loss warning: update_model on Pro-authored models

`update_model` (via `GenerateModelFiles`) regenerates `tool.model` and `tool.content` **only** from the fields the simplified schema defines: `tool.model` gets `version`/`updated`/`variables`/`processes`; `tool.content` gets `type`/`displayname`/`description`/`app_ver`/`product`/`updated`/`params`. Anything Pro stores outside that shape — most importantly **model-level default environments** set via the Model Properties dialog — is silently dropped, with no warning.

Before running `update_model` on a model someone authored in Pro (not one the bridge created), check whether it has model-level environments set. If it does, don't do a wholesale `update_model` — use the surgical primitives instead: `set_parameter_default` and `set_step_parameter`. Both route through `WriteAtbxAtomically` and mutate the existing file in place rather than regenerating it, so they don't touch anything the schema doesn't know about.

## Step kinds

| Kind | Executed how | Notes |
|---|---|---|
| `gpTool` | In-proc `ExecuteToolAsync` | Native GP tool, the common case |
| `scriptTool` | In-proc, dispatched by qualified toolbox path | Signature comes from the target's own `tool.content` |
| `nestedModel` | Recursed through the same step-by-step engine (`.atbx`) or path-dispatched (`.tbx`, can't be parsed) | Cycle + depth-8 guards |
| `pythonScriptTool` | Out-of-proc via `propy.bat` child process | `.pyt`-hosted; see **debugging-model-runs** for cross-process caveats (no selection propagation, no in_memory datasets, timeouts) |
| `iterator` | **Rejected** | Executor has no step-by-step semantics for it; run via Pro's ribbon |

## Canvas caveat

Bridge writes are canvas-safe: `WriteAtbxAtomically` mutates an in-memory copy and does an atomic `File.Replace`, which works even while Pro has the `.atbx` open for read in a ModelBuilder canvas tab. But Pro does not auto-reload — **the open canvas keeps showing the pre-write state until the user closes and reopens that model tab.** The file on disk is correct immediately; tell the user to reopen the tab rather than re-editing through the bridge to "fix" what looks unchanged. (If Pro is holding a stronger lock than a plain open tab — e.g. mid-edit — `File.Replace` itself throws, with a message telling the user to close all ModelBuilder canvas tabs and retry.)

## Common mistakes

- Wiring a step from memory of a similar tool instead of calling `describe_gp_tool` first — this is exactly the "throw stuff at the wall" failure mode the executor's slot-mismatch bugs come from (e.g. `false` from `preserve_shape` landing in `transform_method` on `management.Project` before that tool got a pinned signature).
- Reusing a parameter or output name across steps because it "seemed fine" — there's no duplicate guard; the failure is silent misrouting, not an error.
- Editing a `describe_model` JSON blob by hand and dropping `optional`/`exposed`/`parameterOrder` because they "look like metadata" — this is the exact bug pattern that shipped before those fields existed (all-required, reordered, or stray-promoted interfaces).
- Running `update_model` on a Pro-authored model without checking for model-level environments first, then wondering why a downstream tool that depended on an inherited environment starts failing.
- Passing `""` for an unspecified parameter instead of `"#"` — see the sentinel warning above; this was a real 15-commit detour, not a hypothetical.
- Treating a `describe_model` step as optional and running straight after `create_model`/`update_model` — the round-trip diff is what turns a guess into a verified fact before arcpy ever runs.
