using System.Linq;
using System.Text.Json;
using SekaiToolsApp.Services;
using SekaiToolsEngine.Ipc;

namespace SekaiToolsEngine.Handlers;

public sealed class SuppressHandler : IAsyncDisposable
{
    private readonly IpcTransport _transport;
    private readonly object _gate = new();
    private Suppressor? _suppressor;
    private bool _stopRequested;
    private int _progressFrames;
    private long _runGeneration;

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

    private async Task<object?> StartAsync(JsonElement? @params)
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

        // Reserve ownership before any asynchronous diagnostics or teardown. A newer
        // start invalidates every callback from the previous run immediately.
        Suppressor? previous;
        long generation;
        lock (_gate)
        {
            generation = ++_runGeneration;
            _stopRequested = false;
            _progressFrames = 0;
            previous = _suppressor;
            _suppressor = null;
        }

        if (previous != null)
        {
            try { await previous.StopAsync().ConfigureAwait(false); }
            finally { previous.Dispose(); }
        }

        // 环境概览进日志（引擎/系统/CPU/内存/显卡驱动/ffmpeg 版本）：真机故障排查
        // 全靠导出的日志，这里一次给全。每个任务开头打一遍（降级重试不重复）。
        foreach (var line in SystemEnvironmentInfo.DescribeLines())
            SendIfCurrent(generation, "suppress.log", new { line });
        var ffmpegDesc = DescribeFfmpegSafe(options.FfmpegPath);
        if (ffmpegDesc is not null)
            SendIfCurrent(generation, "suppress.log", new { line = "[Sekai] " + ffmpegDesc });

        // 字体子系统体检异步跑（进程内缓存，不阻塞启动）：健康机器亚秒出"正常"，
        // 病机的"检测超时"会赶在挂起 watchdog 裁决前后落进任务日志——导出的日志自带病灶结论。
        _ = Task.Run(async () =>
        {
            try
            {
                var fontCheck = await SuppressRuntimeService.ProbeFontSubsystemAsync(options.FfmpegPath);
                SendIfCurrent(generation, "suppress.log",
                    new { line = "[Sekai] 字体子系统: " + fontCheck.Message });
            }
            catch
            {
                // 体检失败不影响压制。
            }
        });

