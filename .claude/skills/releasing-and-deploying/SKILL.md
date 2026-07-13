---
name: releasing-and-deploying
description: Use when publishing a new build to teammates, cutting a GitHub release, deciding which build script a code change needs, a release sits stuck as an unpublished Draft, "Latest" on GitHub points at the wrong/oldest tag, SHA256SUMS.txt is missing the .esriAddinX, the build-addin CI job shows "skipped", or CHANGELOG.md looks stale versus git log.
---

# Releasing and Deploying

## Overview
This repo has two independently-built artifacts (MCP server exe, Pro Add-In bundle) and the release pipeline that ships them has **never produced a complete release on its own** — CI builds only the exe, the Add-In build and the final "publish" click are both manual steps a human must remember. They have been forgotten before. Treat every release as a checklist, not a button push.

## When to use
- You changed code and need to get it running locally (dev-cycle question: which script?).
- You're about to `git tag` and cut a GitHub release.
- `gh release list` shows "Latest" on an old/wrong tag, or a tag sits as `Draft` for a long time.
- SHA256SUMS.txt on a release only lists one file, or is missing the `.esriAddinX`.
- Someone asks "can I just download this off GitHub" and you need to check what's actually published vs. what's just tagged.
- You're deciding whether to register a self-hosted Pro runner or delete the `build-addin` CI job.

## When NOT to use
- Don't run `restart-dev-cycle.ps1` for a server-only change — it hard-requires ArcGIS Pro to be closed and does Add-In work you don't need. Use `build-mcp-server.ps1` alone.
- Don't treat `McpServer/ArcGisMcpServer/Dockerfile` as the real deployment path — it's MCP-half-only and best-effort; named pipes to the Pro Add-In don't cross the container boundary (see the Dockerfile's own header comment). Fine for packaging/experiments, not for shipping the bridge.
- Don't resurrect or merge `origin/claude/generate-addin-mcp-release-Ey5vN` — see Entropy below, it's dead.
- Don't skip the ritual because "it's just me" — see Context below; that grace period is not permanent.

## Dev-cycle decision table

| Change touches | Run | Precondition | Verify |
|---|---|---|---|
| MCP server only (`McpServer/**`) | `pwsh ./build-mcp-server.ps1` | Exit any MCP client (Claude Code, etc.) holding `ArcGisMcpServer.exe` — the script refuses to publish while the process is running, by design (see script header) | New `publish/ArcGisMcpServer.exe` timestamp |
| Add-In only (`AddIn/**`) | `pwsh ./build-addin.ps1` (builds via vswhere-located MSBuild, wipes AssemblyCache, deploys — MCP exe untouched, Claude Code sessions stay open; `-BuildOnly` for a compile check with Pro still running) | ArcGIS Pro closed for the deploy step only | Reopen Pro, confirm Add-In loads |
| Both halves | `pwsh ./restart-dev-cycle.ps1` | Pro closed, Claude Code closed | Script prints "Ready. Reopen Claude Code first, then ArcGIS Pro." |

