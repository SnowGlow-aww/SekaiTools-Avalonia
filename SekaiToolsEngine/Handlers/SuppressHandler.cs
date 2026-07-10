using System.Linq;
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
            // SekaiText 走 IPC 时永远用自带 ffmpeg 的纯 ffmpeg 管线：老 SekaiTools 在
            // 用户目录残留的 VapourSynth 会被自动探测抢走压制（坏掉时只报
            // "Header too large."，且 VSFilter 拿不到随引擎发布的字幕字体）。
            PreferFfmpegPipeline = true,
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

    private async Task<object?> ProbeAsync(JsonElement? @params)
    {
        var hint = @params?.TryGetProperty("ffmpegPath", out var fp) == true ? fp.GetString() : null;
        // 与 StartAsync 同一偏好（ffmpeg 优先），否则探测报的后端和实际跑的不一致。
        var probe = Suppressor.ProbeRuntime(hint, preferFfmpeg: true);

        // 逐个试编码验证硬件真的在（结果按 ffmpeg 路径缓存，进程内只跑一次）；
        // recommended 按平台挑最优硬编，客户端用它当默认值——Windows 上再也不会
        // 默认到 macOS 专属的 VideoToolbox。
        var encoders = await SuppressRuntimeService.ProbeAvailableEncodersAsync(hint);
        var recommended = SuppressRuntimeService.RecommendEncoder(encoders);

        return new
        {
            available = probe.IsReady,
            message = probe.Message,
            backend = probe.Descriptor?.Backend.ToString(),
            ffmpegPath = probe.Descriptor?.FfmpegPath,
            encoders = encoders.Select(e => e.ToString()).ToArray(),
            recommended = recommended.ToString(),
        };
    }
}
