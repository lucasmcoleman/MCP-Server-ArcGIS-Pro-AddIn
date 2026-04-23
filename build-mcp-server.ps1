# Builds the MCP server as a single-file Windows exe used by .mcp.json.
#
# WHY: Each Claude Code session that uses this MCP server keeps an
# ArcGisMcpServer.exe process alive for the duration of the session.
# `dotnet build` was unable to replace the locked Debug exe across
# multiple sessions. Publishing a single-file exe to a stable, gitignored
# path lets re-publishes succeed when no sessions are currently running,
# and avoids the `dotnet run --no-build` overhead on session start.
#
# Run this after any code change in McpServer/. If publish fails with
# "file is locked", close all Claude Code sessions that have this MCP
# server attached and retry.

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

$proj   = 'McpServer/ArcGisMcpServer/ArcGisMcpServer.csproj'
$output = 'McpServer/ArcGisMcpServer/publish'

# Refuse to publish if any session is holding the exe — gives a clear
# error instead of the cryptic MSB3027 retry-loop.
$running = Get-Process ArcGisMcpServer -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Cannot publish — ArcGisMcpServer.exe is running:" -ForegroundColor Red
    $running | Format-Table Id, ProcessName, StartTime
    Write-Host "Close any Claude Code session attached to this MCP server, then retry." -ForegroundColor Yellow
    exit 1
}

dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $output

if ($LASTEXITCODE -eq 0) {
    $exe = Join-Path $output 'ArcGisMcpServer.exe'
    Write-Host "`nPublished:" -ForegroundColor Green
    Get-Item $exe | Format-Table Name, Length, LastWriteTime
}
