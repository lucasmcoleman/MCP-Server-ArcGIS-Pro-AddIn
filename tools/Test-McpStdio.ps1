# Test-McpStdio.ps1 — minimal stdio MCP client for smoke-testing the server
# without an MCP host. Sends initialize → initialized → one tools/call and
# prints the tool result. Keeps stdin open (file-redirect EOF makes the host
# shut down before it answers).
#
# Usage:
#   ./tools/Test-McpStdio.ps1 -Tool list_bridges
#   ./tools/Test-McpStdio.ps1 -Tool ping -ServerPath <exe-or-dll> -Env @{ ARCGIS_PROJECT = 'MyProj' }

param(
    [Parameter(Mandatory = $true)] [string]$Tool,
    [string]$ArgumentsJson = '{}',
    [string]$ServerPath = "McpServer\ArcGisMcpServer\bin\Release\net8.0\ArcGisMcpServer.dll",
    [hashtable]$Env = @{},
    [int]$TimeoutSec = 30
)

$ErrorActionPreference = 'Stop'

$psi = [System.Diagnostics.ProcessStartInfo]::new()
if ($ServerPath.EndsWith('.dll')) {
    $psi.FileName = 'dotnet'
    $psi.ArgumentList.Add((Resolve-Path $ServerPath).Path)
} else {
    $psi.FileName = (Resolve-Path $ServerPath).Path
}
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
foreach ($k in $Env.Keys) { $psi.Environment[$k] = [string]$Env[$k] }

$proc = [System.Diagnostics.Process]::Start($psi)
try {
    $stdin = $proc.StandardInput

    # Read one JSON-RPC response line matching the given id, skipping
    # notifications/log lines the server may emit in between.
    function Read-Response([int]$id) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-Date) -lt $deadline) {
            $line = $proc.StandardOutput.ReadLine()
            if ($null -eq $line) { throw 'server closed stdout' }
            if ($line -match "`"id`"\s*:\s*$id\b") { return $line }
        }
        throw "timeout waiting for response id=$id"
    }

    $stdin.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}')
    $stdin.Flush()
    Read-Response 1 | Out-Null

    $stdin.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $call = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"' + $Tool + '","arguments":' + $ArgumentsJson + '}}'
    $stdin.WriteLine($call)
    $stdin.Flush()

    $resp = Read-Response 2 | ConvertFrom-Json
    # Tool output is MCP content blocks; print the text payloads.
    foreach ($c in $resp.result.content) { if ($c.type -eq 'text') { $c.text } }
    if ($resp.error) { Write-Error ($resp.error | ConvertTo-Json -Compress) }
}
finally {
    try { $proc.Kill() } catch { }
}
