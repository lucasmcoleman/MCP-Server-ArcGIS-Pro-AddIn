# restart-dev-cycle.ps1
#
# One-shot helper for the close-restart cycle after MCP server + Add-In
# changes. Performs three steps:
#   1. Verifies ArcGIS Pro is closed (so Pro re-loads the new Add-In).
#   2. Verifies ArcGisMcpServer.exe is not running (so publish can overwrite).
#   3. Wipes the per-user AssemblyCache for this Add-In (so Pro re-extracts
#      the current .esriAddinX instead of using a cached DLL).
#   4. Invokes build-mcp-server.ps1 to publish a fresh MCP server exe.
#
# Run this from the project root (same folder as build-mcp-server.ps1)
# right after closing Pro and Claude Code, BEFORE reopening them.

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

$addInGuid     = '{c56ccfd4-f12a-4916-84c2-64248b3d746c}'
$assemblyCache = Join-Path $env:LOCALAPPDATA "ESRI\ArcGISPro\AssemblyCache\$addInGuid"

# ─── 1. Pro must be closed ───────────────────────────────────────────────
$proRunning = Get-Process ArcGISPro -ErrorAction SilentlyContinue
if ($proRunning) {
    Write-Host "ArcGIS Pro is still running:" -ForegroundColor Red
    $proRunning | Format-Table Id, ProcessName, StartTime
    Write-Host "Close Pro normally (File > Exit), then re-run this script." -ForegroundColor Yellow
    exit 1
}

# ─── 2. MCP server must not be running (it's held by Claude Code) ────────
$mcpRunning = Get-Process ArcGisMcpServer -ErrorAction SilentlyContinue
if ($mcpRunning) {
    Write-Host "ArcGisMcpServer.exe is still running (held by a Claude Code session):" -ForegroundColor Red
    $mcpRunning | Format-Table Id, ProcessName, StartTime
    Write-Host "Close all Claude Code sessions attached to this MCP server, then re-run." -ForegroundColor Yellow
    exit 1
}

# ─── 3. Wipe AssemblyCache ───────────────────────────────────────────────
if (Test-Path -LiteralPath $assemblyCache) {
    Write-Host "Wiping AssemblyCache: $assemblyCache" -ForegroundColor Cyan
    Remove-Item -LiteralPath $assemblyCache -Recurse -Force
    Write-Host "AssemblyCache cleared." -ForegroundColor Green
} else {
    Write-Host "AssemblyCache already absent (nothing to clear)." -ForegroundColor DarkGray
}

# ─── 4. Rebuild MCP server ───────────────────────────────────────────────
Write-Host "`nRebuilding MCP server..." -ForegroundColor Cyan
& (Join-Path $ProjectRoot 'build-mcp-server.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nBuild failed - see output above." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`nReady. Reopen Claude Code first, then ArcGIS Pro." -ForegroundColor Green
Write-Host "Claude Code will load the fresh MCP server on session start." -ForegroundColor DarkGray
Write-Host "Pro will re-extract the Add-In from the deployed .esriAddinX." -ForegroundColor DarkGray
