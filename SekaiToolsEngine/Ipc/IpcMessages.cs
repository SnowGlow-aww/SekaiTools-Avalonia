using System.Text.Json;
using System.Text.Json.Serialization;

namespace SekaiToolsEngine.Ipc;

public sealed class IpcRequest
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}

public sealed class IpcResponse
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("result")] public object? Result { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class IpcNotification
{
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public object? Params { get; set; }
}
