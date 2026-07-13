# Test-McpStdio.ps1 — minimal stdio MCP client for smoke-testing the server
# without an MCP host. Sends initialize → initialized → one tools/call and
# prints the tool result. Keeps stdin open (file-redirect EOF makes the host
# shut down before it answers).
#
# Usage:
#   ./tools/Test-McpStdio.ps1 -Tool list_bridges
#   ./tools/Test-McpStdio.ps1 -Tool ping -ServerPath <exe-or-dll> -Env @{ ARCGIS_PROJECT = 'MyProj' }

param(
    [string]$Tool,
    [string]$ArgumentsJson = '{}',
    # Sequence of calls in ONE server process (needed to test session state
    # like select_bridge). Each item: 'tool_name' or 'tool_name|{"arg":"v"}'.
    [string[]]$Calls,
    # Defaults to the published single-file exe — the artifact .mcp.json actually
    # wires up for MCP clients. Falls back to the bin/Release DLL (a DIFFERENT,
    # separately-built artifact) with a loud warning if the exe hasn't been
    # published yet.
    [string]$ServerPath = "McpServer\ArcGisMcpServer\publish\ArcGisMcpServer.exe",
    [hashtable]$Env = @{},
    [int]$TimeoutSec = 30
)

if (-not $Calls) {
    if (-not $Tool) { throw 'Provide -Tool or -Calls.' }
    $Calls = @("$Tool|$ArgumentsJson")
}

$ErrorActionPreference = 'Stop'

if ($ServerPath -eq "McpServer\ArcGisMcpServer\publish\ArcGisMcpServer.exe" -and -not (Test-Path $ServerPath)) {
    $fallback = "McpServer\ArcGisMcpServer\bin\Release\net8.0\ArcGisMcpServer.dll"
    Write-Warning "Published exe not found at '$ServerPath'. Falling back to '$fallback' — NOTE: this is NOT the artifact .mcp.json wires up for real MCP clients; run build-mcp-server.ps1 to test the real thing."
    $ServerPath = $fallback
}

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
    $stdin.Flush()

    $id = 1
    foreach ($spec in $Calls) {
        $name, $argsJson = $spec -split '\|', 2
        if (-not $argsJson) { $argsJson = '{}' }
        $id++
        $call = '{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"' + $name + '","arguments":' + $argsJson + '}}'
        $stdin.WriteLine($call)
        $stdin.Flush()

        $resp = Read-Response $id | ConvertFrom-Json
        "=== $name ==="
        # Tool output is MCP content blocks; print the text payloads.
        foreach ($c in $resp.result.content) { if ($c.type -eq 'text') { $c.text } }
        if ($resp.error) { Write-Error ($resp.error | ConvertTo-Json -Compress) }
    }
}
finally {
    try { $proc.Kill() } catch { }
}
