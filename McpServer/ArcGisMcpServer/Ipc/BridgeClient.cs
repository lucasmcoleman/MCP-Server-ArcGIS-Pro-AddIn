using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace ArcGisMcpServer.Ipc
{
    public class BridgeClient
    {
        private readonly string _pipeName;
        private const int MaxRetries = 3;
        private const int ConnectTimeoutMs = 5000;

        public BridgeClient(string pipeName) => _pipeName = pipeName;

        public async Task<IpcResponse> SendAsync(IpcRequest req,
            CancellationToken ct = default)
        {
            Exception? lastEx = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(500 * attempt, ct); // 500ms, 1000ms backoff

                try
                {
                    using var client = new NamedPipeClientStream(".", _pipeName,
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    await client.ConnectAsync(ConnectTimeoutMs, ct);
                    using var reader = new StreamReader(client, Encoding.UTF8,
                        leaveOpen: true);
                    using var writer = new StreamWriter(client, new
                        UTF8Encoding(false), leaveOpen: true)
                        { AutoFlush = true };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(req));
                    var line = await reader.ReadLineAsync();
                    if (line is null) throw new IOException("bridge no response");
                    return JsonSerializer.Deserialize<IpcResponse>(line) ??
                        new(false, "deserialize", null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastEx = ex;
                }
            }
            throw new IOException(
                $"bridge unreachable after {MaxRetries} attempts: {lastEx?.Message}", lastEx);
        }

        public Task<IpcResponse> OpAsync(string op,
            Dictionary<string, string>? args = null, CancellationToken ct = default)
            => SendAsync(new IpcRequest(op, args), ct);
    }
}
