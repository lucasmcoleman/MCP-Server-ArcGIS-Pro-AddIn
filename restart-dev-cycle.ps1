# restart-dev-cycle.ps1
#
# One-shot helper for the close-restart cycle after MCP server + Add-In
# changes. Performs:
#   1. Verifies ArcGIS Pro is closed (so Pro re-loads the new Add-In).
#   2. Stops the ArcGisMcpServer-HTTP scheduled task if it's running, so
#      its exe in publish-http/ is unlocked for the sync at the end.
#   3. Verifies no other ArcGisMcpServer.exe is running (held by Claude Code).
#   4. Locates MSBuild via vswhere and rebuilds the Pro Add-In to produce
#      a fresh .esriAddinX bundle.
#   5. Wipes the per-user AssemblyCache for this Add-In (so Pro re-extracts
#      the freshly-built bundle instead of using a cached DLL).
#   6. Deploys the freshly-built .esriAddinX into the AddIns folder.
#   7. Invokes build-mcp-server.ps1 to publish a fresh MCP server exe in publish/.
#   8. Copies the fresh exe to publish-http/ so the HTTP server (Copilot
#      Studio path) runs the same code as the stdio server.
#   9. Restarts the ArcGisMcpServer-HTTP scheduled task if it was running.
#
# Run this from the project root (same folder as build-mcp-server.ps1)
# right after closing Pro and Claude Code, BEFORE reopening them.

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

$addInGuid     = '{c56ccfd4-f12a-4916-84c2-64248b3d746c}'
$addInProj     = Join-Path $ProjectRoot 'AddIn\APBridgeAddIn\APBridgeAddIn.csproj'
$addInBundle   = Join-Path $ProjectRoot 'AddIn\APBridgeAddIn\bin\Release\net8.0-windows8.0\APBridgeAddIn.esriAddinX'
$addInsDir     = Join-Path $env:USERPROFILE "Documents\ArcGIS\AddIns\ArcGISPro\$addInGuid"
$assemblyCache = Join-Path $env:LOCALAPPDATA "ESRI\ArcGISPro\AssemblyCache\$addInGuid"
$httpTaskName  = 'ArcGisMcpServer-HTTP'
$httpDir       = Join-Path $ProjectRoot 'McpServer\ArcGisMcpServer\publish-http'
$httpExe       = Join-Path $httpDir 'ArcGisMcpServer.exe'
$publishedExe  = Join-Path $ProjectRoot 'McpServer\ArcGisMcpServer\publish\ArcGisMcpServer.exe'

# ─── 1. Pro must be closed ───────────────────────────────────────────────
$proRunning = Get-Process ArcGISPro -ErrorAction SilentlyContinue
if ($proRunning) {
    Write-Host "ArcGIS Pro is still running:" -ForegroundColor Red
    $proRunning | Format-Table Id, ProcessName, StartTime
    Write-Host "Close Pro normally (File > Exit), then re-run this script." -ForegroundColor Yellow
    exit 1
}

# ─── 2. Stop HTTP scheduled task (if registered + running) ───────────────
# The Copilot Studio HTTP server runs as a Windows Scheduled Task launching
# publish-http/ArcGisMcpServer.exe. We stop it here so (a) the MCP-server
# running check below passes, and (b) the publish-http/ exe is unlocked
# for the sync at the end.
$httpTask = Get-ScheduledTask -TaskName $httpTaskName -ErrorAction SilentlyContinue
$httpTaskWasRunning = $false
if ($httpTask -and $httpTask.State -eq 'Running') {
    Write-Host "Stopping $httpTaskName scheduled task..." -ForegroundColor Cyan
    Stop-ScheduledTask -TaskName $httpTaskName
    # Brief settle so the process actually releases the file lock.
    Start-Sleep -Seconds 2
    $httpTaskWasRunning = $true
    Write-Host "Stopped." -ForegroundColor Green
}

# ─── 3. MCP server must not be running (it's held by Claude Code) ────────
$mcpRunning = Get-Process ArcGisMcpServer -ErrorAction SilentlyContinue
if ($mcpRunning) {
    Write-Host "ArcGisMcpServer.exe is still running (held by a Claude Code session):" -ForegroundColor Red
    $mcpRunning | Format-Table Id, ProcessName, StartTime
    Write-Host "Close all Claude Code sessions attached to this MCP server, then re-run." -ForegroundColor Yellow
    # If we stopped the HTTP task above but bail here, try to restart it so
    # the user doesn't end up with Copilot Studio in a worse state than before.
    if ($httpTaskWasRunning) {
        Write-Host "Restarting $httpTaskName before exit..." -ForegroundColor Yellow
        Start-ScheduledTask -TaskName $httpTaskName
    }
    exit 1
}

# ─── 4. Rebuild the Pro Add-In ───────────────────────────────────────────
# Done before wiping the cache so a build failure leaves the prior cache
# intact — the next Pro launch still works (just with the old Add-In)
# instead of launching with no Add-In at all.
Write-Host "`nLocating MSBuild via vswhere..." -ForegroundColor Cyan
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Host "vswhere not found at $vswhere" -ForegroundColor Red
    Write-Host "Install Visual Studio (any edition) or VS Build Tools; vswhere ships with the installer." -ForegroundColor Yellow
    exit 1
}
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    Write-Host "MSBuild.exe not found via vswhere. Is Visual Studio installed?" -ForegroundColor Red
    exit 1
}
Write-Host "Using MSBuild: $msbuild" -ForegroundColor DarkGray

