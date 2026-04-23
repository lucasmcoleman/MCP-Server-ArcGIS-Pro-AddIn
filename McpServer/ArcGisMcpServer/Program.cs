using ArcGisMcpServer.Ipc;
using ArcGisMcpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Pipe-name selection policy:
//   1. If ARCGIS_MCP_PIPE_NAME is set, use it verbatim. This is the
//      escape hatch for containers / non-standard deployments / anyone
//      who knows exactly which pipe to hit.
//   2. Otherwise, BridgeDiscovery.Discover() reads the per-PID registry
//      at %LOCALAPPDATA%\ArcGisMcpBridge\ and picks a live bridge
//      (honoring ARCGIS_PROJECT for project-specific routing), falling
//      back to the legacy "ArcGisProBridgePipe" name when no Add-In has
//      registered yet (back-compat with older Add-In builds).
var explicitPipe = Environment.GetEnvironmentVariable("ARCGIS_MCP_PIPE_NAME");
var pipeName = string.IsNullOrWhiteSpace(explicitPipe)
    ? BridgeDiscovery.Discover()
    : explicitPipe;

await Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        // Remove all logging providers - stdout is reserved for MCP STDIO transport.
        // .NET's default console logger writes to stdout, which corrupts the JSON stream.
        logging.ClearProviders();
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton(new BridgeClient(pipeName));
        services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(ProTools).Assembly);
        services.AddHostedService<StartupConfigurator>();
    })
    .RunConsoleAsync();

public class StartupConfigurator : IHostedService
{
    private readonly BridgeClient _client;
    public StartupConfigurator(BridgeClient client) => _client = client;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ProTools.Configure(_client);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
