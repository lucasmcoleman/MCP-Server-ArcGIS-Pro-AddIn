---
name: sharing-the-http-bridge
description: Use when onboarding a second person or team onto the HTTP/Copilot Studio transport, deciding who gets MCP_AUTH_TOKEN, reasoning about select_bridge cross-talk between HTTP callers, planning per-user Pro-instance pinning, sizing Kestrel MaxRequestBodySize vs nginx client_max_body_size, or before re-cutting/cloning the ArcGisMcpServer-HTTP scheduled task deployment for a new machine or user.
---

# Sharing the HTTP Bridge

## Overview

The HTTP/Copilot Studio transport (`--http` / `MCP_TRANSPORT=http`, see README.md "Remote MCP (HTTP transport) for M365 Copilot Studio") was engineered for exactly one caller. Every safety assumption in it — routing, auth, code execution, deployment reproducibility — is single-tenant, and every one of them breaks the moment a second person starts calling the same endpoint. This skill is a pre-onboarding checklist, not a hardening guide.

## When to use

- About to give a second person (or Copilot Studio agent used by a second person) the same `X-Api-Key` / endpoint.
- Someone asks "can we just add another user to the HTTP bridge."
- Debugging why one person's tool calls started hitting the wrong ArcGIS Pro project after another user called `select_bridge`.
- Planning to clone the `ArcGisMcpServer-HTTP` scheduled task setup onto another machine.
- Deciding whether `execute_python` / `bridge_op` should be exposed to a lower-trust caller.

## When NOT to use

- Local stdio work (Claude Code, Claude Desktop spawning the exe as a subprocess) — none of this applies; stdio is inherently one process per user already.
- As a general public-internet hardening guide — it is explicitly NOT that. The design assumes a private LAN, a reverse proxy the owner controls, and callers the owner already trusts. It does not address DDoS, secrets rotation infrastructure, or internet-facing exposure.
- Routine same-user redeploys — see `releasing-and-deploying` for the ordinary build/deploy cycle; this skill is about the *team-adoption* decision, not the mechanics of shipping a build.

## Single-tenant assumption inventory

