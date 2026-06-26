using System.Text.Json;
using System.Text.Json.Serialization;

namespace SekaiToolsEngine.Ipc;

public sealed class IpcTransport
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();

    public IpcTransport(Stream input, Stream output)
    {
        _reader = new StreamReader(input);
        _writer = new StreamWriter(output) { AutoFlush = true };
    }

    public async Task<IpcRequest?> ReadRequestAsync(CancellationToken ct = default)
    {
        var line = await _reader.ReadLineAsync(ct);
        if (line == null) return null;
        return JsonSerializer.Deserialize<IpcRequest>(line, JsonOpts);
    }

    public void SendResponse(int id, object? result = null, string? error = null)
    {
        var msg = new IpcResponse { Id = id, Result = result, Error = error };
        WriteLine(JsonSerializer.Serialize(msg, JsonOpts));
    }

    public void SendNotification(string method, object? @params = null)
    {
        var msg = new IpcNotification { Method = method, Params = @params };
        WriteLine(JsonSerializer.Serialize(msg, JsonOpts));
    }

    private void WriteLine(string json)
    {
        lock (_writeLock)
        {
            _writer.WriteLine(json);
        }
    }
}
