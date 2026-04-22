using ArcGisMcpServer.Ipc;
using ArcGisMcpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        // Remove all logging providers - stdout is reserved for MCP STDIO transport.
        // .NET's default console logger writes to stdout, which corrupts the JSON stream.
        logging.ClearProviders();
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton(new BridgeClient("ArcGisProBridgePipe"));
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
