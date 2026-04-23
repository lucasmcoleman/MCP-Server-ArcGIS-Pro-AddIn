# MCP Server with ArcGIS Pro Add-In (C# .NET 8)

This repository demonstrates how to integrate a **Model Context Protocol (MCP) server** with an **ArcGIS Pro Add-In**. The goal is to expose ArcGIS Pro functionality as MCP tools so that GitHub Copilot (in Agent mode) or any MCP client can interact with your GIS environment.

---

## Overview
- **ArcGIS Pro Add-In** (C# with ArcGIS Pro SDK): runs *in-process* with ArcGIS Pro and exposes GIS operations through a local IPC channel (Named Pipes).
- **MCP Server** (.NET 8 console app): defines MCP tools, communicates with the Add-In via Named Pipes, and is configured as an MCP server in Visual Studio through `.mcp.json`.

This can allow Copilot (Agent Mode) to query maps, list layers, count features, zoom to layers, and more — directly in ArcGIS Pro.

---

## Prerequisites
- Visual Studio 2022 **17.14 or later** (for MCP Agent Mode support)
- ArcGIS Pro SDK for .NET
- ArcGIS Pro installed (same machine)
- .NET 8 SDK

---

## Solution Structure
```
ArcGisProMcpSample/
+- ArcGisProBridgeAddIn/           # ArcGIS Pro Add-In project (in-process)
¦  +- Config.daml
¦  +- Module.cs
¦  +- ProBridgeService.cs          # Named Pipe server + command handler
¦  +- IpcModels.cs                 # IPC request/response DTOs
+- ArcGisMcpServer/                # MCP server project (.NET 8)
¦  +- Program.cs
¦  +- Tools/ProTools.cs            # MCP tool definitions (bridge client)
¦  +- Ipc/BridgeClient.cs          # Named Pipe client
¦  +- Ipc/IpcModels.cs             # Shared IPC DTOs
+- .mcp.json                       # MCP server manifest for VS Copilot
```

---

## ArcGIS Pro Add-In
The Add-In starts a **Named Pipe server** on ArcGIS Pro launch. It handles operations like:
- `pro.getActiveMapName`
- `pro.listLayers`
- `pro.countFeatures`
- `pro.zoomToLayer`
- `pro.selectByAttribute` — select features matching a SQL WHERE clause
- `pro.getCurrentExtent` — return the active map view's extent + spatial reference
- `pro.exportLayer` — export a layer (optionally filtered) to a feature class or shapefile

### Example: `Module.cs` (in sample is in a button)
```csharp
protected override bool Initialize()
{
    _service = new ProBridgeService("ArcGisProBridgePipe");
    _service.Start();
    return true; // initialization successful
}

protected override bool CanUnload()
{
    _service?.Dispose();
    return true;
}
```

### Example: `ProBridgeService` handler
```csharp
case "pro.countFeatures":
{
    if (req.Args == null ||
        !req.Args.TryGetValue("layer", out string? layerName) ||
        string.IsNullOrWhiteSpace(layerName))
        return new(false, "arg 'layer' required", null);

    int count = await QueuedTask.Run(() =>
    {
        var fl = MapView.Active?.Map?.Layers
            .OfType<FeatureLayer>()
            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
        if (fl == null) return 0;
        using var fc = fl.GetFeatureClass();
        return (int)fc.GetCount();
    });

    return new(true, null, new { count });
}
```

---

## MCP Server (.NET 8)
The MCP server uses the official `ModelContextProtocol` NuGet package.

### `Program.cs`
```csharp
await Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton(new BridgeClient("ArcGisProBridgePipe"));
        services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(ProTools).Assembly);
    })
    .RunConsoleAsync();
```

### Example tool
```csharp
[McpServerToolType]
public static class ProTools
{
    private static BridgeClient _client;
    public static void Configure(BridgeClient client) => _client = client;

    [McpServerTool(Title = "Count features in a layer", Name = "pro.countFeatures")]
    public static async Task<object> CountFeatures(string layer)
    {
        var r = await _client.OpAsync("pro.countFeatures", new() { ["layer"] = layer });
        if (!r.Ok) throw new Exception(r.Error);
        var count = ((System.Text.Json.JsonElement)r.Data).GetProperty("count").GetInt32();
        return new { layer, count };
    }
}
```
---

## `.mcp.json` Manifest
Place in solution root (`.mcp.json`):
```json
{
  "servers": {
    "arcgis": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "McpServer/ArcGisMcpServer/ArcGisMcpServer.csproj"
      ]
    }
  }
}
```
---

## Running in Visual Studio
1. Open the solution in **Visual Studio 2022 (=17.14)**.
2. Ensure ArcGIS Pro is running with the Add-In loaded (so the Named Pipe exists).
3. In VS, open **Copilot Chat Agent Mode**.
4. Copilot reads `.mcp.json` and starts the MCP server.
5. Type in chat:
   - `pro.listLayers` ? returns the layers in the active map
   - `pro.countFeatures layer=Buildings` ? returns the feature count

---



## IPC Retry & Timeout

The MCP server's `BridgeClient` retries failed pipe calls with exponential
backoff and enforces a per-request timeout covering connect + write + read.
Defaults can be overridden with environment variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `ARCGIS_MCP_MAX_RETRIES` | `3` | Retries after the first attempt |
| `ARCGIS_MCP_CONNECT_TIMEOUT_MS` | `5000` | Named-pipe connect timeout |
| `ARCGIS_MCP_REQUEST_TIMEOUT_MS` | `30000` | End-to-end per-request timeout |
| `ARCGIS_MCP_INITIAL_BACKOFF_MS` | `250` | First retry backoff (doubles each retry) |
| `ARCGIS_MCP_MAX_BACKOFF_MS` | `4000` | Cap for the exponential backoff |
| `ARCGIS_MCP_PIPE_NAME` | `ArcGisProBridgePipe` | Named pipe the Add-In is listening on |

---

## Container Image (optional)

The MCP server ships with a `Dockerfile` at
`McpServer/ArcGisMcpServer/Dockerfile`. Build from the repo root:

```bash
docker build -f McpServer/ArcGisMcpServer/Dockerfile -t arcgis-mcp-server .
```

Run with STDIO attached (required by MCP transport):

```bash
docker run --rm -i \
  -e ARCGIS_MCP_PIPE_NAME=ArcGisProBridgePipe \
  arcgis-mcp-server
```

> **IPC note:** Named pipes do not traverse container ↔ host boundaries on
> Linux. The container image is intended for packaging/distribution and for
> Windows-container hosts that share the host's pipe namespace. For local
> development on the same Windows machine as ArcGIS Pro, `dotnet run`
> against `ArcGisMcpServer.csproj` remains the simplest path.

---

## Next Steps
- Add authentication / access control between the MCP server and Add-In.
- Surface more ArcGIS Pro domains as tools (Layout export, Editing, Tasks).
- Stream long-running geoprocessing progress back through the MCP channel.
---


![MCP Server with ArcGIS Pro Add-In](MCPServer_ArcGISAddIn.gif)