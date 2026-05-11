using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcGisMcpServer.Ipc
{
    public record IpcRequest(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("args")] Dictionary<string, string>? Args
    );

    // Data is JsonElement? (not object?) so the source-gen serializer can
    // round-trip it without reflection. The bridge returns arbitrary JSON
    // for the data field; JsonElement preserves any shape while remaining
    // trim-safe (JsonElement has built-in trim-annotated serialization).
    public record IpcResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("data")] JsonElement? Data
    );

    // Named replacement for the anonymous type previously used in
    // ProTools.FormatResult's failure branch. Anonymous types can't be
    // registered with source-gen, so making this a real record gives the
    // serializer a stable name to bind to.
    public record FormatErrorPayload(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("error")] string Error
    );

    // Compact source-generated context for types that cross the named-pipe
    // IPC boundary. The pipe protocol is line-delimited JSON (one message
    // per line), so any indentation would break framing.
    [JsonSerializable(typeof(IpcRequest))]
    [JsonSerializable(typeof(IpcResponse))]
    [JsonSerializable(typeof(BridgeDiscovery.BridgeEntry))]
    [JsonSerializable(typeof(JsonElement))]
    internal partial class McpJsonContext : JsonSerializerContext { }

    // Pretty-printed context for tool-output JSON returned to the MCP agent.
    // Indentation here is for human-and-LLM readability of tool results,
    // not transport framing.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(FormatErrorPayload))]
    internal partial class IndentedJsonContext : JsonSerializerContext { }
}
