# restart-dev-cycle.ps1
#
# One-shot helper for the close-restart cycle after MCP server + Add-In
# changes. Performs:
#   1. Verifies ArcGIS Pro is closed (so Pro re-loads the new Add-In).
#   2. Detects and kills any ArcGisMcpServer.exe processes launched in
#      --http mode (the Copilot Studio path's exe in publish-http/). Uses
#      CommandLine inspection rather than scheduled-task state because
#      the VBS launcher exits immediately after spawning the exe, leaving
#      task state at "Ready" while the server is still alive.
#   3. Verifies no other (stdio) ArcGisMcpServer.exe is running — those
#      are Claude Code children that hold the file lock on publish/.
#   4. Locates MSBuild via vswhere and rebuilds the Pro Add-In to produce
#      a fresh .esriAddinX bundle.
#   5. Wipes the per-user AssemblyCache for this Add-In (so Pro re-extracts
#      the freshly-built bundle instead of using a cached DLL).
#   6. Deploys the freshly-built .esriAddinX into the AddIns folder.
#   7. Invokes build-mcp-server.ps1 to publish a fresh MCP server exe in publish/.
#   8. Copies the fresh exe to publish-http/ so the HTTP server (Copilot
#      Studio path) runs the same code as the stdio server.
#   9. Starts the ArcGisMcpServer-HTTP scheduled task. Wrapped in try/finally
#      so the task restarts even if an intermediate step fails — Copilot
#      Studio is never left without a server.
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

# Detect whether the HTTP task is registered. Don't rely on State property —
# see step 2 comment for why the VBS wrapper makes State unreliable.
$httpTask = Get-ScheduledTask -TaskName $httpTaskName -ErrorAction SilentlyContinue
$httpTaskExists = [bool]$httpTask

# Wrap the destructive work in try/finally so the HTTP task is restarted at
# the end regardless of which intermediate step fails. Without this, a build
# failure (step 4) or "Claude Code is open" failure (step 3) after stopping
# the server in step 2 would leave Copilot Studio without an HTTP endpoint.
try {

    # ─── 2. Stop any running HTTP server (by --http command-line match) ──
    # The Scheduled Task launches the server via a VBS wrapper that exits
    # immediately, so Task Scheduler's State is "Ready" most of the time
    # while the spawned exe is still alive. We can't rely on State -eq
    # 'Running' to detect the server. Inspect Win32_Process command lines
    # and kill any ArcGisMcpServer.exe that has "--http" in its args.
    # Stdio MCP children (Claude Code) don't pass --http, so they're left
    # alone for step 3 to deal with.
    $httpProcs = Get-CimInstance Win32_Process -Filter "Name = 'ArcGisMcpServer.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match '--http' }
    if ($httpProcs) {
        Write-Host "Stopping HTTP server process(es):" -ForegroundColor Cyan
        foreach ($p in $httpProcs) {
            Write-Host "  PID $($p.ProcessId)" -ForegroundColor DarkGray
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
        # Wait for the OS to release the file lock on publish-http/exe.
        Start-Sleep -Seconds 2
    }

    # ─── 3. MCP server must not be running (held by Claude Code stdio) ───
    # After step 2, any remaining ArcGisMcpServer processes are stdio
    # children spawned by Claude Code via .mcp.json. Those hold the file
    # lock on publish/ exe; we need Claude Code closed before continuing.
    $stdioMcp = Get-Process ArcGisMcpServer -ErrorAction SilentlyContinue
    if ($stdioMcp) {
        Write-Host "ArcGisMcpServer.exe is still running (stdio child held by a Claude Code session):" -ForegroundColor Red
        $stdioMcp | Format-Table Id, ProcessName, StartTime
        Write-Host "Close all Claude Code sessions attached to this MCP server, then re-run." -ForegroundColor Yellow
        exit 1
    }

    # ─── 4. Rebuild the Pro Add-In ───────────────────────────────────────
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

    # ─── 5. Wipe AssemblyCache ───────────────────────────────────────────
    if (Test-Path -LiteralPath $assemblyCache) {
        Write-Host "`nWiping AssemblyCache: $assemblyCache" -ForegroundColor Cyan
        Remove-Item -LiteralPath $assemblyCache -Recurse -Force
        Write-Host "AssemblyCache cleared." -ForegroundColor Green
    } else {
        Write-Host "`nAssemblyCache already absent (nothing to clear)." -ForegroundColor DarkGray
    }

    # ─── 6. Deploy fresh .esriAddinX to AddIns folder ────────────────────
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

    # ─── 7. Rebuild MCP server ───────────────────────────────────────────
    Write-Host "`nRebuilding MCP server..." -ForegroundColor Cyan
    & (Join-Path $ProjectRoot 'build-mcp-server.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`nBuild failed - see output above." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    # ─── 8. Sync publish-http/ from publish/ ─────────────────────────────
    # The Scheduled Task runs the exe from publish-http/, not publish/. Without
    # this copy, build-mcp-server.ps1 updates publish/ but Copilot Studio
    # continues to hit the previous build. Sync only when the task is registered.
    if ($httpTaskExists) {
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
}
finally {
    # ─── 9. Always start the HTTP task at end (if it's registered) ───────
    # Runs even if an earlier step failed — Copilot Studio is never left
    # without a server. Start-ScheduledTask is idempotent: if the task is
    # already running it's a no-op; if not, it triggers a fresh run.
    if ($httpTaskExists) {
        Write-Host "`nStarting $httpTaskName scheduled task..." -ForegroundColor Cyan
        Start-ScheduledTask -TaskName $httpTaskName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        # Verify the task spawned an --http process. State alone is unreliable
        # (see step 2 comment), so check the process table.
        $verifyProc = Get-CimInstance Win32_Process -Filter "Name = 'ArcGisMcpServer.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine -match '--http' } |
            Select-Object -First 1
        if ($verifyProc) {
            Write-Host "HTTP server up: PID $($verifyProc.ProcessId)" -ForegroundColor Green
        } else {
            Write-Host "WARNING: scheduled task started but no --http server process detected." -ForegroundColor Yellow
            Write-Host "Check Event Viewer (Applications and Services Logs > Microsoft > Windows > TaskScheduler) for failure details." -ForegroundColor DarkGray
        }
    }
}

Write-Host "`nReady. Reopen Claude Code first, then ArcGIS Pro." -ForegroundColor Green
Write-Host "Claude Code will load the fresh MCP server on session start." -ForegroundColor DarkGray
Write-Host "Pro will re-extract the Add-In from the deployed .esriAddinX." -ForegroundColor DarkGray
if ($httpTaskExists) {
    Write-Host "HTTP server (Copilot Studio path) is running the fresh exe." -ForegroundColor DarkGray
}
