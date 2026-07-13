# build-addin.ps1
#
# Rebuild + deploy ONLY the Pro Add-In (.esriAddinX). Companion to
# build-mcp-server.ps1 (MCP exe only) and restart-dev-cycle.ps1 (both halves).
#
# Use this when a change touches AddIn/** only. Claude Code / MCP client
# sessions can STAY OPEN — they hold the file lock on publish/ArcGisMcpServer.exe,
# which this script never touches. Only ArcGIS Pro must be closed, and only
# for the deploy step: the build itself always runs, so compile errors
# surface even while Pro is open.
#
#   .\build-addin.ps1              # build, then deploy (Pro must be closed to deploy)
#   .\build-addin.ps1 -BuildOnly   # compile check only, no deploy
#
# After deploying: reopen Pro, wait out the warm-up window, then run
# .\tools\Test-BridgeLive.ps1 to smoke-test the fresh Add-In.

param(
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$addInGuid     = '{c56ccfd4-f12a-4916-84c2-64248b3d746c}'   # AddInInfo id in Config.daml — stable across builds
$addInProj     = Join-Path $ProjectRoot 'AddIn\APBridgeAddIn\APBridgeAddIn.csproj'
$addInBundle   = Join-Path $ProjectRoot 'AddIn\APBridgeAddIn\bin\Release\net8.0-windows8.0\APBridgeAddIn.esriAddinX'
$addInsDir     = Join-Path $env:USERPROFILE "Documents\ArcGIS\AddIns\ArcGISPro\$addInGuid"
$assemblyCache = Join-Path $env:LOCALAPPDATA "ESRI\ArcGISPro\AssemblyCache\$addInGuid"

# ─── 1. Build (safe with Pro AND Claude Code open) ───────────────────────
# Must use VS MSBuild, not `dotnet build` — the Pro SDK targets file
# (Esri.ProApp.SDK.Desktop.targets) uses CodeTaskFactory, which is MSBuild-only.
Write-Host "Locating MSBuild via vswhere..." -ForegroundColor Cyan
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

$buildStart = Get-Date
Write-Host "Building Add-In ($addInProj)..." -ForegroundColor Cyan
& $msbuild $addInProj -p:Configuration=Release -restore -verbosity:minimal
# MSBuild may emit warnings or the "RegisterAddIn.exe is not recognized" post-build
# notice — neither prevents the .esriAddinX bundle from being produced. Verify the
# bundle exists AND is fresher than the build start, so a failed build can't pass
# on the strength of a stale bundle left over from an earlier successful one.
if (-not (Test-Path -LiteralPath $addInBundle)) {
    Write-Host "Add-In build did not produce $addInBundle" -ForegroundColor Red
    Write-Host "Check the MSBuild output above for compile errors." -ForegroundColor Yellow
    exit 1
}
$addInInfo = Get-Item -LiteralPath $addInBundle
if ($addInInfo.LastWriteTime -lt $buildStart) {
    Write-Host "Bundle at $addInBundle is STALE (older than this build's start) — the build failed before repackaging." -ForegroundColor Red
    Write-Host "Check the MSBuild output above for compile errors." -ForegroundColor Yellow
    exit 1
}
Write-Host "Built: $($addInInfo.Name) ($($addInInfo.Length) bytes, $($addInInfo.LastWriteTime))" -ForegroundColor Green

if ($BuildOnly) {
    Write-Host "`n-BuildOnly: skipping deploy. Bundle is ready at:" -ForegroundColor Green
    Write-Host "  $addInBundle" -ForegroundColor DarkGray
    exit 0
}

# ─── 2. Deploy gate: Pro must be closed ──────────────────────────────────
# Never auto-kill Pro. If it's running, the build above still gave you the
# compile signal; close Pro yourself and re-run (incremental build is fast).
$proRunning = Get-Process ArcGISPro -ErrorAction SilentlyContinue
if ($proRunning) {
    Write-Host "`nArcGIS Pro is still running:" -ForegroundColor Red
    $proRunning | Format-Table Id, ProcessName, StartTime
    Write-Host "Build succeeded, but the deploy copy needs Pro closed." -ForegroundColor Yellow
    Write-Host "Close Pro normally (File > Exit), then re-run this script — the rebuild is incremental and quick." -ForegroundColor Yellow
    exit 1
}

# ─── 3. Wipe AssemblyCache ───────────────────────────────────────────────
# Pro caches extracted DLLs here and may not re-extract on identical-mtime
# input; wiping guarantees the next launch loads the fresh bundle.
if (Test-Path -LiteralPath $assemblyCache) {
    Write-Host "`nWiping AssemblyCache: $assemblyCache" -ForegroundColor Cyan
    Remove-Item -LiteralPath $assemblyCache -Recurse -Force
    Write-Host "AssemblyCache cleared." -ForegroundColor Green
} else {
    Write-Host "`nAssemblyCache already absent (nothing to clear)." -ForegroundColor DarkGray
}

# ─── 4. Deploy fresh .esriAddinX to AddIns folder ────────────────────────
if (-not (Test-Path -LiteralPath $addInsDir)) {
    Write-Host "Creating AddIns target folder: $addInsDir" -ForegroundColor DarkGray
    New-Item -ItemType Directory -Force -Path $addInsDir | Out-Null
}
$deployPath = Join-Path $addInsDir 'APBridgeAddIn.esriAddinX'
Copy-Item -Force -LiteralPath $addInBundle -Destination $deployPath
$deployInfo = Get-Item -LiteralPath $deployPath
Write-Host "Deployed to: $deployPath ($($deployInfo.Length) bytes)" -ForegroundColor Green

Write-Host "`nReady. Reopen ArcGIS Pro — it will re-extract the fresh Add-In." -ForegroundColor Green
Write-Host "MCP server exe untouched: Claude Code sessions did not need to close." -ForegroundColor DarkGray
Write-Host "After Pro's warm-up window, smoke-test with: .\tools\Test-BridgeLive.ps1" -ForegroundColor DarkGray