Write-Host "Building Add-In ($addInProj)..." -ForegroundColor Cyan
& $msbuild $addInProj -p:Configuration=Release -restore -verbosity:minimal
# MSBuild may emit warnings or the "RegisterAddIn.exe is not recognized" post-build
# notice — neither prevents the .esriAddinX bundle from being produced. Check for
# the bundle on disk rather than relying on exit code (the SDK targets sometimes
# return non-zero on benign post-build steps).
if (-not (Test-Path -LiteralPath $addInBundle)) {
    Write-Host "Add-In build did not produce $addInBundle" -ForegroundColor Red
    Write-Host "Check the MSBuild output above for compile errors." -ForegroundColor Yellow
    exit 1
}
$addInInfo = Get-Item -LiteralPath $addInBundle
Write-Host "Built: $($addInInfo.Name) ($($addInInfo.Length) bytes, $($addInInfo.LastWriteTime))" -ForegroundColor Green

# ─── 5. Wipe AssemblyCache ───────────────────────────────────────────────
if (Test-Path -LiteralPath $assemblyCache) {
    Write-Host "`nWiping AssemblyCache: $assemblyCache" -ForegroundColor Cyan
    Remove-Item -LiteralPath $assemblyCache -Recurse -Force
    Write-Host "AssemblyCache cleared." -ForegroundColor Green
} else {
    Write-Host "`nAssemblyCache already absent (nothing to clear)." -ForegroundColor DarkGray
}

# ─── 6. Deploy fresh .esriAddinX to AddIns folder ────────────────────────
# Pro's loader picks up the bundle here keyed by the Add-In GUID. Replacing
# the file is safe to do unconditionally; the cache wipe above guarantees
# Pro will re-extract from this bundle on next launch.
if (-not (Test-Path -LiteralPath $addInsDir)) {
    Write-Host "Creating AddIns target folder: $addInsDir" -ForegroundColor DarkGray
    New-Item -ItemType Directory -Force -Path $addInsDir | Out-Null
}
$deployPath = Join-Path $addInsDir 'APBridgeAddIn.esriAddinX'
Copy-Item -Force -LiteralPath $addInBundle -Destination $deployPath
$deployInfo = Get-Item -LiteralPath $deployPath
Write-Host "Deployed to: $deployPath ($($deployInfo.Length) bytes)" -ForegroundColor Green

# ─── 7. Rebuild MCP server ───────────────────────────────────────────────
Write-Host "`nRebuilding MCP server..." -ForegroundColor Cyan
& (Join-Path $ProjectRoot 'build-mcp-server.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nBuild failed - see output above." -ForegroundColor Red
    if ($httpTaskWasRunning) {
        Write-Host "Restarting $httpTaskName before exit..." -ForegroundColor Yellow
        Start-ScheduledTask -TaskName $httpTaskName
    }
    exit $LASTEXITCODE
}

# ─── 8. Sync publish-http/ from publish/ ─────────────────────────────────
# The Scheduled Task runs the exe from publish-http/, not publish/. Without
# this copy, build-mcp-server.ps1 updates publish/ but Copilot Studio
# continues to hit the previous build. Sync only when the task is registered.
if ($httpTask) {
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        Write-Host "Source exe not found at $publishedExe — skipping publish-http sync." -ForegroundColor Yellow
    } else {
        Write-Host "`nSyncing publish-http\ from publish\..." -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $httpDir | Out-Null
        Copy-Item -Force -LiteralPath $publishedExe -Destination $httpExe
        $syncInfo = Get-Item -LiteralPath $httpExe
        Write-Host "Synced: $httpExe ($($syncInfo.Length) bytes)" -ForegroundColor Green
    }
} else {
    Write-Host "`n(No $httpTaskName scheduled task registered — skipping publish-http sync.)" -ForegroundColor DarkGray
}

# ─── 9. Restart HTTP scheduled task if it was running before ─────────────
if ($httpTaskWasRunning) {
    Write-Host "Restarting $httpTaskName scheduled task..." -ForegroundColor Cyan
    Start-ScheduledTask -TaskName $httpTaskName
    Start-Sleep -Seconds 2
    $taskState = (Get-ScheduledTask -TaskName $httpTaskName).State
    Write-Host "Task state: $taskState" -ForegroundColor Green
}

Write-Host "`nReady. Reopen Claude Code first, then ArcGIS Pro." -ForegroundColor Green
Write-Host "Claude Code will load the fresh MCP server on session start." -ForegroundColor DarkGray
Write-Host "Pro will re-extract the Add-In from the deployed .esriAddinX." -ForegroundColor DarkGray
if ($httpTaskWasRunning) {
    Write-Host "HTTP server (Copilot Studio path) is back up running the fresh exe." -ForegroundColor DarkGray
}
