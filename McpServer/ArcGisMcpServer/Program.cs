using ArcGisMcpServer.Ipc;
using ArcGisMcpServer.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Pipe-name selection policy:
//   1. If ARCGIS_MCP_PIPE_NAME is set, use it verbatim. This is the
//      escape hatch for containers / non-standard deployments / anyone
//      who knows exactly which pipe to hit.
//   2. Otherwise, BridgeDiscovery.Discover is called on every request so the
//      server follows Pro across restarts (new PID ⇒ new pipe name) without
//      needing an MCP server restart. Discovery reads the per-PID registry
//      at %LOCALAPPDATA%\ArcGisMcpBridge\ and picks a live bridge (honoring
//      ARCGIS_PROJECT for project-specific routing), falling back to the
//      legacy "ArcGisProBridgePipe" name when no Add-In has registered yet.
var explicitPipe = Environment.GetEnvironmentVariable("ARCGIS_MCP_PIPE_NAME");
Func<string> pipeNameResolver = string.IsNullOrWhiteSpace(explicitPipe)
    ? BridgeDiscovery.Discover
    : () => explicitPipe!;

// Transport selection. HTTP mode is opt-in via --http or MCP_TRANSPORT=http.
// stdio is the default so existing local clients (Claude Code, etc.) keep
// working unchanged. HTTP mode is for remote clients like M365 Copilot Studio
// that connect via the home-server reverse proxy.
var httpMode = args.Contains("--http", StringComparer.OrdinalIgnoreCase)
    || string.Equals(
        Environment.GetEnvironmentVariable("MCP_TRANSPORT"),
        "http",
        StringComparison.OrdinalIgnoreCase);

if (httpMode)
{
    // HTTP mode: ASP.NET Core hosts the MCP server on Streamable HTTP transport.
    // M365 Copilot Studio (and any other remote MCP client) connects through a
    // reverse proxy that terminates TLS; this server speaks plain HTTP on a
    // private LAN/loopback port.
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSingleton(new BridgeClient(pipeNameResolver));
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<ProTools>();
    builder.Services.AddHostedService<StartupConfigurator>();

    var app = builder.Build();

    // Default to 0.0.0.0:5000 so the reverse proxy on the LAN can reach us.
    // Override with ASPNETCORE_URLS if needed (e.g., 127.0.0.1:5000 to bind
    // loopback-only when running behind a same-host proxy).
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    {
        app.Urls.Add("http://0.0.0.0:5000");
    }

    // Fail-fast if the auth token isn't set. An unauthenticated MCP endpoint
    // exposed over a reverse proxy would let anyone on the internet drive
    // ArcGIS Pro; we'd rather refuse to start than silently expose it.
    var expectedToken = Environment.GetEnvironmentVariable("MCP_AUTH_TOKEN");
    if (string.IsNullOrWhiteSpace(expectedToken))
    {
        throw new InvalidOperationException(
            "MCP_AUTH_TOKEN environment variable must be set when running in HTTP mode. " +
            "Set it to a strong random string and configure the same value as the " +
            "X-Api-Key header in your MCP client (e.g., Copilot Studio).");
    }

    app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        var provided = context.Request.Headers["X-Api-Key"].ToString();
        if (!CryptographicEquals(provided, expectedToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
        await next(context);
    });

    app.MapMcp("/mcp");

    await app.RunAsync();
}
else
{
    // stdio mode (default): existing behavior for local clients invoking the
    // server as a subprocess. stdout is the protocol channel, so all logging
    // providers are removed to avoid corrupting the JSON-RPC stream.
    await Host.CreateDefaultBuilder(args)
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton(new BridgeClient(pipeNameResolver));
            services.AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<ProTools>();
            services.AddHostedService<StartupConfigurator>();
        })
        .RunConsoleAsync();
}

// Constant-time string comparison to avoid timing oracles on the API key.
static bool CryptographicEquals(string a, string b)
{
    if (a.Length != b.Length) return false;
    var diff = 0;
    for (var i = 0; i < a.Length; i++)
    {
        diff |= a[i] ^ b[i];
    }
    return diff == 0;
}

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