| Assumption | Location | What breaks at user #2 |
|---|---|---|
| `select_bridge` routing override is a process-global static | `McpServer/ArcGisMcpServer/Ipc/BridgeDiscovery.cs:69-75` (`_runtimeOverride` / `RuntimeOverride`); gate added `BridgeDiscovery.cs:59-63` (`HttpMode`) + `ProTools.cs:1638-1648` (`SelectBridge`) | **Partially closed 2026-07-12.** `select_bridge` now refuses outright on HTTP (`BridgeDiscovery.HttpMode == true`, set by `Program.cs`'s `--http` branch) unless the operator has explicitly set `MCP_HTTP_ALLOW_SELECT_BRIDGE=true` — the underlying static is still process-global (the code comment at `BridgeDiscovery.cs:73-74` still reads "acceptable, HTTP deployments are single-tenant," now stale prose next to live gating code) and opting in re-exposes the exact cross-caller redirect risk described above. This fix is server-side (`McpServer/ArcGisMcpServer`) and is live as soon as the MCP exe is republished — no Add-In redeploy needed. |
| One shared `MCP_AUTH_TOKEN` via `X-Api-Key`, constant-time compare | `Program.cs:61-68` (token check), `Program.cs:107-117` (`CryptographicEquals`) | No per-user identity, no audit trail, no revocation short of rotating the one token for everyone. Anyone who has the key looks identical to the server. |
| `execute_python` and `bridge_op` grant arbitrary code execution in Pro's process | `ProTools.cs:952-968` (`ExecutePython`, description literally: "Execute arbitrary Python code INSIDE ArcGIS Pro's live Python environment"), `ProTools.cs:1527-1530` (`BridgeOp`, raw op dispatch) | Any holder of the shared key can run arbitrary arcpy/Python inside the same Pro session everyone else's deliverables depend on — not sandboxed per caller, on both stdio and HTTP transports. |
| HTTP mode binds plaintext by default | `Program.cs:53-56` (`app.Urls.Add("http://0.0.0.0:5000")`) | Safety rests entirely on an un-versioned Windows Firewall rule (README.md "Windows Firewall" section, `-RemoteAddress` constraint) plus the external reverse proxy's TLS termination — nothing in the repo enforces either. |
| Kestrel's default ~30MB request-body cap vs. the documented nginx config's `client_max_body_size 0` | **Fixed 2026-07-12:** `Program.cs`'s HTTP branch now sets `builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null)` (unlimited), for parity with the nginx side documented at README.md:478 | No longer a gap as of this fix — large payloads (big `execute_python` code blocks, huge GP value tables) no longer 413 at the Kestrel layer. Still zero *test* coverage of the HTTP path specifically (`Test-BridgeLive.ps1` drives the named pipe directly, `tools/Test-BridgeLive.ps1:3`), so this was not exercised against a live HTTP request — verify with a real large payload before relying on it for a big batch call. |
| The `MCP_AUTH_TOKEN` value lives only in a user-scope env var on the host machine | Task/VBS setup captured 2026-07-12 into `docs/http-deployment.md` + `tools/Register-HttpServerTask.ps1` (the deployment IS now reconstructible from a clone) — but the token itself is deliberately not in the repo | A machine rebuild still needs the token re-provisioned by hand before `Register-HttpServerTask.ps1` will run; and the docs/script go stale if the live task is changed without updating them |

## Before user #2: checklist

1. **Pin each user to their own Pro instance.** Set `ARCGIS_PROJECT` per user (env var, strict pin — see `BridgeDiscovery.cs:39-45` comment block). A strict pin makes `select_bridge` refuse to redirect that user's own routing; it does **not** stop that user's `select_bridge` call from clobbering `_runtimeOverride` for everyone else on the same process, because the check is per-request pin resolution, not per-caller state. **As of 2026-07-12, `select_bridge` is gated off by default on HTTP** (`BridgeDiscovery.HttpMode`) unless `MCP_HTTP_ALLOW_SELECT_BRIDGE=true` is explicitly set — so "disable or gate `select_bridge` for the HTTP transport" is now the out-of-the-box default rather than something you have to build yourself. Leave the env var unset for any team deployment; only set it if every HTTP caller is individually trusted with process-wide routing control. This fix is server-side and live as soon as the MCP exe is republished.
2. **Decide the code-execution question consciously, don't inherit it.** A shared team key means every key-holder can run arbitrary arcpy/Python in the shared Pro session (`ExecutePython`, `BridgeOp`). Either gate/remove those two tools for lower-trust callers, or accept that "team access" means "team-wide arcpy root."
3. **Move off one shared token.** Per-user tokens minimum; if that's not feasible yet, at least write down a rotation story (who rotates it, how often, what breaks when you do).
4. **Scheduled-task setup is in the repo (done 2026-07-12).** `docs/http-deployment.md` + `tools/Register-HttpServerTask.ps1` capture the live task; when cloning to a second machine, provision `MCP_AUTH_TOKEN` (user-scope env var) first, then run the register script. Keep both files in sync with any live-task changes.
5. ~~Set Kestrel's `MaxRequestBodySize`~~ — **done 2026-07-12.** `Program.cs`'s HTTP branch now sets `Limits.MaxRequestBodySize = null` (unlimited), matching `client_max_body_size 0` on the proxy side. Nothing left to do here unless you want a real matching cap on both sides instead of unlimited on both.
6. **Treat releases as contracts once the team consumes them.** Today, per the repo owner, releases can be broken and re-cut freely because only one person is affected; that stops being true the moment a second person's deliverables depend on the endpoint being up. See `releasing-and-deploying` for what changes about the release process at that point.

## Brittleness disclosure duty

Nobody being onboarded currently knows how fragile this chain is. Before anyone else starts depending on it, tell them explicitly:

- The whole bridge lives inside one ArcGIS Pro session's state. If Pro crashes, hangs, or the project closes, the bridge is down for everyone using that instance — there is no failover.
- The Python GP lane has a ~3-minute warm-up wedge after Pro launches (`ExecutePython`'s own description, `ProTools.cs:965-967`) and, per CLAUDE.md's "Things that bite," an early Python-touching call can wedge that lane **permanently** for the session — only a Pro restart clears it.
- There is one arcpy session per Pro instance; concurrent callers hitting the same instance share it. `run_model`'s async path tracks jobs in a `ConcurrentDictionary` (`ProBridgeService.cs:2612`) but that's bookkeeping, not a mutex — it does not stop two people from stepping on each other's edits or GP state in the same Pro session.
- "Bridge down" recovery today is a human going to the machine and restarting Pro. There is no remote restart, no auto-recovery, no alerting.

## Common mistakes

- **Assuming `Start-ScheduledTask` succeeding means the HTTP server is actually up.** It doesn't check the process table. CHANGELOG.md:102 documents exactly this: a missing VBS launcher let the scheduled task "start" while the exe never actually launched, silently leaving Copilot Studio pointed at a dead endpoint until someone noticed tool calls failing. Verify with a process check or a live `/mcp` request after any task restart, not by trusting the task's reported state.
- **Assuming a mid-deploy failure leaves the HTTP task in its prior state.** Before the fix in CHANGELOG.md:101, a script failure between "stop the task" and "restart the task" could exit with the HTTP server down and nobody restarting it. `restart-dev-cycle.ps1` now guarantees the restart via `try/finally`, but any new deploy tooling you write for a multi-user setup needs the same guarantee — don't assume happy-path ordering.
- **Treating `select_bridge` as a safe per-caller action because it "worked" in testing with one user.** The underlying override is still a process-global static (`BridgeDiscovery.cs:69-75`) by explicit design. As of 2026-07-12 it's refused by default on HTTP (see item 1 above) — but that default is an env var (`MCP_HTTP_ALLOW_SELECT_BRIDGE`) away from being disabled, and a bridge that predates this fix (Add-In redeploy is irrelevant here — this fix is server-side, so check whether the MCP exe itself has been republished) has no gate at all. It will not surface as a bug in single-user testing; it surfaces as silent cross-talk the first time two people call it against the same server process with the gate off or absent.

## One honest line

Almost none of this is needed while the deployment serves one person — this skill exists for the day adoption grows past that, not for today's reality.
