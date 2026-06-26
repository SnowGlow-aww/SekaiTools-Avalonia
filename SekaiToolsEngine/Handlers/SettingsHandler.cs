using System.Text.Json;
using SekaiToolsEngine.Ipc;

namespace SekaiToolsEngine.Handlers;

public sealed class SettingsHandler
{
    public SettingsHandler(IpcTransport transport) { }

    public void Register(Dispatcher dispatcher)
    {
        dispatcher.Register("settings.get", GetAsync);
        dispatcher.Register("settings.set", SetAsync);
    }

    private Task<object?> GetAsync(JsonElement? @params)
    {
        return Task.FromResult<object?>(new { status = "ok" });
    }

    private Task<object?> SetAsync(JsonElement? @params)
    {
        return Task.FromResult<object?>("ok");
    }
}
