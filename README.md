# ArcGIS Pro MCP Bridge

An [MCP (Model Context Protocol)](https://modelcontextprotocol.io) server that lets MCP clients — Claude Code, GitHub Copilot Agent Mode, Anthropic API integrations, anything that speaks MCP — drive **ArcGIS Pro** in real time. The server brokers calls over a named pipe to an in-process Pro Add-In, exposing 28 tools spanning map operations, project lifecycle, geoprocessing, ModelBuilder, and layout production.

![MCP Server with ArcGIS Pro Add-In](MCPServer_ArcGISAddIn.gif)

---

## What it does

- **Drive Pro from natural language.** Ask the agent to "buffer the West Coast states by 50 miles, dissolve, calculate area in square miles, then export a PDF" and it chains the right MCP tools together.
- **28 first-class tools** across 6 domains (full list below). All return structured JSON, not opaque strings — agents can introspect errors and chain operations programmatically.
- **Multi-Pro routing.** Each Pro instance binds a per-PID pipe and writes a registry entry; the MCP server picks the right one (most-recent-started, or by `ARCGIS_PROJECT` env var).
- **Survives Pro restarts mid-session.** The MCP server re-discovers the live pipe on every request, so closing and reopening Pro doesn't require restarting your MCP client.
- **Robust error handling.** Silent failures fail loud (`Layer not found: X` instead of `0`); slow operations don't time out prematurely (default 120s); typed-return tools surface bridge errors as structured JSON instead of getting swallowed by the MCP SDK's generic-error wrapper.

See [`CHANGELOG.md`](CHANGELOG.md) for the evolution from a basic pipe demo to the current state.

---

## Architecture

```
   ┌─────────────────────┐    stdio (JSON-RPC)    ┌──────────────────────┐
   │  MCP Client         │◀──────────────────────▶│  ArcGisMcpServer.exe │
   │  (Claude Code,      │                        │  (.NET 8 console)    │
   │   Copilot Agent…)   │                        │                      │
   └─────────────────────┘                        │  - 28 [McpServerTool]│
                                                  │    methods           │
                                                  │  - BridgeClient      │
                                                  │  - BridgeDiscovery   │
                                                  └──────────┬───────────┘
                                                             │
                                          named pipe `ArcGisProBridge_<PID>`
                                                             │
                                                             ▼
                                                  ┌──────────────────────┐
                                                  │ ArcGIS Pro process   │
                                                  │ (per-instance PID)   │
                                                  │                      │
                                                  │  ┌────────────────┐  │
                                                  │  │ APBridgeAddIn  │  │
                                                  │  │ Module1 +      │  │
                                                  │  │ ProBridgeService│  │
                                                  │  └────────────────┘  │
                                                  └──────────────────────┘

                                                             │
                                                             ▼
                                  registry: %LOCALAPPDATA%\ArcGisMcpBridge\<PID>.json
```

**Discovery contract:** Each Pro instance, on Add-In load, writes a JSON file at `%LOCALAPPDATA%\ArcGisMcpBridge\<PID>.json` describing its `pid`, `pipeName`, `projectPath`, `projectName`, and `startedUtc`. On every MCP tool call, `BridgeDiscovery.Discover()` reads that directory, filters out dead PIDs (cleaning them up), and selects:
1. Bridge whose `projectName` matches `$env:ARCGIS_PROJECT` (case-insensitive), if set.
2. Otherwise, the most-recently-started live bridge.
3. Falls back to the legacy hard-coded `ArcGisProBridgePipe` if no entries exist (back-compat with pre-discovery Add-Ins).

---

## Available tools

All tools return JSON strings. Success returns the operation's data payload; failure returns `{"success":false,"op":"pro.X","error":"..."}` with the bridge's error text intact.

### Diagnostics
| Tool | Purpose |
|---|---|
| `ping` | Validate MCP server is alive (server-local, doesn't hit Pro) |
| `echo` | Round-trip test (server-local) |
| `get_active_map_name` | Name of the active map view |
| `get_current_extent` | Viewport extent + SR (clamped to ±180/±90 for geographic SRs) |
| `get_view_diagnostics` | Raw Map/Extent/Camera state — for debugging projection/extent oddities |

### Map operations
| Tool | Purpose |
|---|---|
| `list_layers` | Layer names in the active map |
| `list_maps` | All maps in the project |
| `count_features` | Feature count for a layer (errors if not found) |
| `zoom_to_layer` | Zoom the active view to a layer's extent (errors if not found) |
| `select_by_attribute` | Select features via SQL WHERE clause |
| `clear_selection` | Clear selection on one layer or all layers |
| `add_layer_from_url` | Add a layer from a service URL |
| `add_layer_from_file` | Add a layer from a file path (.shp, .gdb FC, raster) |
| `export_layer` | Export to feature class or shapefile |

### Project lifecycle
| Tool | Purpose |
|---|---|
| `get_project_info` | Project name, .aprx path, default gdb/toolbox, counts, active map |
| `list_toolboxes` | Toolboxes referenced by the project |
| `create_project` | New project from optional template; saves current first |
| `open_project` | Open an .aprx; saves current first |
| `save_project` | Explicit `Project.Current.SaveAsync()` |

### Geoprocessing
| Tool | Purpose |
|---|---|
| `run_gp_tool` | Run any GP tool (`analysis.Buffer`, `management.AddField`, etc.). Handles value-table parameters via two-level JSON arrays. |

### ModelBuilder
| Tool | Purpose |
|---|---|
| `create_toolbox` | New empty `.atbx` |
| `list_models` | Tools (Model/Script) in a toolbox |
| `describe_model` | Full JSON definition of a model (inputs + steps + connections) |
| `create_model` | Build a model from a JSON definition |
| `update_model` | Replace a model's definition |
| `run_model` | Execute a model with parameters |

### Layouts
| Tool | Purpose |
|---|---|
| `list_layouts` | Layouts in the project |
| `create_layout` | New blank layout (configurable size/orientation) |
| `open_layout` | Open the layout in a Pro pane |
| `list_layout_elements` | Map frames, text elements, etc. on a layout |
| `set_layout_text` | Update a text element's content |
| `add_map_frame_to_layout` | Place a map frame on a layout and bind it to a map |
| `export_layout` | PDF / PNG / JPG / TIFF / SVG export |

---

## Prerequisites

| Component | Version | Notes |
|---|---|---|
| ArcGIS Pro | 3.6+ (`desktopVersion=3.6.59527` in `Config.daml`) | The Pro SDK targets the running Pro version |
| ArcGIS Pro SDK for .NET | matching Pro version | Installs as a VS extension |
| Visual Studio 2022 | 17.14+ for MCP Agent Mode in Copilot, any 17.x for Add-In dev | MSBuild from VS is required for the Add-In (NOT `dotnet build`) |
| .NET 8 SDK | 8.0+ | For the MCP server |
| PowerShell | `pwsh` 7+ | For build scripts |
| MCP client | Claude Code, Copilot Agent Mode, Claude Desktop, etc. | Anything that speaks MCP stdio |

---

## Build & deploy

The project has two independently-built artifacts:

### 1. ArcGIS Pro Add-In (`AddIn/APBridgeAddIn/`)

**Important:** must be built with **MSBuild from Visual Studio**, not `dotnet build`. The Pro SDK uses `Esri.ProApp.SDK.Desktop.targets` which depends on `CodeTaskFactory` (MSBuild-only).

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  AddIn/APBridgeAddIn/APBridgeAddIn.csproj -p:Configuration=Debug
```

Output: `AddIn/APBridgeAddIn/bin/Debug/net8.0-windows8.0/APBridgeAddIn.esriAddinX` (a ZIP bundle with the DLL + manifest).

**Deploy** by copying that file to:
```
C:\Users\<you>\Documents\ArcGIS\AddIns\ArcGISPro\{c56ccfd4-f12a-4916-84c2-64248b3d746c}\APBridgeAddIn.esriAddinX
```
The GUID is the `AddInInfo id` from `Config.daml` — stable across builds. Pro de-dupes by GUID, only the highest-version copy loads.

`CS8632` warnings ("annotation for nullable reference types should only be used in a `#nullable` annotations context") are cosmetic — ignore them.

### 2. MCP Server (`McpServer/ArcGisMcpServer/`)

A single-file publish via the helper script:

```powershell
pwsh ./build-mcp-server.ps1
```

This runs `dotnet publish ... -p:PublishSingleFile=true` to produce `McpServer/ArcGisMcpServer/publish/ArcGisMcpServer.exe`. The `.mcp.json` at the repo root points directly at this exe — no `dotnet run` wrapper, faster cold start.

The script refuses to run if `ArcGisMcpServer.exe` is currently running (any Claude Code session attached holds the file lock).

### 3. The close-restart cycle

When you change Add-In code, MCP-server code, or both, use the helper:

```powershell
# After editing code:
pwsh ./build-mcp-server.ps1            # rebuild MCP server (only if MCP code changed)

# Before reopening Pro / Claude Code:
pwsh ./restart-dev-cycle.ps1
```

`restart-dev-cycle.ps1`:
1. Verifies Pro and `ArcGisMcpServer.exe` are closed (refuses to run if not).
2. Wipes the per-user AssemblyCache for the Add-In GUID — Pro caches the extracted DLL there and may not re-extract on next launch if the cache is fresh.
3. Invokes `build-mcp-server.ps1`.

After it finishes, reopen Claude Code (loads the new MCP exe), then reopen Pro (re-extracts the deployed `.esriAddinX`).

---

## Configuration

### `.mcp.json`

The repo root ships a working manifest pointing at the published exe:

```json
{
  "inputs": [],
  "servers": {
    "arcgis": {
      "type": "stdio",
      "command": "McpServer/ArcGisMcpServer/publish/ArcGisMcpServer.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Add `env` overrides as needed (see below).

### Environment variables

Tunable from the `env` block of `.mcp.json` or the parent shell:

| Variable | Default | Purpose |
|---|---|---|
| `ARCGIS_MCP_PIPE_NAME` | _(unset)_ | Hard-code the pipe name, bypassing discovery. Use only for containers / non-standard deployments. |
| `ARCGIS_PROJECT` | _(unset)_ | When set, discovery prefers a bridge whose `projectName` matches (case-insensitive). Lets a Claude session pin to a specific .aprx. |
| `ARCGIS_MCP_MAX_RETRIES` | `3` | Retries after the first attempt (for transient errors only — timeouts no longer retry, see below) |
| `ARCGIS_MCP_CONNECT_TIMEOUT_MS` | `5000` | Named-pipe `ConnectAsync` timeout |
| `ARCGIS_MCP_REQUEST_TIMEOUT_MS` | `120000` | End-to-end per-request timeout. Raised from 30s to 120s to handle slow Pro ops like `create_project` on a fresh template. |
| `ARCGIS_MCP_INITIAL_BACKOFF_MS` | `250` | First retry backoff (doubles each retry) |
| `ARCGIS_MCP_MAX_BACKOFF_MS` | `4000` | Cap for the exponential backoff |

**Timeout behavior:** A genuine handler timeout (the bridge stops responding) returns `{success:false, error:"timeout: ..."}` immediately rather than retrying for `MaxRetries × RequestTimeoutMs` more. Transient connection errors (pipe not yet up after a Pro restart, broken pipe mid-request) still retry with backoff.

### Targeting a specific Pro instance

Two ways:

**Project-name routing** — set in `.mcp.json`:
```json
{
  "servers": {
    "arcgis": {
      "command": "McpServer/ArcGisMcpServer/publish/ArcGisMcpServer.exe",
      "env": {
        "ARCGIS_PROJECT": "MyProject.aprx"
      }
    }
  }
}
```
The MCP server will pick a bridge whose live registry entry matches `MyProject.aprx`. Multiple Claude Code sessions can each pin to different projects this way.

**Pipe-name pinning** — for containers or scripted bridges:
```json
"env": { "ARCGIS_MCP_PIPE_NAME": "ArcGisProBridge_12345" }
```
Skips discovery entirely. Use sparingly; the per-request rediscovery is what makes Pro restarts transparent.

---

## Logging

The Add-In writes `mcp-bridge.log` to `Project.Current.HomeFolderPath`, falling back to `%TEMP%` if no project is open. Two entry types:

- **Thrown exceptions** — full stack trace via `LogException`.
- **Non-success responses** — `RESPONSE_NOT_OK error=<message>` entries via `LogNonSuccess`. Captures structured failures (`{success:false}` returns) that aren't exceptions, so handlers like `export_layout` and `count_features` leave an audit trail when they reject input.

Best-effort — logging never breaks the IPC loop.

---

## Troubleshooting

### "An error occurred invoking 'X'" generic errors after a Pro restart
Pre-`G7` this happened because the MCP server cached the original pipe name. As of [`27672f8`](https://github.com/lucasmcoleman/MCP-Server-ArcGIS-Pro-AddIn/commit/27672f8), it re-discovers per request — so this should self-heal on the next call. If it doesn't:
1. Check `%LOCALAPPDATA%\ArcGisMcpBridge\` — there should be exactly one `<PID>.json` whose PID matches your live Pro.
2. Check the bridge log: `<aprx-folder>\mcp-bridge.log`.

### Add-In change didn't take effect after rebuild
Pro caches the extracted DLL in `%LOCALAPPDATA%\ESRI\ArcGISPro\AssemblyCache\{c56ccfd4-…}\`. The `restart-dev-cycle.ps1` script wipes this for you. If you run a manual deploy, also delete that folder before relaunching Pro.

### `'RegisterAddIn.exe' is not recognized` during MSBuild
Benign. The `.esriAddinX` bundle is built before that step; just do the file copy manually to the AddIns folder.

### Bypass the MCP server entirely (raw pipe debugging)

Useful when MCP-routed calls fail mysteriously and you want to confirm the Add-In is healthy:

```powershell
$p = New-Object System.IO.Pipes.NamedPipeClientStream(".", "ArcGisProBridge_<PID>", [System.IO.Pipes.PipeDirection]::InOut)
$p.Connect(3000)
$r = New-Object System.IO.StreamReader($p)
$w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true
$w.WriteLine('{"op":"pro.getActiveMapName","args":null}')
$r.ReadLine()
```

Replace `<PID>` with the live Pro PID from `tasklist` or the registry JSON.

---

## Container image (optional)

```bash
docker build -f McpServer/ArcGisMcpServer/Dockerfile -t arcgis-mcp-server .
docker run --rm -i \
  -e ARCGIS_MCP_PIPE_NAME=ArcGisProBridgePipe \
  arcgis-mcp-server
```

> **IPC limitation:** Named pipes don't traverse container ↔ host boundaries on Linux. The image is intended for packaging/distribution and Windows-container hosts that share the host's pipe namespace. For local dev on the same Windows machine as Pro, the published exe (via `build-mcp-server.ps1`) is simpler.

---

## Project structure

```
MCP-Server-ArcGIS-Pro-AddIn/
├── AddIn/
│   └── APBridgeAddIn/                 # ArcGIS Pro Add-In (in-process)
│       ├── Config.daml                # Add-In manifest (GUID, version, autoLoad)
│       ├── Module1.cs                 # Module init/uninit + project event subscriptions
│       ├── Button1.cs                 # Optional UI button (start/stop bridge)
│       ├── ProBridgeService.cs        # Named-pipe server + 28 op handlers
│       ├── BridgeRegistry.cs          # Per-PID registry write/update/cleanup
│       ├── IpcModels.cs               # IpcRequest / IpcResponse records
│       ├── ModelBuilder/
│       │   └── AtbxManager.cs         # Model JSON ↔ .atbx ZIP marshalling
│       └── APBridgeAddIn.csproj       # MSBuild project (Pro SDK targets)
│
├── McpServer/
│   └── ArcGisMcpServer/               # MCP server (.NET 8 console)
│       ├── Program.cs                 # Host bootstrap, BridgeClient DI
│       ├── Tools/
│       │   └── ProTools.cs            # [McpServerTool] methods (one per op)
│       ├── Ipc/
│       │   ├── BridgeClient.cs        # Named-pipe client w/ retry, timeout, rediscovery
│       │   ├── BridgeDiscovery.cs     # Reads %LOCALAPPDATA%\ArcGisMcpBridge\
│       │   └── IpcModels.cs           # Mirror of Add-In DTOs
│       ├── Dockerfile
│       └── ArcGisMcpServer.csproj
│
├── .mcp.json                          # MCP manifest (points at published exe)
├── build-mcp-server.ps1               # Single-file publish helper
├── restart-dev-cycle.ps1              # Wipe AssemblyCache + rebuild MCP in one shot
├── README.md                          # This file
├── CHANGELOG.md                       # Round 1/2/3 evolution
└── MCPServer_ArcGISAddIn.gif          # Demo
```

---

## Extending — patterns to follow

When adding a new handler, follow the established idioms (every one of these is a verified pattern from the Round 1/2/3 work):

| Situation | Pattern |
|---|---|
| Project-lifecycle ops (Create/Open project) | Wrap body in `await System.Windows.Application.Current.Dispatcher.InvokeAsync(...)` and unwrap the nested `Task` returned. `QueuedTask.Run` alone is **not** enough — `Project.CreateAsync`/`OpenAsync` need the WPF GUI thread. |
| GUI-thread pane creation (open layout, etc.) | Lookup work inside `QueuedTask.Run`; pane creation via `FrameworkApplication.Current.Dispatcher.InvokeAsync` |
| Resource not found (layer, layout, map) | `throw new InvalidOperationException($"Layer not found: {name}")` inside QueuedTask. The outer `RunAsync` catch logs it and returns a structured error. |
| GP tool error messages | Build defensively: `result.Messages.Any() ? join(...) : "fallback message including tool name"` to avoid empty `"GP tool failed: "` strings. |
| Silent-success detection | After the SDK call, post-check (e.g., `File.Exists`, `projectName match`) and return `{success:false}` if the post-check fails. The SDK can lie. |
| Value-table GP params | Use `FlattenGpParam(JsonNode)` — handles two-level `JsonArray` as arcpy `"tok tok;tok tok"` strings. |
| MCP tool wrapper (`ProTools.cs`) | Always `Task<string>` returning `FormatResult(r, "pro.X")`. Never `throw new Exception(r.Error)` — the MCP SDK swallows thrown messages, leaving only generic errors. |
| Geographic extent handling | If `sr.IsGeographic` and any bound exceeds ±180/±90, clamp before returning. Pro doesn't clamp `MapView.Extent` to SR domain. |
| NaN/Infinity in responses | The bridge's `SendAsync` already uses `JsonNumberHandling.AllowNamedFloatingPointLiterals` — no extra work needed for Pro SDK doubles like `Camera.Pitch`. |

See `CHANGELOG.md` for the historical context (which fix introduced which pattern).

---

## License

This project is provided as-is for demonstration and development purposes. Check with the repo owner for production-use licensing.

---

## Acknowledgments

- The MCP SDK and protocol from [Model Context Protocol](https://modelcontextprotocol.io)
- Esri's ArcGIS Pro SDK for .NET
- The Round 1/2/3 hardening work — see `CHANGELOG.md`
