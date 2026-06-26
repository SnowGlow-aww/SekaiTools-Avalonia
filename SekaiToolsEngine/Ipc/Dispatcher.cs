using System.Text.Json;

namespace SekaiToolsEngine.Ipc;

public sealed class Dispatcher
{
    private readonly Dictionary<string, Func<JsonElement?, Task<object?>>> _handlers = new();

    public void Register(string method, Func<JsonElement?, Task<object?>> handler)
    {
        _handlers[method] = handler;
    }

    public async Task<(object? result, string? error)> DispatchAsync(string method, JsonElement? @params)
    {
        if (!_handlers.TryGetValue(method, out var handler))
            return (null, $"Unknown method: {method}");

        try
        {
            var result = await handler(@params);
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
