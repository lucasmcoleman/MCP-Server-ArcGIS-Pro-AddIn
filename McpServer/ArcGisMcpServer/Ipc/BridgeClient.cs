using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ArcGisMcpServer.Ipc
{
    /// <summary>
    /// Options for <see cref="BridgeClient"/>. All timeouts are in milliseconds.
    /// Defaults can be overridden via the constructor or via environment
    /// variables (see <see cref="FromEnvironment"/>).
    /// </summary>
    public sealed class BridgeClientOptions
    {
        public int MaxRetries { get; init; } = 3;
        public int ConnectTimeoutMs { get; init; } = 5000;
        // 120s default covers slow Pro operations like create_project on a fresh
        // template (.gdb init + template copy + UI setup can easily take 60s+).
        // Agents can't show incremental progress, so cutting off mid-operation
        // makes successful work look like a failure.
        public int RequestTimeoutMs { get; init; } = 120000;
        public int InitialBackoffMs { get; init; } = 250;
        public int MaxBackoffMs { get; init; } = 4000;

        /// <summary>
        /// Reads optional overrides from environment variables. Missing or
        /// unparseable values fall back to the built-in defaults.
        /// Variables: ARCGIS_MCP_MAX_RETRIES, ARCGIS_MCP_CONNECT_TIMEOUT_MS,
        /// ARCGIS_MCP_REQUEST_TIMEOUT_MS, ARCGIS_MCP_INITIAL_BACKOFF_MS,
        /// ARCGIS_MCP_MAX_BACKOFF_MS.
        /// </summary>
        public static BridgeClientOptions FromEnvironment() => new()
        {
            MaxRetries        = EnvInt("ARCGIS_MCP_MAX_RETRIES",        3),
            ConnectTimeoutMs  = EnvInt("ARCGIS_MCP_CONNECT_TIMEOUT_MS", 5000),
            RequestTimeoutMs  = EnvInt("ARCGIS_MCP_REQUEST_TIMEOUT_MS", 120000),
            InitialBackoffMs  = EnvInt("ARCGIS_MCP_INITIAL_BACKOFF_MS", 250),
            MaxBackoffMs      = EnvInt("ARCGIS_MCP_MAX_BACKOFF_MS",     4000),
        };

        private static int EnvInt(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out var v) && v >= 0 ? v : fallback;
        }
    }

    public class BridgeClient
    {
        // Resolver is called on every connection attempt so that when Pro restarts
        // (new PID ⇒ new pipe name), subsequent requests pick up the fresh pipe
        // without needing to restart the MCP server. Typical overhead per call is
        // one small JSON read from %LOCALAPPDATA%\ArcGisMcpBridge\ via BridgeDiscovery.
        private readonly Func<string> _pipeNameResolver;
        private readonly BridgeClientOptions _options;

        public BridgeClient(string pipeName)
            : this(() => pipeName, BridgeClientOptions.FromEnvironment()) { }

        public BridgeClient(string pipeName, BridgeClientOptions options)
            : this(() => pipeName, options) { }

        public BridgeClient(Func<string> pipeNameResolver)
            : this(pipeNameResolver, BridgeClientOptions.FromEnvironment()) { }

        public BridgeClient(Func<string> pipeNameResolver, BridgeClientOptions options)
        {
            _pipeNameResolver = pipeNameResolver;
            _options = options;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req,
            CancellationToken ct = default)
        {
            Exception? lastEx = null;

            // attempts = 1 initial try + MaxRetries retries.
            int attempts = Math.Max(1, _options.MaxRetries + 1);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(BackoffForAttempt(attempt), ct);

                // Per-request timeout covers connect + write + read. A hung
                // ArcGIS Pro handler would otherwise block the MCP caller
                // indefinitely, which breaks Copilot Agent Mode UX.
                using var timeoutCts = new CancellationTokenSource(_options.RequestTimeoutMs);
                using var linkedCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    return await SendOnceAsync(req, linkedCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller cancelled — don't retry, propagate.
                    throw;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // A genuine handler timeout won't be resolved by retrying — the
                    // bridge isn't responding within the allotted time, and N more
                    // attempts of the same duration just stalls the agent for minutes.
                    // Return a structured response so FormatResult surfaces it to the
                    // agent immediately instead of the generic MCP error wrapper.
                    return new IpcResponse(false,
                        $"timeout: bridge op '{req.Op}' exceeded {_options.RequestTimeoutMs}ms; " +
                        "the handler started but didn't respond. Check mcp-bridge.log for progress.",
                        null);
                }
                catch (Exception ex)
                {
                    // Transient errors (pipe not yet created after Pro restart,
                    // broken pipe mid-request, connection refused) — retry with
                    // backoff. G7's per-request pipe rediscovery means retries
                    // automatically follow Pro across restarts.
                    lastEx = ex;
                }
            }

            throw new IOException(
                $"bridge unreachable for op '{req.Op}' after {attempts} attempt(s): {lastEx?.Message}",
                lastEx);
        }

        private async Task<IpcResponse> SendOnceAsync(IpcRequest req, CancellationToken ct)
        {
            var pipeName = _pipeNameResolver();
            using var client = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(_options.ConnectTimeoutMs, ct);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };

            await writer.WriteLineAsync(
                JsonSerializer.Serialize(req).AsMemory(), ct);

            var line = await reader.ReadLineAsync(ct);
            if (line is null) throw new IOException("bridge closed without response");

            return JsonSerializer.Deserialize<IpcResponse>(line)
                ?? new IpcResponse(false, "deserialize returned null", null);
        }

        /// <summary>Exponential backoff, capped at <see cref="BridgeClientOptions.MaxBackoffMs"/>.</summary>
        private int BackoffForAttempt(int attempt)
        {
            // attempt is 1-based here (first retry = 1). Delay = initial * 2^(attempt-1).
            long delay = (long)_options.InitialBackoffMs << (attempt - 1);
            if (delay > _options.MaxBackoffMs) delay = _options.MaxBackoffMs;
            return (int)delay;
        }

        public Task<IpcResponse> OpAsync(string op,
            Dictionary<string, string>? args = null, CancellationToken ct = default)
            => SendAsync(new IpcRequest(op, args), ct);
    }
}
