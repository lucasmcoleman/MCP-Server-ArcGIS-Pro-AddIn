# Invoke-BridgeOp.ps1 — direct named-pipe client for the ArcGIS Pro bridge.
#
# Talks straight to the Add-In's pipe (ArcGisProBridge_<PID>), bypassing the
# MCP server entirely. Useful for:
#   - testing new bridge ops before the MCP server exposes them
#   - testing while a Claude Code session holds the published MCP exe lock
#   - scripted smoke tests in CI / dev loops
#
# Usage:
#   ./tools/Invoke-BridgeOp.ps1 -Op pro.getProjectInfo
#   ./tools/Invoke-BridgeOp.ps1 -Op pro.listLayers -Args @{ map = 'Map' }
#   ./tools/Invoke-BridgeOp.ps1 -Op pro.runGPTool -Args @{ tool='analysis.Buffer'; parameters='["Roads","out_fc","100 Meters"]' }
#
# Output: the raw JSON IpcResponse line ({"ok":...,"error":...,"data":...}).

param(
    [Parameter(Mandatory = $true)] [string]$Op,
    [hashtable]$Args = $null,
    [int]$ConnectTimeoutMs = 5000,
    [int]$ReadTimeoutMs = 600000,
    [string]$PipeName = $null
)

$ErrorActionPreference = 'Stop'

# ─── Discover the pipe (same logic as BridgeDiscovery) ───────────────────
if (-not $PipeName) {
    $dir = Join-Path $env:LOCALAPPDATA 'ArcGisMcpBridge'
    $entries = @()
    if (Test-Path $dir) {
        foreach ($f in Get-ChildItem $dir -Filter '*.json') {
            try {
                $e = Get-Content $f.FullName -Raw | ConvertFrom-Json
                if ($e.pipeName -and (Get-Process -Id $e.pid -ErrorAction SilentlyContinue)) {
                    $entries += $e
                }
            } catch { }
        }
    }
    if ($entries.Count -eq 0) {
        Write-Error 'No live bridge registry entries found — is ArcGIS Pro running with the Add-In loaded?'
        exit 1
    }
    $pick = $entries | Sort-Object { [datetime]$_.startedUtc } -Descending | Select-Object -First 1
    $PipeName = $pick.pipeName
    Write-Verbose "Using bridge pid=$($pick.pid) pipe=$PipeName project=$($pick.projectName)"
}

# ─── Build the request line ──────────────────────────────────────────────
$argsObj = @{}
if ($Args) { foreach ($k in $Args.Keys) { $argsObj[$k] = [string]$Args[$k] } }
$request = @{ op = $Op; args = $argsObj } | ConvertTo-Json -Compress -Depth 5

# ─── Connect, send, receive ──────────────────────────────────────────────
$client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName,
    [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
try {
    $client.Connect($ConnectTimeoutMs)
    $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $reader = [System.IO.StreamReader]::new($client, [System.Text.Encoding]::UTF8)

    $writer.WriteLine($request)

    $readTask = $reader.ReadLineAsync()
    if (-not $readTask.Wait($ReadTimeoutMs)) {
        Write-Error "Timed out after ${ReadTimeoutMs}ms waiting for response to '$Op'."
        exit 2
    }
    $line = $readTask.Result
    if ($null -eq $line) {
        Write-Error 'Bridge closed the pipe without responding.'
        exit 3
    }
    # Emit raw JSON; callers can pipe to ConvertFrom-Json.
    $line
}
finally {
    $client.Dispose()
}
