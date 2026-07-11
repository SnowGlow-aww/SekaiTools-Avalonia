using System.Linq;
using System.Text.Json;
using SekaiToolsApp.Services;
using SekaiToolsEngine.Ipc;

namespace SekaiToolsEngine.Handlers;

public sealed class SuppressHandler
{
    private readonly IpcTransport _transport;
    private readonly object _gate = new();
    private Suppressor? _suppressor;
    private bool _stopRequested;
    private int _progressFrames;

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

        lock (_gate)
        {
            _stopRequested = false;
            _progressFrames = 0;
            _suppressor?.Dispose();
            _suppressor = new Suppressor(options, MakeCallbacks(options, isRetry: false));
            _suppressor.Start();
        }

        return Task.FromResult<object?>("ok");
    }

    private SuppressorCallbacks MakeCallbacks(SuppressorOptions options, bool isRetry)
    {
        return new SuppressorCallbacks
        {
            OnStarted = () => _transport.SendNotification("suppress.started", null),
            OnLogLine = line => _transport.SendNotification("suppress.log", new { line }),
            OnProgressLogLine = line => _transport.SendNotification("suppress.progressLog", new { line }),
            OnProgress = (frame, total, fps) =>
            {
                if (frame > 0) Interlocked.Exchange(ref _progressFrames, frame);
                _transport.SendNotification("suppress.progress", new { frame, total, fps });
            },
            OnFinished = (reason, ex) =>
            {
                // 硬件编码器"起步即失败"（一帧都没编出来）：典型于并行压制打满显卡
                // 编码会话（AMF 并发 InitDX11 → AVERROR(ENODEV)=-19、NVENC 消费级卡
                // 会话上限 → ENOMEM=-12）或驱动暂时性故障。自动改用 x264 软编 +
                // 关硬解重试一次——宁可慢，不能让任务白白挂掉。已出过帧的失败不重试
                // （问题不在初始化），软编失败也不重试（重跑同样的东西没有意义）。
                if (!isRetry
                    && reason == SuppressorStopReason.Failed
                    && Volatile.Read(ref _progressFrames) == 0
                    && IsHardwareEncoder(options.PreferredEncoder)
                    && TryStartSoftwareRetry(options, ex))
                    return;

                _transport.SendNotification("suppress.finished",
                    new { reason = reason.ToString(), error = ex?.Message });
            },
        };
    }

    private bool TryStartSoftwareRetry(SuppressorOptions failed, Exception? ex)
    {
        var fallback = new SuppressorOptions
        {
            SourceVideo = failed.SourceVideo,
            SourceSubtitle = failed.SourceSubtitle,
            OutputPath = failed.OutputPath,
            UseComplexConfig = failed.UseComplexConfig,
            Crf = failed.Crf,
            FfmpegPath = failed.FfmpegPath,
            PreferredEncoder = VideoEncoder.Libx264,
            UseHwAccelDecode = false,
            PreferFfmpegPipeline = failed.PreferFfmpegPipeline,
            SourceFrameCount = failed.SourceFrameCount,
        };

        lock (_gate)
        {
            if (_stopRequested) return false;
            _transport.SendNotification("suppress.log", new
            {
                line = $"[Sekai] 硬件编码器 {failed.PreferredEncoder} 启动即失败" +
                       $"（{ex?.Message?.ReplaceLineEndings(" ") ?? "未知原因"}）——" +
                       "常见于并行压制占满显卡编码会话；自动改用 x264 软编重试。",
            });
            try
            {
                _suppressor?.Dispose();
                _suppressor = new Suppressor(fallback, MakeCallbacks(fallback, isRetry: true));
                _suppressor.Start();
                return true;
            }
            catch (Exception startEx)
            {
                _transport.SendNotification("suppress.log",
                    new { line = "[Sekai] x264 软编重试启动失败：" + startEx.Message });
                return false;
            }
        }
    }

    private static bool IsHardwareEncoder(VideoEncoder encoder)
        => encoder is not (VideoEncoder.Libx264 or VideoEncoder.Libx265 or VideoEncoder.LibSvtAv1);

    private async Task<object?> StopAsync(JsonElement? @params)
    {
        Suppressor? current;
        lock (_gate)
        {
            _stopRequested = true;
            current = _suppressor;
        }

        if (current != null)
            await current.StopAsync();
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
