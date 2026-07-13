using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APBridgeAddIn
{
    // IpcRequest/IpcResponse are hand-mirrored between this project and
    // McpServer/ArcGisMcpServer/Ipc/IpcModels.cs — there is no shared assembly
    // across the named-pipe boundary. Any field added here MUST be mirrored in
    // McpServer/ArcGisMcpServer/Ipc/IpcModels.cs or it silently vanishes at
    // runtime (deserialized as default/missing on the other side, no compile
    // error).
    public record IpcRequest(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("args")] Dictionary<string, string>? Args
    );


    public record IpcResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("data")] object? Data
    );

}