        var suppressor = new Suppressor(options, MakeCallbacks(options, attempt: 0, generation));
        try
        {
            lock (_gate)
            {
                if (_runGeneration != generation || _stopRequested)
                    throw new OperationCanceledException("压制启动已被更新的请求取消。");

                _suppressor = suppressor;
                suppressor.Start();
            }
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_suppressor, suppressor))
                    _suppressor = null;
                if (_runGeneration == generation)
                {
                    ++_runGeneration;
                    _stopRequested = true;
                }
            }
            suppressor.Dispose();
            throw;
        }

        return "ok";
    }

    private SuppressorCallbacks MakeCallbacks(SuppressorOptions options, int attempt, long generation)
    {
        return new SuppressorCallbacks
        {
            OnStarted = () => SendIfCurrent(generation, "suppress.started", null),
            OnLogLine = line => SendIfCurrent(generation, "suppress.log", new { line }),
            OnProgressLogLine = line => SendIfCurrent(generation, "suppress.progressLog", new { line }),
            OnProgress = (frame, total, fps) =>
            {
                lock (_gate)
                {
                    if (_runGeneration != generation) return;
                    if (frame > 0) _progressFrames = frame;
                    _transport.SendNotification("suppress.progress", new { frame, total, fps });
                }
            },
            OnFinished = (reason, ex) =>
            {
                if (!IsCurrent(generation)) return;

                // 普通失败仍只在起步零帧时降级；保留对“有可靠正面证据的中途挂起”
                // 异常的恢复能力。静默 watchdog 不再仅凭进度/文件大小不变创建该异常，
                // 因为这种旁路信号在真机上会误杀健康的 QSV 与 x264。用户主动取消不重试。
                var midRunHang = ex is SuppressPipelineHangException
                {
                    Stage: SuppressPipelineHangStage.MidRun,
                };
                if (reason == SuppressorStopReason.Failed
                    && (Volatile.Read(ref _progressFrames) == 0 || midRunHang)
                    && TryStartFallback(options, ex, attempt, generation))
                    return;

                SendIfCurrent(generation, "suppress.finished",
                    new { reason = reason.ToString(), error = ex?.Message });
            },
        };
    }

    /// <summary>
    /// 失败自动恢复阶梯（至多两级重试）：
    /// ⓪ 已经实际产出后又被可靠证据判为中途挂起 → 直接切 x264 + 软件解码，
    ///    覆盖未完成输出并从头重跑一次。此时当前硬件组合已经在真实长片负载下死锁，
    ///    仅关硬解再赌一次既浪费整片时间，也未必能绕过硬编驱动问题；
    /// ① 疑似管线挂起（watchdog 强杀）且硬解开着 → 只关硬解、编码器不变。真机实证
    ///    （Windows+QSV 报告者）：EmguCV 探帧与 dxva2 硬件解码全挂死、QSV 试编码
    ///    却通过的机器——解码栈坏了但编码是好的，保住硬编速度；
    /// ② 其余起步失败（硬编报错退出：并行压制打满显卡编码会话 AMF→-19 / NVENC→-12、
    ///    驱动暂时性故障；或关硬解后仍挂起）→ x264 软编 + 软件解码，宁可慢不白挂。
    /// 已是全软还失败 → 不再重试（重跑同样的东西没有意义）。
    /// </summary>
    private bool TryStartFallback(SuppressorOptions failed, Exception? ex, int attempt, long generation)
    {
        if (attempt >= 2) return false;

        var hang = ex as SuppressPipelineHangException;
        SuppressorOptions fallback;
        string logLine;

        if (hang?.Stage == SuppressPipelineHangStage.MidRun)
        {
            // 已经是最保守的全软件路线，再跑同一组合不会改变结果，防止失败循环。
            if (failed.PreferredEncoder == VideoEncoder.Libx264 && !failed.UseHwAccelDecode)
                return false;

            fallback = CloneOptions(failed, VideoEncoder.Libx264, useHwAccelDecode: false);
            logLine = "[Sekai] 压制在实际产出后停止增长——判定当前编解码管线中途挂起。" +
                      "将覆盖未完成输出，并从头改用 x264 软编 + 软件解码自动重试一次。";
        }
        else if (hang != null && failed.UseHwAccelDecode)
        {
            fallback = CloneOptions(failed, failed.PreferredEncoder, useHwAccelDecode: false);
            logLine = "[Sekai] 疑似硬件解码挂起（起步零输出）——自动关闭硬解重试，" +
                      $"编码器保持 {failed.PreferredEncoder}。若每次压制都触发此重试，" +
                      "可在压制选项里直接关闭「硬解」跳过等待。";
        }
        else if (IsHardwareEncoder(failed.PreferredEncoder))
        {
            fallback = CloneOptions(failed, VideoEncoder.Libx264, useHwAccelDecode: false);
            logLine = hang != null
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
            if (_runGeneration != generation || _stopRequested) return false;

            // Each fallback is a distinct owned attempt. Incrementing the generation
            // makes late stdout/progress/finished callbacks from the failed process stale.
            var fallbackGeneration = ++_runGeneration;
            var completed = _suppressor;
            var replacement = new Suppressor(
                fallback,
                MakeCallbacks(fallback, attempt + 1, fallbackGeneration));
            _suppressor = replacement;

            try
            {
                _transport.SendNotification("suppress.log", new { line = logLine });
                // 中途挂起的静默估算进度属于上一轮；必须在新一轮启动前清零，
                // 否则重试若又起步失败，会被误判成“已经出过帧”而挡住后续降级。
                _progressFrames = 0;
                _transport.SendNotification("suppress.progress", new { frame = 0, total = 0, fps = 0.0 });
                replacement.Start();
                // The completed Suppressor performs terminal cleanup immediately after
                // this callback returns. Do not dispose it from inside its own callback.
                return true;
            }
            catch (Exception startEx)
            {
                if (ReferenceEquals(_suppressor, replacement))
                    _suppressor = completed;
                _runGeneration = generation;
                replacement.Dispose();
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

    private bool IsCurrent(long generation)
    {
        lock (_gate)
            return _runGeneration == generation;
    }

    private void SendIfCurrent(long generation, string method, object? payload)
    {
        lock (_gate)
        {
            if (_runGeneration != generation) return;
            _transport.SendNotification(method, payload);
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

    public async ValueTask DisposeAsync()
    {
        Suppressor? current;
        lock (_gate)
        {
            ++_runGeneration;
            _stopRequested = true;
            current = _suppressor;
            _suppressor = null;
        }

        if (current == null) return;
        try
        {
            await current.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            current.Dispose();
        }
    }

    private async Task<object?> ProbeAsync(JsonElement? @params)
    {
        var hint = @params?.TryGetProperty("ffmpegPath", out var fp) == true ? fp.GetString() : null;
        // 与 StartAsync 同一偏好（ffmpeg 优先），否则探测报的后端和实际跑的不一致。
        var probe = Suppressor.ProbeRuntime(hint, preferFfmpeg: true);

        // 逐个试编码验证硬件真的在（结果按 ffmpeg 路径缓存，进程内只跑一次）；
        // recommended 按平台挑最优硬编，客户端用它当默认值——Windows 上再也不会
        // 默认到 macOS 专属的 VideoToolbox。字体子系统体检与试编码并发跑（各自
        // 20s 封顶），probe 总时长不因此变长。
        var fontCheckTask = SuppressRuntimeService.ProbeFontSubsystemAsync(hint);
        var encoderProbe = await SuppressRuntimeService.ProbeEncodersDetailedAsync(hint);
        var recommended = SuppressRuntimeService.RecommendEncoder(encoderProbe.Available);
        var fontCheck = await fontCheckTask;

        return new
        {
            available = probe.IsReady,
            message = probe.Message,
            backend = probe.Descriptor?.Backend.ToString(),
            ffmpegPath = probe.Descriptor?.FfmpegPath,
            encoders = encoderProbe.Available.Select(e => e.ToString()).ToArray(),
            recommended = recommended.ToString(),
            // 未通过试编码的硬件编码器 → 原因摘要（RTX 机器上 NVENC 消失这类
            // "该在却不在"从黑盒变成一句话病因）。
            encoderFailures = encoderProbe.Failures,
            fontCheck = new { status = fontCheck.Status, elapsedMs = fontCheck.ElapsedMs, message = fontCheck.Message },
        };
    }
}
