<#
.SYNOPSIS
    Idempotently (re)registers the ArcGisMcpServer-HTTP scheduled task that
    runs ArcGisMcpServer.exe --http for the Copilot Studio transport.

.DESCRIPTION
    Reconstructs the deployment documented in docs/http-deployment.md:
      Task Scheduler (logon trigger) -> wscript.exe run-mcp-http.vbs (hidden,
      fire-and-forget) -> ArcGisMcpServer.exe --http (long-lived, LAN-bound).

    This script is safe to re-run. It:
      (a) verifies MCP_AUTH_TOKEN is set at User scope and aborts with a
          clear message (without printing the value) if it isn't,
      (b) writes/overwrites the VBS launcher at
          %LOCALAPPDATA%\ArcGisMcpServer\run-mcp-http.vbs to match where the
          live deployment actually keeps it,
      (c) registers/updates the ArcGisMcpServer-HTTP scheduled task with the
          same trigger (logon, current user), principal (interactive, limited
          run level) and settings (restart-on-failure x3 @ 1min, start-when-
          available, unlimited execution time, IgnoreNew for duplicate
          instances) captured from the live task on 2026-07-12 -- see
          docs/http-deployment.md for the full capture.
      (d) never starts the server itself unless -Start is passed. Registering
          a task definition is not the same as running it; this script
          defaults to the safe (no-op) side.
      (e) prints the same process-table verification command
          restart-dev-cycle.ps1 uses, because scheduled-task State is NOT a
          reliable signal here (the VBS wrapper exits immediately after
          spawning the real server -- see docs/http-deployment.md, "The VBS
          launcher").

    What this script does NOT do:
      - It does not generate or set MCP_AUTH_TOKEN. See README.md,
        "Remote MCP (HTTP transport) for M365 Copilot Studio" -> "Server
        side: starting in HTTP mode" for the generation recipe.
      - It does not touch the Windows Firewall rule or the reverse proxy
        (nginx/SWAG) config. See README.md's "Windows Firewall" and
        "nginx (SWAG) example" sections.
      - It does not build ArcGisMcpServer.exe. Run build-mcp-server.ps1 (or
        restart-dev-cycle.ps1) first so McpServer\ArcGisMcpServer\publish\
        has a fresh exe to copy from.

.PARAMETER Start
    After registering/updating the task, start it and verify a live --http
    process appears in the process table. Omit to only register the
    definition without starting anything.

.EXAMPLE
    pwsh ./tools/Register-HttpServerTask.ps1
    Registers/updates the task definition only. Nothing is started.

.EXAMPLE
    pwsh ./tools/Register-HttpServerTask.ps1 -Start
    Registers/updates the task, then starts it and verifies the process.
#>

[CmdletBinding()]
param(
    [switch]$Start
)

$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$taskName    = 'ArcGisMcpServer-HTTP'
$publishExe  = Join-Path $ProjectRoot 'McpServer\ArcGisMcpServer\publish\ArcGisMcpServer.exe'
$httpDir     = Join-Path $ProjectRoot 'McpServer\ArcGisMcpServer\publish-http'
$httpExe     = Join-Path $httpDir 'ArcGisMcpServer.exe'
$vbsDir      = Join-Path $env:LOCALAPPDATA 'ArcGisMcpServer'
$vbsPath     = Join-Path $vbsDir 'run-mcp-http.vbs'

# ─── (a) MCP_AUTH_TOKEN must already be provisioned, user scope ───────────
# The value itself is never read into a variable that gets printed or
# logged below -- only its presence/absence is checked. Generation recipe
# lives in README.md ("Server side: starting in HTTP mode"), not here.
$tokenSet = -not [string]::IsNullOrEmpty(
    [Environment]::GetEnvironmentVariable('MCP_AUTH_TOKEN', 'User')
)
if (-not $tokenSet) {
    Write-Host "MCP_AUTH_TOKEN is not set at User scope for this account." -ForegroundColor Red
    Write-Host "The HTTP server refuses to start without it. Generate and persist one first:" -ForegroundColor Yellow
    Write-Host @'

  $bytes = New-Object byte[] 32
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  $token = [Convert]::ToBase64String($bytes)
  Write-Output $token   # paste into your password manager -- do not commit, do not log
  [Environment]::SetEnvironmentVariable("MCP_AUTH_TOKEN", $token, "User")

'@ -ForegroundColor DarkGray
    Write-Host "Open a NEW shell after setting it (existing shells won't see it), then re-run this script." -ForegroundColor Yellow
    exit 1
}
Write-Host "MCP_AUTH_TOKEN: set (User scope) -- OK" -ForegroundColor Green

# ─── (b) Write/overwrite the VBS launcher ─────────────────────────────────
# Matches the live deployment's location exactly (%LOCALAPPDATA%\ArcGisMcpServer\
# run-mcp-http.vbs) -- see docs/http-deployment.md, "The VBS launcher".
# SW_HIDE (the literal 0 below) keeps no console window visible; the launcher
# exits immediately after spawning (WshShell.Run's 3rd/4th args), which is
# exactly why task State can't be trusted to mean "server is running" --
# see the verification step at the end of this script.
New-Item -ItemType Directory -Force -Path $vbsDir | Out-Null

$vbsContent = @"
' Hidden launcher for ArcGisMcpServer.exe in HTTP mode.
' Used by the $taskName Scheduled Task so the server runs
' without a visible console window. Without this wrapper, .NET console
' apps under Task Scheduler appear as a window the user might close,
' which would terminate the server.
'
' The third argument to WshShell.Run is 0 = SW_HIDE.
Set WshShell = CreateObject("WScript.Shell")
WshShell.Run """$httpExe"" --http", 0, False
"@

Set-Content -LiteralPath $vbsPath -Value $vbsContent -Encoding ASCII
Write-Host "VBS launcher written: $vbsPath" -ForegroundColor Green

if (-not (Test-Path -LiteralPath $httpExe)) {
    Write-Host "NOTE: $httpExe does not exist yet." -ForegroundColor Yellow
    if (Test-Path -LiteralPath $publishExe) {
        Write-Host "Copying from $publishExe now (mirrors restart-dev-cycle.ps1's publish-http sync)..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $httpDir | Out-Null
        Copy-Item -Force -LiteralPath $publishExe -Destination $httpExe
        Write-Host "Copied. $((Get-Item -LiteralPath $httpExe).Length) bytes." -ForegroundColor Green
    } else {
        Write-Host "Neither $httpExe nor $publishExe exist. Run build-mcp-server.ps1 first," -ForegroundColor Red
        Write-Host "then re-run this script (the task will still register, but the launcher will fail until the exe exists)." -ForegroundColor Yellow
    }
}

# ─── (c) Register/update the scheduled task ───────────────────────────────
# Trigger/principal/settings below mirror the live task captured on this
# machine on 2026-07-12 -- see docs/http-deployment.md for the full capture
# this was derived from.
$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "`"$vbsPath`""

$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"

$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Limited

# Note: DisallowStartIfOnBatteries / StopIfGoingOnBatteries are ON by
# DEFAULT for New-ScheduledTaskSettingsSet (there is no such parameter to
# pass -- they're toggled OFF via -AllowStartIfOnBatteries /
# -DontStopIfGoingOnBatteries, neither of which is used here), so omitting
# them below reproduces the live task's captured values (both true).
$settings = New-ScheduledTaskSettingsSet `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'ArcGIS MCP Server in HTTP mode for M365 Copilot Studio' `
    -Force | Out-Null

Write-Host "Scheduled task '$taskName' registered/updated." -ForegroundColor Green

# ─── (d) Start nothing unless -Start was passed ───────────────────────────
if ($Start) {
    Write-Host "`n-Start passed: starting the task..." -ForegroundColor Cyan
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 2

    # ─── (e) Verify via process table, not task State ─────────────────────
    # Task State reports "Ready" almost immediately because wscript.exe exits
    # right after spawning the real server -- see docs/http-deployment.md.
    $verifyProc = Get-CimInstance Win32_Process -Filter "Name = 'ArcGisMcpServer.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match '--http' } |
        Select-Object -First 1

    if ($verifyProc) {
        Write-Host "HTTP server up: PID $($verifyProc.ProcessId)" -ForegroundColor Green
    } else {
        Write-Host "WARNING: task started but no --http server process was found." -ForegroundColor Yellow
        Write-Host "Check Event Viewer > Applications and Services Logs > Microsoft > Windows > TaskScheduler." -ForegroundColor DarkGray
    }
} else {
    Write-Host "`nTask registered but NOT started (pass -Start to start it now)." -ForegroundColor DarkGray
}

Write-Host "`nVerify at any time with:" -ForegroundColor Cyan
Write-Host @'
  Get-CimInstance Win32_Process -Filter "Name = 'ArcGisMcpServer.exe'" |
      Where-Object { $_.CommandLine -and $_.CommandLine -match '--http' }
'@ -ForegroundColor DarkGray
Write-Host "A matching row with a ProcessId means the server is live. No row means the task" -ForegroundColor DarkGray
Write-Host "hasn't fired yet, crashed, or the VBS launcher is missing/broken." -ForegroundColor DarkGray
