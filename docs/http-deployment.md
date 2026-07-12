# HTTP / Copilot Studio Deployment

This document captures the **actual, currently-deployed** Windows Scheduled Task
that runs `ArcGisMcpServer.exe --http` on this machine, so the deployment is
reconstructible from a clone instead of living only in one person's memory (see
`sharing-the-http-bridge` skill, "Single-tenant assumption inventory" — this was
the one row with no version-controlled record).

This is a deployment/ops document. For the transport's design, auth model,
firewall rule, nginx/SWAG config, and Copilot Studio wizard steps, see
**README.md → "Remote MCP (HTTP transport) for M365 Copilot Studio"**. This
document does not repeat that material — it captures only what's specific to
*this machine's* running instance: the scheduled task, the VBS launcher, and
the rebuild recipe.

## Captured from the live system on 2026-07-12

Captured read-only via `Get-ScheduledTask` / `Export-ScheduledTask` and by
reading the VBS launcher file directly. Nothing below is fabricated or
inferred — see the "If the task doesn't exist" section at the bottom for what
a reconstructed-not-captured doc would look like instead.

## End-to-end architecture

```
M365 Copilot Studio (cloud)
        |  HTTPS + X-Api-Key header
        v
yoursite.example.com                  <- reverse proxy (nginx/SWAG), TLS termination
        |  HTTP (LAN only)
        v
<this-machine-ip>:5000                <- ArcGisMcpServer.exe --http, bound 0.0.0.0:5000
        ^
        |  Windows Firewall inbound rule scoped to the proxy host's IP only
        |
   Task Scheduler: "ArcGisMcpServer-HTTP"  (LogonTrigger, runs at user logon)
        |  spawns
        v
   wscript.exe run-mcp-http.vbs         <- hidden-window wrapper, exits immediately
        |  spawns (fire-and-forget, SW_HIDE)
        v
   ArcGisMcpServer.exe --http           <- the actual long-lived server process
        |  named pipe (ArcGisProBridge_<PID>)
        v
   ArcGIS Pro + APBridgeAddIn
```

The Windows Firewall rule and the nginx/SWAG reverse-proxy config are
machine/network-specific and are **not** re-documented here — follow
README.md's "Windows Firewall" and "nginx (SWAG) example" sections verbatim;
they are not duplicated in this file.

## The actual task definition (this machine)

Queried via `Get-ScheduledTask -TaskName ArcGisMcpServer-HTTP` and
`Export-ScheduledTask` (both read-only; the live task was never modified or
restarted to produce this document).

