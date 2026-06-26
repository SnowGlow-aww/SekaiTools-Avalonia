using System.Text.Json;
using SekaiToolsApp.Services;
using SekaiToolsEngine.Ipc;

namespace SekaiToolsEngine.Handlers;

public sealed class SuppressHandler
{
    private readonly IpcTransport _transport;
    private Suppressor? _suppressor;

    public SuppressHandler(IpcTransport transport)
    {
        _transport = transport;
    }

    public void Register(Dispatcher dispatcher)
    {
        dispatcher.Register("suppress.start", StartAsync);
        dispatcher.Register("suppress.stop", StopAsync);
        dispatcher.Register("suppress.probe", ProbeAsync);
    }

    private Task<object?> StartAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;

        var options = new SuppressorOptions
        {
            SourceVideo = p.GetProperty("sourceVideo").GetString()!,
            OutputPath = p.GetProperty("outputPath").GetString()!,
            SourceSubtitle = p.TryGetProperty("sourceSubtitle", out var ss) ? ss.GetString() ?? "" : "",
            Crf = p.TryGetProperty("crf", out var crf) ? crf.GetInt32() : 21,
            FfmpegPath = p.TryGetProperty("ffmpegPath", out var ff) ? ff.GetString() ?? "" : "",
            PreferredEncoder = p.TryGetProperty("encoder", out var enc)
                               && enc.ValueKind == JsonValueKind.String
                               && Enum.TryParse<VideoEncoder>(enc.GetString(), ignoreCase: true, out var ve)
                ? ve
                : VideoEncoder.Libx264,
            UseHwAccelDecode = !p.TryGetProperty("useHwAccelDecode", out var hw)
                               || hw.ValueKind != JsonValueKind.False,
        };

        var callbacks = new SuppressorCallbacks
        {
            OnStarted = () => _transport.SendNotification("suppress.started", null),
            OnLogLine = line => _transport.SendNotification("suppress.log", new { line }),
            OnProgressLogLine = line => _transport.SendNotification("suppress.progressLog", new { line }),
            OnProgress = (frame, total, fps) =>
                _transport.SendNotification("suppress.progress", new { frame, total, fps }),
            OnFinished = (reason, ex) =>
                _transport.SendNotification("suppress.finished", new { reason = reason.ToString(), error = ex?.Message }),
        };

        _suppressor?.Dispose();
        _suppressor = new Suppressor(options, callbacks);
        _suppressor.Start();

        return Task.FromResult<object?>("ok");
    }

    private async Task<object?> StopAsync(JsonElement? @params)
    {
        if (_suppressor != null)
            await _suppressor.StopAsync();
        return "ok";
    }

    private Task<object?> ProbeAsync(JsonElement? @params)
    {
        var hint = @params?.TryGetProperty("ffmpegPath", out var fp) == true ? fp.GetString() : null;
        var probe = Suppressor.ProbeRuntime(hint);
        return Task.FromResult<object?>(new
        {
            available = probe.IsReady,
            message = probe.Message,
            backend = probe.Descriptor?.Backend.ToString(),
            ffmpegPath = probe.Descriptor?.FfmpegPath,
        });
    }
}
