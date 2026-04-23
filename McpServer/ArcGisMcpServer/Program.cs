using ArcGisMcpServer.Ipc;
using ArcGisMcpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Discover which Pro instance to talk to (one bridge per Pro process).
// Falls back to the legacy "ArcGisProBridgePipe" name if no bridges are
// registered, so this works against pre-discovery Add-In versions too.
var pipeName = BridgeDiscovery.Discover();

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
