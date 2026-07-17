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

        // 环境概览进日志（引擎/系统/CPU/内存/显卡驱动/ffmpeg 版本）：真机故障排查
        // 全靠导出的日志，这里一次给全。每个任务开头打一遍（降级重试不重复）。
        foreach (var line in SystemEnvironmentInfo.DescribeLines())
            _transport.SendNotification("suppress.log", new { line });
        var ffmpegDesc = DescribeFfmpegSafe(options.FfmpegPath);
        if (ffmpegDesc is not null)
            _transport.SendNotification("suppress.log", new { line = "[Sekai] " + ffmpegDesc });

        lock (_gate)
        {
            _stopRequested = false;
            _progressFrames = 0;
            _suppressor?.Dispose();
            _suppressor = new Suppressor(options, MakeCallbacks(options, attempt: 0));
            _suppressor.Start();
        }

        return Task.FromResult<object?>("ok");
    }

    private SuppressorCallbacks MakeCallbacks(SuppressorOptions options, int attempt)
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
                // "起步即失败"（一帧都没编出来）的自动降级阶梯，见 TryStartFallback。
                // 已出过帧的失败不重试（问题不在起步），用户主动取消也不重试。
                if (reason == SuppressorStopReason.Failed
                    && Volatile.Read(ref _progressFrames) == 0
                    && TryStartFallback(options, ex, attempt))
                    return;

                _transport.SendNotification("suppress.finished",
                    new { reason = reason.ToString(), error = ex?.Message });
            },
        };
    }

    /// <summary>
    /// 起步失败降级阶梯（至多两级重试）：
    /// ① 疑似管线挂起（看门狗强杀）且硬解开着 → 只关硬解、编码器不变。真机实证
    ///    （Windows+QSV 报告者）：EmguCV 探帧与 dxva2 硬件解码全挂死、QSV 试编码
    ///    却通过的机器——解码栈坏了但编码是好的，保住硬编速度；
    /// ② 其余起步失败（硬编报错退出：并行压制打满显卡编码会话 AMF→-19 / NVENC→-12、
    ///    驱动暂时性故障；或关硬解后仍挂起）→ x264 软编 + 软件解码，宁可慢不白挂。
    /// 已是全软还失败 → 不再重试（重跑同样的东西没有意义）。
    /// </summary>
    private bool TryStartFallback(SuppressorOptions failed, Exception? ex, int attempt)
    {
        if (attempt >= 2) return false;

        var hang = ex is SuppressPipelineHangException;
        SuppressorOptions fallback;
        string logLine;

        if (hang && failed.UseHwAccelDecode)
        {
            fallback = CloneOptions(failed, failed.PreferredEncoder, useHwAccelDecode: false);
            logLine = "[Sekai] 疑似硬件解码挂起（起步零输出）——自动关闭硬解重试，" +
                      $"编码器保持 {failed.PreferredEncoder}。若每次压制都触发此重试，" +
                      "可在压制选项里直接关闭「硬解」跳过等待。";
        }
        else if (IsHardwareEncoder(failed.PreferredEncoder))
        {
            fallback = CloneOptions(failed, VideoEncoder.Libx264, useHwAccelDecode: false);
            logLine = hang
                ? "[Sekai] 管线仍挂起（起步零输出）——自动改用 x264 软编 + 软件解码重试。"
                : $"[Sekai] 硬件编码器 {failed.PreferredEncoder} 启动即失败" +
                  $"（{ex?.Message?.ReplaceLineEndings(" ") ?? "未知原因"}）——" +
                  "常见于并行压制占满显卡编码会话；自动改用 x264 软编重试。";
        }
        else
        {
            return false;
        }

        lock (_gate)
        {
            if (_stopRequested) return false;
            _transport.SendNotification("suppress.log", new { line = logLine });
            try
            {
                _suppressor?.Dispose();
                _suppressor = new Suppressor(fallback, MakeCallbacks(fallback, attempt + 1));
                _suppressor.Start();
                return true;
            }
            catch (Exception startEx)
            {
                _transport.SendNotification("suppress.log",
                    new { line = "[Sekai] 降级重试启动失败：" + startEx.Message });
                return false;
            }
        }
    }

    private static SuppressorOptions CloneOptions(SuppressorOptions src, VideoEncoder encoder, bool useHwAccelDecode)
        => new()
        {
            SourceVideo = src.SourceVideo,
            SourceSubtitle = src.SourceSubtitle,
            OutputPath = src.OutputPath,
            UseComplexConfig = src.UseComplexConfig,
            Crf = src.Crf,
            FfmpegPath = src.FfmpegPath,
            PreferredEncoder = encoder,
            UseHwAccelDecode = useHwAccelDecode,
            PreferFfmpegPipeline = src.PreferFfmpegPipeline,
            SourceFrameCount = src.SourceFrameCount,
        };

    private static bool IsHardwareEncoder(VideoEncoder encoder)
        => encoder is not (VideoEncoder.Libx264 or VideoEncoder.Libx265 or VideoEncoder.LibSvtAv1);

    /// <summary>ffmpeg 版本行：优先用与压制一致的解析结果（探测缓存过，不重复开销），
    /// 解析失败退回 hint 路径；再失败返回 null（概览缺一行不影响压制）。</summary>
    private static string? DescribeFfmpegSafe(string? hint)
    {
        try
        {
            var resolved = Suppressor.ProbeRuntime(hint, preferFfmpeg: true).Descriptor?.FfmpegPath;
            return SystemEnvironmentInfo.DescribeFfmpeg(resolved ?? hint);
        }
        catch
        {
            return SystemEnvironmentInfo.DescribeFfmpeg(hint);
        }
    }

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