| Property | Value |
|---|---|
| Task name / path | `\ArcGisMcpServer-HTTP` |
| Description | "ArcGIS MCP Server in HTTP mode for M365 Copilot Studio" |
| Action | `wscript.exe "C:\Users\Lucas\AppData\Local\ArcGisMcpServer\run-mcp-http.vbs"` |
| Trigger | `LogonTrigger`, `UserId = LUCAS-PC\Lucas` (runs at that user's interactive logon) |
| Principal | `LogonType = InteractiveToken`, `RunLevel = Limited` (runs as the logged-on user, not elevated, not a service account) |
| Multiple instances policy | `IgnoreNew` — a second logon trigger firing while one instance is already running is a no-op |
| Restart on failure | 3 attempts, 1-minute interval |
| Disallow on battery / stop on battery | both `true` (desktop-appropriate defaults; irrelevant on a desktop but present) |
| Start when available | `true` — if the trigger was missed (machine was off), Task Scheduler runs it at next opportunity |
| Execution time limit | `PT0S` (unlimited — the server is meant to run indefinitely) |

Full exported XML is in the session scratchpad, **not committed to the repo**:
`.../scratchpad/http-task.xml`. It contains no secrets — only a `Principal`
SID and a `LUCAS-PC\Lucas` username — but it's a point-in-time export of one
machine's task, not a template, so it stays out of version control.

## The VBS launcher

Full contents of `C:\Users\Lucas\AppData\Local\ArcGisMcpServer\run-mcp-http.vbs`
(read directly from disk; nothing redacted — it contains no token, no
credential, just a hardcoded path and a shell verb):

```vbscript
' Hidden launcher for ArcGisMcpServer.exe in HTTP mode.
' Used by the ArcGisMcpServer-HTTP Scheduled Task so the server runs
' without a visible console window. Without this wrapper, .NET console
' apps under Task Scheduler appear as a window the user might close,
' which would terminate the server.
'
' The third argument to WshShell.Run is 0 = SW_HIDE.
Set WshShell = CreateObject("WScript.Shell")
WshShell.Run """C:\Users\Lucas\Documents\Programming\MCP-Server-ArcGIS-Pro-AddIn\McpServer\ArcGisMcpServer\publish-http\ArcGisMcpServer.exe"" --http", 0, False
```

**Why a VBS wrapper exists at all:** Task Scheduler launching a .NET console
app directly gives it a visible console window under the logged-on user's
session; a user who sees a random console window and closes it kills the
server. `WshShell.Run(..., 0, False)` launches with `SW_HIDE` (no window) and
the `False` for the "wait for completion" argument means `wscript.exe` doesn't
block — it spawns `ArcGisMcpServer.exe` and **exits immediately**.

**This is exactly why `restart-dev-cycle.ps1` never trusts scheduled-task
`State`.** Once `wscript.exe` exits, Task Scheduler's `Get-ScheduledTask`
reports `State = Ready` (not `Running`) even while the actual HTTP server
process is alive and serving requests — the task's own lifecycle bookkeeping
tracks `wscript.exe`, not the process it detached. `restart-dev-cycle.ps1`
instead inspects `Win32_Process` command lines for `ArcGisMcpServer.exe`
processes whose `CommandLine` matches `--http`, both to find/kill the running
server before a rebuild and to verify a fresh `Start-ScheduledTask` actually
produced a live process afterward (see that script's step 2 and step 9
comments, and CHANGELOG.md's entry on the missing-VBS failure mode this
verification was added to catch).

## Token provisioning

`MCP_AUTH_TOKEN` is **not** embedded in the VBS launcher, the task XML, or any
file on disk that this document reads from — it is a **user-scope Windows
environment variable**, inherited by any process (including `wscript.exe` and
its child) launched under that user's logon session. This was verified
read-only via:

```powershell
[Environment]::GetEnvironmentVariable('MCP_AUTH_TOKEN', 'User')
```

Result on this machine: **set, non-empty** (confirmed by checking length only
— the value itself was never printed or logged, and does not appear anywhere
in this file or in `tools/Register-HttpServerTask.ps1`).

Generation/provisioning recipe (from README.md, "Server side: starting in
HTTP mode" — reproduced here only because it's the exact recipe
`Register-HttpServerTask.ps1` checks for, not a duplicate of the surrounding
HTTP-transport design doc):

```powershell
# Generate a strong token once, save in your password manager
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$token = [Convert]::ToBase64String($bytes)
Write-Output $token   # paste into password manager — do not commit, do not log

# Persist for future logons/processes (user scope, not machine scope)
[Environment]::SetEnvironmentVariable("MCP_AUTH_TOKEN", $token, "User")
```

`Program.cs` refuses to start the HTTP listener at all if `MCP_AUTH_TOKEN` is
unset — there is no silent-unauthenticated-mode fallback.

## Rebuild-from-nothing sequence (new machine, or reinstalling this one)

1. Clone the repo; confirm ArcGIS Pro 3.6+ and the Add-In are already set up
   per the main README (this doc assumes the bridge itself already works over
   stdio before adding the HTTP transport on top).
2. Generate and persist `MCP_AUTH_TOKEN` at **User** scope (recipe above).
   Open a **new** shell/logon afterward — existing shells don't see a
   just-set user env var.
3. Build the MCP server: `pwsh ./build-mcp-server.ps1` (produces
   `McpServer/ArcGisMcpServer/publish/ArcGisMcpServer.exe`).
4. Run `pwsh ./tools/Register-HttpServerTask.ps1` (this lane's script). It
   will:
   - abort with a clear message if `MCP_AUTH_TOKEN` isn't set (step 2 above),
   - write the VBS launcher at `%LOCALAPPDATA%\ArcGisMcpServer\run-mcp-http.vbs`,
     pointing at `publish-http\ArcGisMcpServer.exe --http`,
   - copy the built exe from `publish\` into `publish-http\` (mirroring what
     `restart-dev-cycle.ps1` does on every redeploy),
   - register the `ArcGisMcpServer-HTTP` scheduled task with the same
     trigger/principal/settings captured above,
   - **not start anything** unless `-Start` is passed.
5. Set up the Windows Firewall rule and reverse proxy per README.md's
   "Windows Firewall" and "nginx (SWAG) example" sections — those are
   network-specific and not scripted here.
6. Start the task (`Start-ScheduledTask -TaskName ArcGisMcpServer-HTTP`, or
   `Register-HttpServerTask.ps1 -Start`) and verify with the process-table
   check below — never trust task `State`.
7. Run the README's `curl` smoke test against the public HTTPS endpoint, then
   wire up Copilot Studio per README's "Copilot Studio wizard" section.

## Verifying the server is actually running

Matches `restart-dev-cycle.ps1`'s own verification — task `State` is not
reliable (see "The VBS launcher" above), so check the process table for a
live `--http` process instead:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'ArcGisMcpServer.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine -match '--http' }
```

A matching row with a `ProcessId` means the server is up. No row means the
task either hasn't fired yet, crashed on launch, or the VBS launcher is
missing/broken — check Event Viewer under
"Applications and Services Logs > Microsoft > Windows > TaskScheduler" for
the failure.

## If the live task does not exist on the machine you're reading this from

Everything above (task properties, VBS content, token check) was captured
from a machine where the task **does** exist and is registered. If you're
reconstructing this on a machine where `Get-ScheduledTask -TaskName
ArcGisMcpServer-HTTP` returns nothing, you are not looking at a captured
deployment — follow the "Rebuild-from-nothing sequence" above, which derives
entirely from `restart-dev-cycle.ps1`'s documented behavior and README.md's
HTTP section rather than from a live capture. `Register-HttpServerTask.ps1`
works identically either way (that's the point of it being idempotent), but
be aware the task *properties* it registers (trigger type, restart policy)
are this machine's captured values, presented as a reasonable default, not
verified against a second live instance.