Add-In build command (VS MSBuild only — `dotnet build` fails, the Pro SDK targets file uses `CodeTaskFactory`):
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  AddIn/APBridgeAddIn/APBridgeAddIn.csproj -p:Configuration=Release
```
Output: `AddIn/APBridgeAddIn/bin/Release/net8.0-windows8.0/APBridgeAddIn.esriAddinX`. Copy it to:
```
C:\Users\<you>\Documents\ArcGIS\AddIns\ArcGISPro\{c56ccfd4-f12a-4916-84c2-64248b3d746c}\APBridgeAddIn.esriAddinX
```
(GUID is the stable `AddInInfo id` in `Config.daml`.)

`restart-dev-cycle.ps1` additionally: verifies Pro is closed, kills any `--http`-flagged `ArcGisMcpServer.exe` process, rebuilds the Add-In via `vswhere`-located MSBuild, **wipes** `%LOCALAPPDATA%\ESRI\ArcGISPro\AssemblyCache\{c56ccfd4-...}` (Pro caches extracted DLLs there and won't re-extract on identical-mtime input), deploys the bundle, republishes the exe, mirrors it to `publish-http/`, and restarts the `ArcGisMcpServer-HTTP` scheduled task if it was already registered — all wrapped so a mid-script failure still leaves the HTTP task running.

## The release ritual

CI (`.github/workflows/release.yml`, triggered on `v*` tag push or manual `workflow_dispatch`) builds and uploads **only `ArcGisMcpServer.exe`**. Its `build-addin` job is gated `if: vars.HAS_ARCGIS_RUNNER == 'true'` on a `runs-on: [self-hosted, windows, arcgis-pro]` runner — that runner has never been registered (`gh api .../actions/variables` returns zero variables) and the job has shown `conclusion: "skipped"` completing in under a second on all 3 historical runs. Do not expect it to produce the Add-In.

Full checklist — every step is manual until noted otherwise:

1. **Tag and push.**
   ```
   git tag vX.Y.Z && git push origin vX.Y.Z
   ```
   CI kicks off automatically and creates a **Draft** release with the exe attached (`draft: true` in the workflow's `softprops/action-gh-release` step — this is deliberate, not a bug).
2. **Build the Add-In locally** with the MSBuild command above (CI will not do this for you).
3. **Upload it to the release CI already created:**
   ```
   gh release upload vX.Y.Z <path-to-.esriAddinX>
   ```
4. **Regenerate `SHA256SUMS.txt` covering BOTH artifacts and upload it** — the CI-generated one only hashes the exe (see the workflow's "Stage release assets" step, which only ever sees the exe artifact locally when the Add-In job skips) and will otherwise be silently short.
   ```powershell
   Get-FileHash ArcGisMcpServer.exe, APBridgeAddIn.esriAddinX -Algorithm SHA256 |
     ForEach-Object { "$($_.Hash.ToLower())  $($_.Path | Split-Path -Leaf)" } | Out-File SHA256SUMS.txt -Encoding ascii
   gh release upload vX.Y.Z SHA256SUMS.txt --clobber
   ```
5. **Publish it: `gh release edit vX.Y.Z --draft=false`.** THIS IS THE STEP THAT WAS MISSED TWICE — v0.2.0 and v0.3.0 sat as Drafts for two months while `gh release list` reported v0.1.0 as "Latest" simply because it was the only non-draft release, even though it was chronologically the oldest tag.
6. **Verify:**
   ```
   gh release view vX.Y.Z --json isDraft,assets
   ```
   Confirm `isDraft: false` and exactly 3 assets (`ArcGisMcpServer.exe`, `APBridgeAddIn.esriAddinX`, `SHA256SUMS.txt`).

## Current entropy state (updated 2026-07-12)

Done on 2026-07-12 — do not redo:
- ~~Publish the stuck drafts~~ — `v0.2.0` and `v0.3.0` were published (they had sat as Drafts for two months); `v0.3.0` now correctly holds `Latest`.
- ~~Backfill CHANGELOG.md~~ — backfilled through 2026-07-10 (five campaign sections). **Keep it current**: every future release cut should land its CHANGELOG section in the same commit series.

- ~~Delete `origin/claude/generate-addin-mcp-release-Ey5vN`~~ — deleted 2026-07-12; its two stray commits (an obsolete `-SelfContained` build switch) are preserved under the local tag `archive/claude-selfcontained-branch` should anyone ever need them.

Still open:
- **Master is 53+ commits past `v0.3.0`** (as of 2026-07-12) — a `v0.4.0` cut is overdue.
- **Decide the `build-addin` job's fate**: register a real ArcGIS-Pro-licensed self-hosted runner labeled `arcgis-pro` and set the `HAS_ARCGIS_RUNNER` repo variable to `true`, or delete the job as aspirational dead weight that has skipped on every run since it was added.

## Context: who this affects

Today releases are consumed by nobody but the repo owner — cut and re-cut freely, break things, it's fine. The stated goal is the whole GIS team adopting this, and "numerous deliverables routinely depend on this" already describes internal usage even before public releases exist. The day a teammate downloads a tagged release and builds a deliverable on it, every subsequent release becomes a contract (don't silently reshape asset names, don't retag, don't force-push over a tag). Tighten the ritual's rigor accordingly as adoption grows — nobody outside the owner currently appreciates how brittle the Add-In/exe/pipe coupling is.

## HTTP deployment — now documented in-repo

As of 2026-07-12 the `ArcGisMcpServer-HTTP` scheduled task is reconstructible from the repo: `docs/http-deployment.md` documents the captured live task (trigger, principal, settings, the VBS launcher at `%LOCALAPPDATA%\ArcGisMcpServer\run-mcp-http.vbs`, and why the wrapper exists — it exits immediately after spawning, which is why `restart-dev-cycle.ps1` inspects `Win32_Process` command lines instead of trusting task `State`), and `tools/Register-HttpServerTask.ps1` recreates it idempotently (aborts if `MCP_AUTH_TOKEN` isn't set user-scope; never embeds the token). **Keep both in sync** whenever the task, VBS, or token handling changes — the token value itself lives only in the user-scope env var on the host machine, never in the repo.

## Common mistakes

- Running `restart-dev-cycle.ps1` for a docs-only or server-only change, then getting blocked because Pro is open — the script has no fast path, it always demands Pro closed.
- Using `dotnet build` on the Add-In project and getting a confusing `CodeTaskFactory`/targets error instead of realizing MSBuild is required.
- Assuming the tagged release is live because CI ran green — CI green only means the exe uploaded to a Draft; nobody flipped `--draft=false`. This is exactly how v0.2.0 and v0.3.0 went unpublished for two months.
- Trusting the CI-generated `SHA256SUMS.txt` as covering both artifacts — it's generated before the Add-In (built out-of-band) exists in the CI workspace, so it only ever hashes the exe.
