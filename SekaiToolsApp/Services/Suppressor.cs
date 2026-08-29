using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SekaiToolsApp.Services;

/// <summary>
/// 启动 Suppressor 所需的输入。原 <c>Suppressor</c> 与 <c>SuppressPageModel</c>
/// 通过 singleton 互相耦合，新版改为参数注入：UI 收集完字段后构造一次此对象交给后台。
/// </summary>
public sealed class SuppressorOptions
{
    public required string SourceVideo { get; init; }
    public string SourceSubtitle { get; init; } = string.Empty;
    public required string OutputPath { get; init; }
    public bool UseComplexConfig { get; init; } = true;
    public int Crf { get; init; } = 21;
    public string FfmpegPath { get; init; } = string.Empty;
    public VideoEncoder PreferredEncoder { get; init; } = VideoEncoder.Libx264;
    public bool UseHwAccelDecode { get; init; } = true;

    /// <summary>
    /// 优先纯 ffmpeg 管线，本机残留的 VapourSynth 只作兜底（无 ffmpeg 时）。
    /// SekaiText IPC 引擎必须开：老 SekaiTools 在用户目录装过 VapourSynth 的机器上，
    /// 自动探测会把压制切到我们控制不了的 VSPipe，坏掉时只报
    /// "yuv4mpegpipe … Header too large."，且 VSFilter 烧字幕拿不到随引擎发布的字体。
    /// </summary>
    public bool PreferFfmpegPipeline { get; init; }

    /// <summary>
    /// 视频总帧数，用于进度百分比计算。零值时由 <see cref="Suppressor"/> 自行 probe，
    /// 再缓存到该字段（仅本次启动有效）。
    /// </summary>
    public int SourceFrameCount { get; set; }
}

/// <summary>
/// Suppressor 后台流水线状态回调。线程不固定（来自 Process IO 线程），
/// UI 端需要自行把更新分发回主线程（参考 SubtitlePageView 的 Dispatcher.Post 模式）。
/// </summary>
public sealed class SuppressorCallbacks
{
    /// <summary>从启动成功后回调一次，可用作 UI "已开始" 信号。</summary>
    public Action? OnStarted { get; init; }

    /// <summary>追加一行日志（已经按行切分，不含末尾换行）。</summary>
    public Action<string>? OnLogLine { get; init; }

    /// <summary>替换最后一行日志。Suppressor 在解析到 ffmpeg "frame=… fps=…"
    /// 进度行时使用：连续的进度行只占一行，避免文本框无限增长。</summary>
    public Action<string>? OnProgressLogLine { get; init; }

    /// <summary>当前帧 / 总帧数 / FPS。Suppressor 在解析进度行时回调。</summary>
    public Action<int, int, double>? OnProgress { get; init; }

    /// <summary>整条流水线结束（正常退出 / 失败 / 取消）后回调一次。</summary>
    public Action<SuppressorStopReason, Exception?>? OnFinished { get; init; }
}

public enum SuppressorStopReason
{
    Completed,
    Canceled,
    Failed,
}

/// <summary>有充分证据时可判定的挂起阶段。当前 watchdog 只自动判定起步零产出挂起；
/// 中途挂起必须来自比“进度/文件大小不变”更可靠的正面证据，不能据旁路静默猜测。</summary>
public enum SuppressPipelineHangStage
{
    Startup,
    MidRun,
}

/// <summary>
/// 管线挂起被 watchdog 强制终止。区别于普通报错退出：上层（SuppressHandler）
/// 根据 <see cref="Stage"/> 选择对应的自动恢复路线。
/// </summary>
public sealed class SuppressPipelineHangException(string message, SuppressPipelineHangStage stage) : Exception(message)
{
    public SuppressPipelineHangStage Stage { get; } = stage;
}

/// <summary>
/// 视频压制流水线。
///
/// 优先使用本地 VapourSynth 资源（如果存在），否则自动回退到跨平台的 ffmpeg
/// burn-in 流程。这样 macOS / Linux 不再被 Windows-only 资源锁死。
/// </summary>
public sealed partial class Suppressor : IDisposable
{
    private readonly SuppressorOptions _options;
    private readonly SuppressorCallbacks _callbacks;
    private readonly X264Params _x264Params;

    private SuppressRuntimeDescriptor? _runtime;
    private Process? _vProcess;
    private Process? _fProcess;
    private Task? _pipeTask;
    private Task? _logTask;
    private Task? _vLogTask;
    private Task? _stdoutTask;
    private Task? _progressTask;
    private Task? _frameProbeTask;
    private Task? _completionTask;
    private CancellationTokenSource? _cts;
    // ffmpeg 的 -progress 机读输出走独立临时文件，不再走 stdout pipe。真机实证：
    // 某些 Windows 安全软件/注入环境会让重定向 stdout 与 stderr 的周期输出双双消失，
    // 甚至在长片尾部把管线拖死；普通文件 IO 不经过这条异常管道链路。
    private string? _progressFilePath;
    private int _frameCount;
    private double _fps;
    // 从 ffmpeg 首部 "Duration:" 行解析一次：进度百分比一律按 out_time/总时长 计算(单调、被时长封顶)。
    private double _durationSec;
    // 从视频流 "N fps" 解析一次：仅当 EmguCV 拿不到帧数时，用 时长×帧率 兜底估算总帧数。
    private double _sourceFps;
    // ffprobe 探帧结果：>0=成功；0=未完成/失败（ResolveTotalFrames 用 时长×帧率 兜底）。
    // 探测使用可取消、限时的外部进程，避免 EmguCV/MediaFoundation 永久挂死时泄漏后台线程与 native 状态。
    private volatile int _probedFrameCount;
    // -progress 机读通道收到的行数（watchdog 诊断用）。
    private int _pgLineCount;
    // Only positive frame/out-time advancement counts as startup progress. A
    // malformed line or a single frame=0 block must not permanently disarm the
    // hang watchdog.
    private int _progressEvidence;
    private bool _lastLogLineWasProgress;
    // -progress 机读进度块的累积值（进度文件读取线程独占写入，progress= 行收束上报）。
    private int _pgFrame;
    private double _pgFps;
    private double _pgOutTimeSec = -1;
    // stderr 是否出现过 stats 进度行：出现过则机读 tick 不再合成日志行，免得两路进度行交替刷屏。
    private volatile bool _sawStderrProgress;
    // watchdog 判定管线挂起并强杀了子进程：收尾时以 SuppressPipelineHangException 收场。
    private volatile bool _hangAborted;
    // 挂起的具体判词与阶段，随异常带给上层与日志。
    private volatile string? _hangReason;
    private volatile SuppressPipelineHangStage _hangStage;
    private int _suppressCompletionCallback;
    private int _disposed;

    public Suppressor(SuppressorOptions options, SuppressorCallbacks callbacks)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _x264Params = new X264Params { Crf = options.Crf };
    }

    public static SuppressRuntimeProbe ProbeRuntime(string? ffmpegPathHint = null, bool preferFfmpeg = false)
        => SuppressRuntimeService.Probe(ffmpegPathHint, preferFfmpeg);

    public bool IsRunning => _vProcess is { HasExited: false } || _fProcess is { HasExited: false };

    /// <summary>
    /// 启动一个可用的压制后端并开始跑流水线。
    /// 同步返回，IO 在内部线程跑；调用方拿 <see cref="SuppressorCallbacks.OnFinished"/> 等结束。
    /// </summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(Suppressor));
        if (IsRunning)
            throw new InvalidOperationException("Suppressor 已经在运行。");

        EnsureSourceExists();

        var startupCommitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _runtime = SuppressRuntimeService.Resolve(_options.FfmpegPath, _options.PreferFfmpegPipeline);

            _frameCount = 0;
            _fps = 0;
            _durationSec = 0;
            _sourceFps = 0;
            _probedFrameCount = 0;
            _pgLineCount = 0;
            _progressEvidence = 0;
            _lastLogLineWasProgress = false;
            _pgFrame = 0;
            _pgFps = 0;
            _pgOutTimeSec = -1;
            _sawStderrProgress = false;
            _hangAborted = false;
            _hangReason = null;
            _hangStage = SuppressPipelineHangStage.Startup;
            Volatile.Write(ref _suppressCompletionCallback, 0);
            _cts = new CancellationTokenSource();
            _progressFilePath = Path.Combine(Path.GetTempPath(),
                $"sekai-suppress-progress-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
            StartFrameProbe();

            switch (_runtime.Backend)
            {
                case SuppressBackend.VapourSynth:
                    StartLegacyPipeline();
                    break;
                case SuppressBackend.Ffmpeg:
                    StartFfmpegPipeline();
                    break;
                default:
                    throw new InvalidOperationException($"未知压制后端：{_runtime.Backend}");
            }

            // 先挂上退出监控，再调用外部回调。即使 OnStarted 抛错，catch 也会终止、等待并释放已启动进程。
            _completionTask = Task.Run(() => WaitForExitAsync(startupCommitted.Task));
            StartProgressWatchdog();
            _callbacks.OnStarted?.Invoke();
            startupCommitted.TrySetResult(true);
        }
        catch
        {
            startupCommitted.TrySetResult(false);
            AbortFailedStart();
            throw;
        }
    }

    /// <summary>
    /// 主动停止：终止子进程，等待 IO 任务退出。可重入。
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 已 Dispose 等情况吞掉。
        }

        TryKill(_vProcess);
        TryKill(_fProcess);

        try
        {
            if (_pipeTask != null) await _pipeTask.ConfigureAwait(false);
        }
        catch
        {
            // pipe 任务在被取消/管道关闭时会抛，统一吞。
        }

        try
        {
            if (_logTask != null) await _logTask.ConfigureAwait(false);
        }
        catch
        {
            // 同上。
        }

        try
        {
            if (_vLogTask != null) await _vLogTask.ConfigureAwait(false);
        }
        catch
        {
            // 同上。
        }

        try
        {
            if (_stdoutTask != null)
                await Task.WhenAny(_stdoutTask, Task.Delay(3000)).ConfigureAwait(false);
        }
        catch
        {
            // 同上。
        }

        try
        {
            // 限时等待：进度读取只做文件/管道 IO；取消绝不允许被异常环境拖死。
            if (_progressTask != null)
                await Task.WhenAny(_progressTask, Task.Delay(3000)).ConfigureAwait(false);
        }
        catch
        {
            // 同上。
        }

        var completion = _completionTask;
        if (completion is not null)
        {
            try { await Task.WhenAny(completion, Task.Delay(5000)).ConfigureAwait(false); }
            catch { /* WaitForExitAsync 会自行报告并清理。 */ }
        }
    }

    private void StartLegacyPipeline()
    {
        if (_runtime is null)
            throw new InvalidOperationException("压制运行环境尚未解析。");

        _vProcess = CreateVapourProcess(_runtime.VapourSynthPath!, _runtime.VapourScriptPath!);
        _fProcess = CreateLegacyFfmpegProcess(_runtime.FfmpegPath);

        // 完整命令行进日志：报错时（自动导出的日志里）能直接复现/定位参数问题。
        LogCommandLine(_vProcess, "VSPipe");
        LogCommandLine(_fProcess, "ffmpeg");

        _vProcess.Start();
        _fProcess.Start();
        BoostProcessPriority(_vProcess);
        BoostProcessPriority(_fProcess);

        _pipeTask = Task.Run(() => RunPipe(_cts!.Token));
        _logTask = Task.Run(() => RunLogReader(_cts!.Token));
        _vLogTask = Task.Run(() => RunVapourLogReader(_cts!.Token));
        _stdoutTask = Task.Run(() => RunUnusedStdoutReader(_cts!.Token));
        _progressTask = Task.Run(() => RunProgressReader(_cts!.Token));
    }

    private void StartFfmpegPipeline()
    {
        if (_runtime is null)
            throw new InvalidOperationException("压制运行环境尚未解析。");

        _fProcess = CreateFfmpegOnlyProcess(_runtime.FfmpegPath);
        LogCommandLine(_fProcess, "ffmpeg");
        _fProcess.Start();
        BoostProcessPriority(_fProcess);

        _logTask = Task.Run(() => RunLogReader(_cts!.Token));
        _stdoutTask = Task.Run(() => RunUnusedStdoutReader(_cts!.Token));
        _progressTask = Task.Run(() => RunProgressReader(_cts!.Token));
    }

    private void LogCommandLine(Process process, string name)
    {
        var args = string.Join(' ',
            process.StartInfo.ArgumentList.Select(a =>
                a.Length > 0 && !a.Contains(' ') && !a.Contains('"') ? a : "\"" + a.Replace("\"", "\\\"") + "\""));
        _callbacks.OnLogLine?.Invoke($"[Sekai] {name} 命令行: {process.StartInfo.FileName} {args}");
    }

    private async Task WaitForExitAsync(Task<bool> startupCommitted)
    {
        Exception? failure = null;
        var canceled = false;
        try
        {
            var monitored = new List<Process>(2);
            if (_vProcess != null) monitored.Add(_vProcess);
            if (_fProcess != null) monitored.Add(_fProcess);

            if (monitored.Count > 0)
            {
                var waits = new Task[monitored.Count];
                for (var i = 0; i < monitored.Count; i++)
                    waits[i] = monitored[i].WaitForExitAsync();

                // 等第一个子进程退出。任一子进程（ffmpeg _fProcess 或 VSPipe _vProcess）
                // 异常退出（非 0）时，主动 Kill 仍在跑的另一个，确保它的 WaitForExitAsync
                // 也能返回——否则 ffmpeg 中途死亡后 VSPipe 不回收、管道不关闭会永挂。
                // 正常完成时 VSPipe 先 EOF 退出(0)、ffmpeg 仍在收尾编码，此处不 Kill，
                // 成功路径不受影响。
                var firstDone = await Task.WhenAny(waits).ConfigureAwait(false);
                var firstIdx = Array.IndexOf(waits, firstDone);
                if (firstIdx >= 0 && ExitedWithFailure(monitored[firstIdx]))
                {
                    for (var i = 0; i < monitored.Count; i++)
                        if (i != firstIdx) TryKill(monitored[i]);
                }

                for (var i = 0; i < waits.Length; i++)
                    await waits[i].ConfigureAwait(false);
            }

            if (_logTask != null)
            {
                try { await _logTask.ConfigureAwait(false); }
                catch { /* 已经在 RunLogReader 内吞，外层留底。 */ }
            }

            if (_vLogTask != null)
            {
                try { await _vLogTask.ConfigureAwait(false); }
                catch { /* 同上。 */ }
            }

            if (_pipeTask != null)
            {
                try { await _pipeTask.ConfigureAwait(false); }
                catch { /* 同上。 */ }
            }

            if (_stdoutTask != null)
            {
                try { await Task.WhenAny(_stdoutTask, Task.Delay(3000)).ConfigureAwait(false); }
                catch { /* 同上。 */ }
            }

            if (_progressTask != null)
            {
                // 限时等待（与 StopAsync 同理）：完成上报绝不允许被进度线程拖死，
                // 否则 ffmpeg 都退出了任务还永远显示 running。
                try { await Task.WhenAny(_progressTask, Task.Delay(3000)).ConfigureAwait(false); }
                catch { /* 同上。 */ }
            }

            TryDeleteProgressFile();

            canceled = _cts?.IsCancellationRequested ?? false;
            if (!canceled)
            {
                failure = BuildExitFailure();
                // watchdog 强杀的挂起：退出码信息（Kill 产生的 -1/137）没有诊断价值，
                // 换成带类型标记的挂起异常（判词由 watchdog 在强杀现场写好），上层据此选降级路线。
                if (_hangAborted)
                    failure = new SuppressPipelineHangException(
                        (_hangReason ?? "压制管线挂起：长时间无任何进度输出，已被 watchdog 终止") +
                        (failure != null ? $"（{failure.Message.ReplaceLineEndings(" ")}）" : "。"),
                        _hangStage);
                if (failure != null)
                {
                    _callbacks.OnLogLine?.Invoke($"[Sekai] 压制后端异常退出：{failure.Message}");
                }
                else
                {
                    // 完成时用 ResolveTotalFrames(与运行期同源)：EmguCV 探不到但时长×帧率能兜出总数的
                    // 输入，末尾也能正确补一帧 100%，不会卡在 99.x。
                    var total = ResolveTotalFrames();
                    if (total > 0)
                    {
                        _frameCount = total;
                        _callbacks.OnProgress?.Invoke(_frameCount, total, _fps);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var reason = failure != null ? SuppressorStopReason.Failed
            : canceled ? SuppressorStopReason.Canceled
            : SuppressorStopReason.Completed;

        try
        {
            if (await startupCommitted.ConfigureAwait(false) &&
                Volatile.Read(ref _suppressCompletionCallback) == 0)
                _callbacks.OnFinished?.Invoke(reason, failure);
        }
        finally
        {
            CleanupTerminalState(terminateProcesses: false);
        }
    }

    private Exception? BuildExitFailure()
    {
        var failures = new List<string>();

        AppendExitFailure(_vProcess, "VSPipe", failures);
        AppendExitFailure(_fProcess, "ffmpeg", failures);

        if (failures.Count == 0)
            return null;

        return new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static bool ExitedWithFailure(Process process)
    {
        try
        {
            return process.HasExited && process.ExitCode != 0;
        }
        catch (InvalidOperationException)
        {
            // 进程已释放时读取 ExitCode 可能失败，按"无失败"处理。
            return false;
        }
    }

    private static void AppendExitFailure(Process? process, string name, ICollection<string> failures)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                return;

            if (process.ExitCode != 0)
                failures.Add($"{name} 退出码 {process.ExitCode}。");
        }
        catch (InvalidOperationException)
        {
            // 进程已释放时读取 ExitCode 可能失败，忽略。
        }
    }

    private void RunPipe(CancellationToken token)
    {
        if (_vProcess == null || _fProcess == null) return;
        var src = _vProcess.StandardOutput.BaseStream;
        var dst = _fProcess.StandardInput.BaseStream;

        var buffer = new byte[1 << 16];
        try
        {
            int read;
            while (!token.IsCancellationRequested &&
                   (read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                dst.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 取消 / 进程被 Kill 关闭管道 → 正常路径，吞。
        }
        finally
        {
            try { dst.Close(); }
            catch { /* 已关闭。 */ }
        }
    }

    private void RunLogReader(CancellationToken token)
    {
        if (_fProcess == null) return;
        var stderr = _fProcess.StandardError;
        var sb = new StringBuilder(256);

        try
        {
            // 逐字读、遇 \r 或 \n 立即切段。ffmpeg 的进度行用 \r 原地覆盖(末尾无 \n)，普通日志用 \n。
            // 旧代码 StreamReader.ReadLine() 读到 \r 必须预读一字节判 \r\n——ffmpeg 初始化期(探测/建
            // libass 字体缓存)stderr 长时间静默，这个预读会一直阻塞，导致运行中一条进度都吐不出来
            // (UI 恒显 "帧 0/0 · 0%")，直到进程被 Kill 才把缓冲的最后一条进度冲出。逐字读消除此阻塞，
            // 进度即时可见。
            int ch;
            while (!token.IsCancellationRequested && (ch = stderr.Read()) >= 0)
            {
                var c = (char)ch;
                if (c == '\r' || c == '\n')
                {
                    if (sb.Length > 0)
                    {
                        AnalysisLog(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (sb.Length > 0)
                AnalysisLog(sb.ToString());
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 进程被关闭时 Read 会抛，吞。
        }
    }

    // ffmpeg 的 stderr 周期状态行（frame=… fps=…）在部分环境下根本不输出——真机日志实证：
    // Windows 上 dxva2 硬解 + QSV 硬编、stderr 重定向为管道时，初始化日志行全都在、
    // 周期 stats 一条都没有（只有进程结束时的终报），引擎无从解析 → UI 恒显 0%。
    // 2.3.3 曾用 -progress pipe:1 作冗余，但报告者机器连这条 stdout pipe 也会全程沉默；
    // 2.3.8 改让 -progress 写独立临时文件，这里增量轮询，绕开异常的子进程输出管道。
    // stderr stats 解析仍保留，两路取值同源、互为冗余。
    private void RunProgressReader(CancellationToken token)
    {
        if (_fProcess == null) return;
        if (!string.IsNullOrEmpty(_progressFilePath))
        {
            RunProgressFileReader(_progressFilePath, token);
            return;
        }

        var stdout = _fProcess.StandardOutput;

        try
        {
            // 机读块每行 \n 结尾（与 stats 的 \r 不同），ReadLine 无预读阻塞问题。
            string? line;
            while (!token.IsCancellationRequested && (line = stdout.ReadLine()) != null)
                ConsumeProgressLine(line);
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 进程被关闭时 ReadLine 会抛，吞。
        }
        catch (Exception ex)
        {
            // 读线程绝不允许静默死亡（2.3.3 真机事故：一个异常吞掉整条机读通道却无迹可循）——
            // 留一行日志，导出日志可见。
            try { _callbacks.OnLogLine?.Invoke("[Sekai] 进度读取线程异常退出：" + ex.Message); }
            catch { /* ignored */ }
        }
    }

    /// <summary>stdout 仍重定向并持续排空，防止 ffmpeg/第三方库的意外输出混进父进程
    /// 的 NDJSON stdout；真正的机读进度已走文件，这里不解析内容。</summary>
    private void RunUnusedStdoutReader(CancellationToken token)
    {
        if (_fProcess == null || string.IsNullOrEmpty(_progressFilePath)
            || !_fProcess.StartInfo.RedirectStandardOutput) return;
        try
        {
            var stream = _fProcess.StandardOutput;
            var buffer = new char[1024];
            while (!token.IsCancellationRequested && stream.Read(buffer, 0, buffer.Length) > 0)
            {
                // drain only
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 进程结束/取消时关闭管道是正常路径。
        }
    }

    /// <summary>增量读取 ffmpeg 正在追加的 -progress 文件。每次短暂打开并带
    /// FileShare.ReadWrite，Windows 上不长期占句柄，取消/降级重试也能立即清理文件。</summary>
    private void RunProgressFileReader(string path, CancellationToken token)
    {
        long offset = 0;
        var pending = string.Empty;
        var buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                var readAny = false;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length < offset)
                    {
                        // 理论上 GUID 路径不会被截断；留此守卫防外部清理工具原地重建。
                        offset = 0;
                        pending = string.Empty;
                    }

                    stream.Position = offset;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        readAny = true;
                        offset += read;
                        pending += Encoding.UTF8.GetString(buffer, 0, read);

                        int newline;
                        while ((newline = pending.IndexOf('\n')) >= 0)
                        {
                            ConsumeProgressLine(pending[..newline].TrimEnd('\r'));
                            pending = pending[(newline + 1)..];
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    // ffmpeg 尚未创建进度文件；下一轮再看。
                }
                catch (IOException)
                {
                    // ffmpeg 正在创建/替换文件的极短窗口；下一轮重试。
                }
                catch (UnauthorizedAccessException)
                {
                    // 安全软件短暂扫描/锁定临时文件；下一轮重试。
                }

                if (!IsRunning && !readAny)
                {
                    if (pending.Length > 0) ConsumeProgressLine(pending.TrimEnd('\r'));
                    return;
                }

                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            try { _callbacks.OnLogLine?.Invoke("[Sekai] 进度文件读取线程异常退出：" + ex.Message); }
            catch { /* ignored */ }
        }
    }

    private void ConsumeProgressLine(string line)
    {
        Interlocked.Increment(ref _pgLineCount);
        var eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line[..eq];
        var val = line[(eq + 1)..].Trim();
        if (ProgressValueShowsAdvancement(key, val))
            Interlocked.Exchange(ref _progressEvidence, 1);
        switch (key)
        {
            case "frame":
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                {
                    _pgFrame = f;
                }
                break;
            case "fps":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fp) && fp > 0)
                    _pgFps = fp;
                break;
            case "out_time": // 编码初期为 N/A，解析失败即维持旧值
                if (TryParseFfmpegTime(val, out var sec))
                {
                    _pgOutTimeSec = sec;
                }
                break;
            case "progress": // continue/end 都收束一次上报
                EmitProgressTick();
                break;
        }
    }

    /// <summary>把一个机读进度块折算成 OnProgress 上报，口径与 stderr stats 分支完全一致。
    /// 整体 try/catch：单个 tick 出错只跳过本次上报，绝不让进度读取线程死亡。</summary>
    private void EmitProgressTick()
    {
        try
        {
            if (_pgFps > 0) _fps = _pgFps;

            // 合成日志行先发、且只用机读块的原始值（不依赖总帧数解析）：后续链路无论出什么
            // 状况，stderr 无 stats 的环境导出的日志里都至少留得下真实进度痕迹。
            if (!_sawStderrProgress)
            {
                var t = TimeSpan.FromSeconds(Math.Max(_pgOutTimeSec, 0));
                _callbacks.OnProgressLogLine?.Invoke(
                    $"frame={_pgFrame} fps={_fps:0.0} time={t:hh\\:mm\\:ss\\.ff}");
            }

            var total = ResolveTotalFrames();
            int reported;
            if (_durationSec > 0 && total > 0 && _pgOutTimeSec >= 0)
            {
                var ratio = Math.Clamp(_pgOutTimeSec / _durationSec, 0, 1);
                reported = (int)Math.Round(ratio * total);
            }
            else
            {
                reported = total > 0 ? Math.Min(_pgFrame, total) : _pgFrame;
            }

            _frameCount = reported;
            if (total > 0)
                _callbacks.OnProgress?.Invoke(reported, total, _fps);
        }
        catch
        {
            // 跳过本次 tick，读取线程继续。
        }
    }

    private void RunVapourLogReader(CancellationToken token)
    {
        if (_vProcess == null) return;
        var stderr = _vProcess.StandardError;

        try
        {
            string? line;
            while (!token.IsCancellationRequested && (line = stderr.ReadLine()) != null)
            {
                _callbacks.OnLogLine?.Invoke("[VSPipe] " + line);
                _lastLogLineWasProgress = false;
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 进程被关闭时 ReadLine 会抛，吞。
        }
    }

    private void AnalysisLog(string log)
    {
        var progress = FfmpegProgressPattern().Match(log);
        if (progress.Success)
        {
            // ffmpeg 进度数字始终是 invariant 格式；用 InvariantCulture + TryParse 避免
            // 欧洲逗号区把 "fps=23.5" 解析错，且解析失败时不抛（FormatException 会逃出
            // RunLogReader 的窄 catch 导致挂死）——失败就当普通日志行处理、跳过本次进度更新。
            if (!int.TryParse(progress.Groups["FrameNumber"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var frameNumber) ||
                !double.TryParse(progress.Groups["FramesPerSecond"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var framesPerSecond))
            {
                _callbacks.OnLogLine?.Invoke(log);
                _lastLogLineWasProgress = false;
                return;
            }

            _fps = framesPerSecond;

            // 进度行用 OnProgressLogLine 替换上一行，避免日志窗口被进度刷屏。
            if (_lastLogLineWasProgress)
                _callbacks.OnProgressLogLine?.Invoke(log);
            else
                _callbacks.OnLogLine?.Invoke(log);

            _lastLogLineWasProgress = true;
            _sawStderrProgress = true;
            if (frameNumber > 0) Interlocked.Exchange(ref _progressEvidence, 1);

            var total = ResolveTotalFrames();

            // 百分比一律按 out_time / 总时长 计算：out_time 单调且被总时长封顶。ffmpeg 的 frame= 在进程被
            // Kill(取消)时可能已冲到全片帧数(实测 time=1.34s 却 frame=42334)，直接拿它当分子会假报 100%。
            // 因此优先用 time=，把上报帧数换算成 时间比例×总帧数，既真实又天然夹在 [0,total] 内。
            int reported;
            var timeMatch = FfmpegTimePattern().Match(log);
            if (_durationSec > 0 && total > 0 && timeMatch.Success &&
                TryParseFfmpegTime(timeMatch.Groups["Time"].Value, out var outTimeSec))
            {
                if (outTimeSec > 0) Interlocked.Exchange(ref _progressEvidence, 1);
                var ratio = Math.Clamp(outTimeSec / _durationSec, 0, 1);
                reported = (int)Math.Round(ratio * total);
            }
            else
            {
                // 兜底(总时长/时间未解析到)：退回 ffmpeg 帧数，仍夹在 [0,total] 防越界显示。
                reported = total > 0 ? Math.Min(frameNumber, total) : frameNumber;
            }

            _frameCount = reported;
            if (total > 0)
                _callbacks.OnProgress?.Invoke(reported, total, _fps);

            return;
        }

        // 非进度行：普通日志。顺带一次性抓取总时长 / 源帧率，供上面的百分比与兜底总帧数使用。
        if (_durationSec <= 0)
        {
            var dur = FfmpegDurationPattern().Match(log);
            if (dur.Success &&
                int.TryParse(dur.Groups["H"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
                int.TryParse(dur.Groups["M"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) &&
                double.TryParse(dur.Groups["S"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                _durationSec = h * 3600 + m * 60 + s;
        }

        if (_sourceFps <= 0)
        {
            var vfps = FfmpegVideoFpsPattern().Match(log);
            if (vfps.Success &&
                double.TryParse(vfps.Groups["Fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                _sourceFps = f;
        }

        _callbacks.OnLogLine?.Invoke(log);
        _lastLogLineWasProgress = false;
    }

    /// <summary>总帧数(百分比分母)，非阻塞：优先 EmguCV 后台探测结果，没出结果/失败时用
    /// 时长×源帧率 兜底，二者皆无返回 0(调用方跳过上报)。禁止在此做任何可能阻塞的调用。</summary>
    private int ResolveTotalFrames()
    {
        if (_options.SourceFrameCount > 0) return _options.SourceFrameCount;
        var probed = _probedFrameCount;
        if (probed > 0) return probed;
        if (_durationSec > 0 && _sourceFps > 0) return (int)Math.Round(_durationSec * _sourceFps);
        return 0;
    }

    /// <summary>用 ffprobe 在独立子进程中读取容器帧数。探测限时十秒，取消、超时或异常时
    /// 强制回收整个进程树；不再使用可能永久挂死且无法取消的 EmguCV native 线程。</summary>
    private void StartFrameProbe()
    {
        if (_options.SourceFrameCount > 0)
        {
            _probedFrameCount = _options.SourceFrameCount;
            return;
        }

        var ffprobe = _runtime?.FfprobePath;
        var token = _cts?.Token ?? CancellationToken.None;
        if (string.IsNullOrWhiteSpace(ffprobe) || string.IsNullOrWhiteSpace(_options.SourceVideo)) return;

        _frameProbeTask = Task.Run(() => ProbeFrameCountAsync(ffprobe, _options.SourceVideo, token));
    }

    private async Task ProbeFrameCountAsync(string ffprobePath, string videoPath, CancellationToken token)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=nb_frames,avg_frame_rate,duration:format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(videoPath);

            process = Process.Start(startInfo);
            if (process is null) return;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* 已强杀，句柄稍后统一释放。 */ }
                return;
            }

            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || token.IsCancellationRequested) return;

            var probed = ParseFrameProbe(output);
            if (probed <= 0) return;

            // 回填 options：软编自动重试(SuppressHandler)复用同一份探测结果。
            _options.SourceFrameCount = probed;
            _probedFrameCount = probed;
        }
        catch
        {
            TryKill(process);
            // 探测失败 → 运行时继续用 ffmpeg 时长×帧率兜底，压制本身不受影响。
        }
        finally
        {
            TryKill(process);
            process?.Dispose();
        }
    }

    internal static int ParseFrameProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
                return 0;

            var stream = streams[0];
            if (stream.TryGetProperty("nb_frames", out var frames) &&
                TryReadPositiveDouble(frames, out var frameCount) && frameCount <= int.MaxValue)
                return (int)Math.Round(frameCount);

            if (!stream.TryGetProperty("avg_frame_rate", out var frameRateElement)) return 0;
            var frameRateText = frameRateElement.ValueKind == JsonValueKind.String
                ? frameRateElement.GetString()
                : frameRateElement.GetRawText();
            if (!TryParseRational(frameRateText, out var frameRate)) return 0;

            double duration = 0;
            if (stream.TryGetProperty("duration", out var streamDuration))
                TryReadPositiveDouble(streamDuration, out duration);
            if (duration <= 0 && document.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var formatDuration))
                TryReadPositiveDouble(formatDuration, out duration);
            if (duration <= 0) return 0;

            var estimated = duration * frameRate;
            return estimated is > 0 and <= int.MaxValue ? (int)Math.Round(estimated) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryReadPositiveDouble(JsonElement element, out double value)
    {
        value = 0;
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static bool TryParseRational(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('/');
        if (parts.Length == 1)
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out result) && result > 0;
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            numerator <= 0 || denominator <= 0)
            return false;
        result = numerator / denominator;
        return double.IsFinite(result) && result > 0;
    }

    /// <summary>两路进度(stderr stats / -progress 机读)持续毫无动静时的两级 watchdog：
    /// 第一检查点（默认 12 秒）写诊断行并记录输出文件体积基线；到挂起阈值（默认 25 秒，
    /// SEKAI_SUPPRESS_HANG_ABORT_SECONDS 可调，&lt;=0 关闭强杀只留诊断）仍零输出时先看
    /// 输出文件是否在增长——**在长就绝不杀**（真机实证：报告者机器上 ffmpeg 在重定向管道下
    /// stats 与 -progress 双双沉默但编码以近实时速度健康推进，疑似安全软件吞掉子进程管道
    /// 输出），转入静默模式按输出体积粗估进度；只有零输出且文件不长的才按起步挂起强杀
    /// （SuppressPipelineHangException 收场让上层降级重试）。
    /// 出现过任何一行进度输出后 watchdog 全程退场（中途变慢/磁盘等待不属于起步挂起）。</summary>
    private void StartProgressWatchdog()
    {
        var token = _cts!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var abortSeconds = 25;
                var env = Environment.GetEnvironmentVariable("SEKAI_SUPPRESS_HANG_ABORT_SECONDS");
                if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var custom)) abortSeconds = custom;

                // 强杀阈值被调小（自测）时，第一检查点取其一半，保证有增长观察窗。
                var diagSeconds = Math.Min(12, abortSeconds <= 0 ? 12 : Math.Max(1, abortSeconds / 2));
                await Task.Delay(TimeSpan.FromSeconds(diagSeconds), token).ConfigureAwait(false);
                if (HasProgressOutput() || !IsRunning) return;
                var baselineSize = OutputFileSize();
                if (diagSeconds >= 12)
                    _callbacks.OnLogLine?.Invoke(
                        "[Sekai] 诊断：启动 12 秒未收到任何进度输出（stderr 状态行与 -progress 机读文件均无）——请导出此日志反馈。");

                if (abortSeconds <= 0) return;
                if (abortSeconds > diagSeconds)
                {
                    await Task.Delay(TimeSpan.FromSeconds(abortSeconds - diagSeconds), token).ConfigureAwait(false);
                    if (HasProgressOutput() || !IsRunning) return;
                }

                // 裁决点：输出文件在长 → 压制活着只是哑巴，转静默模式；不长才是真挂起。
                var verdictSize = OutputFileSize();
                if (verdictSize - baselineSize > SilentAliveGrowthBytes)
                {
                    await RunSilentModeAsync(verdictSize, token).ConfigureAwait(false);
                    return;
                }

                _hangStage = SuppressPipelineHangStage.Startup;
                _hangReason = "压制管线挂起：启动后长时间无任何进度输出且输出文件无增长，已被 watchdog 终止";
                _hangAborted = true;
                _callbacks.OnLogLine?.Invoke(
                    $"[Sekai] 启动 {abortSeconds} 秒无任何进度输出且输出文件无增长：疑似解码/编码管线挂起，已自动终止压制进程。");
                TryKill(_vProcess);
                TryKill(_fProcess);
            }
            catch
            {
                // 取消/Dispose → 直接退出。
            }
        });
    }

    // 静默模式判活阈值：mp4 头部/缓冲噪音在 KB 级，超过 64KB 的增长必然是媒体包在落盘。
    private const long SilentAliveGrowthBytes = 64 * 1024;

    /// <summary>静默模式：进度通道全哑但输出文件在增长——绝不终止，改按 输出体积/源体积
    /// 粗估进度（CRF 模式成品体积未知，只能估算并封顶 99%，完成时由收尾路径补 100%）。
    /// 文件长时间（默认 120 秒，SEKAI_SUPPRESS_SILENT_STALL_SECONDS 可调）不增长时只告警、
    /// 不自动终止。MP4 写入、系统缓冲以及被安全软件拦截的进度通道都可能造成长时间“假静止”；
    /// 一旦已经确认产生有效媒体数据，仅凭这些旁路信号无法安全证明编码器真的挂死。
    /// 通道恢复输出则静默模式解除，用户也始终可以主动取消。</summary>
    private async Task RunSilentModeAsync(long lastSize, CancellationToken token)
    {
        var stallSeconds = 120;
        var env = Environment.GetEnvironmentVariable("SEKAI_SUPPRESS_SILENT_STALL_SECONDS");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var custom) && custom > 0) stallSeconds = custom;

        _callbacks.OnLogLine?.Invoke(
            "[Sekai] watchdog 静默模式：stderr 状态行与机读进度文件均无更新，但输出文件正在增长——" +
            "判定压制仍在进行，不终止。进度改按输出文件体积粗估。此情况可能由安全软件或异常运行环境" +
            "拦截 ffmpeg 状态输出导致，可将引擎目录加入其白名单以恢复正常进度显示。");

        long sourceSize = 0;
        try
        {
            sourceSize = new FileInfo(_options.SourceVideo).Length;
        }
        catch
        {
            // 拿不到源体积就不合成百分比，只播报输出体积。
        }

        var stallWatch = Stopwatch.StartNew();
        var stallWarningSent = false;
        while (!token.IsCancellationRequested && IsRunning)
        {
            await Task.Delay(2000, token).ConfigureAwait(false);
            if (HasProgressOutput())
            {
                _callbacks.OnLogLine?.Invoke("[Sekai] watchdog 静默模式解除：进度通道恢复输出。");
                return;
            }

            var size = OutputFileSize();
            var fileGrew = size > lastSize;
            if (fileGrew)
            {
                lastSize = size;
                stallWatch.Restart();
                stallWarningSent = false;
            }
            else if (!stallWarningSent && stallWatch.Elapsed.TotalSeconds > stallSeconds)
            {
                stallWarningSent = true;
                _callbacks.OnLogLine?.Invoke(
                    $"[Sekai] watchdog：输出文件已 {stallSeconds} 秒无增长且进度通道仍无输出；" +
                    "此前已确认产生有效媒体数据，无法安全判定编码器挂死，因此继续等待而不误杀。可随时手动取消。");
            }

            var total = ResolveTotalFrames();
            if (sourceSize > 0 && total > 0)
            {
                var ratio = Math.Clamp((double)lastSize / sourceSize, 0, 0.99);
                var estimated = (int)(ratio * total);
                _frameCount = estimated;
                _callbacks.OnProgress?.Invoke(estimated, total, 0);
            }

            var activity = stallWarningSent ? " · 暂无新落盘，继续等待" : "";
            _callbacks.OnProgressLogLine?.Invoke(
                $"[Sekai] 静默压制中：输出 {lastSize / 1048576.0:0.0} MB{activity}");
        }
    }

    private long OutputFileSize()
    {
        try
        {
            var info = new FileInfo(_options.OutputPath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool HasProgressOutput()
        => Volatile.Read(ref _progressEvidence) != 0;

    internal static bool ProgressValueShowsAdvancement(string key, string value)
    {
        if (key == "frame")
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)
                   && frame > 0;
        if (key == "out_time")
            return TryParseFfmpegTime(value, out var seconds) && seconds > 0;
        return false;
    }

    /// <summary>解析 ffmpeg 的 HH:MM:SS(.ms) 时间为秒。失败返回 false（调用方自行走兜底）。</summary>
    private static bool TryParseFfmpegTime(string value, out double seconds)
    {
        seconds = 0;
        var parts = value.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return false;
        seconds = h * 3600 + m * 60 + s;
        return true;
    }

    private Process CreateVapourProcess(string vapourExecutable, string vapourScript)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = vapourExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                // VSPipe 启动失败（脚本报错/插件缺失）时 stdout 一个字节都不会有，
                // ffmpeg 那头只会报 "yuv4mpegpipe … Header too large."——真正的原因
                // 在 VSPipe 的 stderr 里，必须捕获转发进日志（RunVapourLogReader）。
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        process.StartInfo.ArgumentList.Add(vapourScript);
        process.StartInfo.ArgumentList.Add("-");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("y4m");
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add($"source={_options.SourceVideo}");
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add($"subtitle={_options.SourceSubtitle}");

        return process;
    }

    private Process CreateLegacyFfmpegProcess(string ffmpegPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                // stdout 只作隔离排空；-progress 写独立临时文件，由 RunProgressReader 轮询。
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        // 后台/API 进程必须禁用 ffmpeg 的控制台读键线程。否则它会读取引擎继承来的
        // NDJSON stdin（日志中的 "Press [q]"），与主进程争抢控制管道并可能中途挂起。
        process.StartInfo.ArgumentList.Add("-nostdin");
        process.StartInfo.ArgumentList.Add("-progress");
        process.StartInfo.ArgumentList.Add(_progressFilePath ?? "pipe:1");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("yuv4mpegpipe");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add("-");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(_options.SourceVideo);
        process.StartInfo.ArgumentList.Add("-map");
        process.StartInfo.ArgumentList.Add("0:v:0");
        process.StartInfo.ArgumentList.Add("-map");
        process.StartInfo.ArgumentList.Add("1:a?");

        AddEncoderArgs(process.StartInfo.ArgumentList);

        process.StartInfo.ArgumentList.Add("-c:a");
        process.StartInfo.ArgumentList.Add("copy");
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add(_options.OutputPath);

        return process;
    }

    private Process CreateFfmpegOnlyProcess(string ffmpegPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-nostdin");
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-threads");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("-progress");
        process.StartInfo.ArgumentList.Add(_progressFilePath ?? "pipe:1");

        AddHwAccelDecodeArgs(process.StartInfo.ArgumentList);

        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(_options.SourceVideo);

        var subtitleFilter = BuildSubtitleFilter();
        if (subtitleFilter is not null)
        {
            process.StartInfo.ArgumentList.Add("-filter_threads");
            process.StartInfo.ArgumentList.Add("0");
            process.StartInfo.ArgumentList.Add("-vf");
            process.StartInfo.ArgumentList.Add(subtitleFilter);
        }

        process.StartInfo.ArgumentList.Add("-map");
        process.StartInfo.ArgumentList.Add("0:v:0");
        process.StartInfo.ArgumentList.Add("-map");
        process.StartInfo.ArgumentList.Add("0:a?");

        AddEncoderArgs(process.StartInfo.ArgumentList);

        process.StartInfo.ArgumentList.Add("-c:a");
        process.StartInfo.ArgumentList.Add("copy");
        process.StartInfo.ArgumentList.Add("-max_muxing_queue_size");
        process.StartInfo.ArgumentList.Add("1024");
        process.StartInfo.ArgumentList.Add(_options.OutputPath);

        return process;
    }

    private string? BuildSubtitleFilter()
    {
        if (string.IsNullOrWhiteSpace(_options.SourceSubtitle))
            return null;

        if (!File.Exists(_options.SourceSubtitle))
            return null;

        var escaped = EscapeFfmpegFilterValue(Path.GetFullPath(_options.SourceSubtitle));
        var filter = $"subtitles=filename={escaped}";

        // 字幕样式默认引用思源黑体（"思源黑体 CN Bold"/"思源黑体 Medium"），但用户机器上
        // 往往没装（macOS 不自带）：libass 找不到家族名会静默回退系统兜底字体（macOS 上
        // 是苹方 Regular），成品字幕整体变窄变细且丢失字重，还没有任何报错。
        // 因此把字体随引擎一起发布（可执行文件旁的 fonts/），压制时交给 libass 检索；
        // 系统已装同名字体时结果不变。目录不存在（如自定义部署）则保持旧行为。
        var fontsDir = BundledFontsDir();
        if (fontsDir is not null)
            filter += $":fontsdir={EscapeFfmpegFilterValue(fontsDir)}";

        return filter;
    }

    /// <summary>随应用/引擎发布的字幕字体目录（存在且非空才返回）。字体体检复用。</summary>
    internal static string? BundledFontsDir()
    {
        var baseDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
        var dir = Path.Combine(baseDir, "fonts");
        try
        {
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any())
                return dir;
        }
        catch
        {
            // 探测失败不影响压制，退回不带 fontsdir 的旧行为。
        }

        return null;
    }

    /// <summary>
    /// 为 ffmpeg 滤镜图里的 <c>subtitles=filename=…</c> 值做转义。
    ///
    /// libavfilter 解析滤镜串有两层转义：先是整条滤镜描述（特殊字符 <c>[ ] , ;</c> 与转义符
    /// <c>\ '</c>），再是单个选项值（分隔符 <c>:</c> 与转义符 <c>\ '</c>）。单层用单引号包裹
    /// 无法同时穿过两层——尤其撇号 <c>'</c> 在内层会被当作引号提前闭合，导致路径被截断、
    /// libass 报 "Unable to open" 整条压制失败（exit 254）。因此这里不再包裹引号，而是按字符
    /// 同时为两层做转义。各分支的字节数都已用打包同款 ffmpeg 实测过含
    /// 撇号 / 空格 / 方括号 / 逗号 / 分号 / 等号 / 中文 的路径。
    /// </summary>
    internal static string EscapeFfmpegFilterValue(string value)
    {
        var normalized = value.Replace('\\', '/');
        var builder = new StringBuilder(normalized.Length * 2);
        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case '\'':
                    // 两层都把 ' 当转义/引号字符：内层需 \' ，外层再把这两个字符转义
                    // → 最终字节为 \\\' （三反斜杠 + 撇号）。
                    builder.Append("\\\\\\'");
                    break;
                case ':':
                    // : 仅是内层（选项值）的分隔符；外层把 \ 转义后交给内层 → \\: 。
                    builder.Append("\\\\:");
                    break;
                case ',':
                case ';':
                case '[':
                case ']':
                    // 仅是外层（滤镜描述）的特殊字符，外层单反斜杠转义即可。
                    builder.Append('\\');
                    builder.Append(ch);
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    private void AddHwAccelDecodeArgs(IList<string> args)
    {
        if (!_options.UseHwAccelDecode) return;

        var hasSubtitle = !string.IsNullOrWhiteSpace(_options.SourceSubtitle)
                          && File.Exists(_options.SourceSubtitle);

        if (OperatingSystem.IsMacOS())
        {
            // 带字幕压制时，libass 滤镜在 CPU 空间执行。若此时启用 videotoolbox 硬解，
            // 每帧都必须从 GPU VRAM 同步拉回系统内存，反而形成 IO 堵塞并拉低帧率；
            // 此时直走多线程 CPU 解码 -> libass 渲染 -> VideoToolbox 硬件编码器，实测速度快 30%~50%。
            if (!hasSubtitle)
            {
                args.Add("-hwaccel");
                args.Add("videotoolbox");
            }
        }
        else if (_options.PreferredEncoder is VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc or VideoEncoder.Av1Nvenc)
        {
            args.Add("-hwaccel");
            args.Add("cuda");
            if (!hasSubtitle)
            {
                args.Add("-hwaccel_output_format");
                args.Add("cuda");
            }
        }
        else
        {
            args.Add("-hwaccel");
            args.Add("auto");
        }
    }

    private void AddEncoderArgs(IList<string> args)
    {
        switch (_options.PreferredEncoder)
        {
            case VideoEncoder.H264VideoToolbox:
                args.Add("-c:v");
                args.Add("h264_videotoolbox");
                args.Add("-q:v");
                args.Add("65");
                args.Add("-prio_speed");
                args.Add("1");
                args.Add("-realtime");
                args.Add("0");
                args.Add("-profile:v");
                args.Add("high");
                args.Add("-allow_sw");
                args.Add("1");
                break;
            case VideoEncoder.HevcVideoToolbox:
                args.Add("-c:v");
                args.Add("hevc_videotoolbox");
                args.Add("-q:v");
                args.Add("65");
                args.Add("-prio_speed");
                args.Add("1");
                args.Add("-realtime");
                args.Add("0");
                args.Add("-allow_sw");
                args.Add("1");
                args.Add("-tag:v");
                args.Add("hvc1");
                break;
            // NVENC 三兄弟统一走恒定质量：-rc vbr -cq N 必须配 -b:v 0，否则 ffmpeg
            // 默认 200k 平均码率会当作 vbr 目标把画面压糊。p4/hq 是离线成片档；
            // 旧的 p1+ull 是直播延迟档（禁 B 帧），成品质量差一截。
            case VideoEncoder.H264Nvenc:
                args.Add("-c:v");
                args.Add("h264_nvenc");
                args.Add("-preset");
                args.Add("p4");
                args.Add("-tune");
                args.Add("hq");
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-cq");
                args.Add(_options.Crf.ToString());
                args.Add("-b:v");
                args.Add("0");
                args.Add("-profile:v");
                args.Add("high");
                args.Add("-multipass");
                args.Add("0");
                break;
            case VideoEncoder.HevcNvenc:
                args.Add("-c:v");
                args.Add("hevc_nvenc");
                args.Add("-preset");
                args.Add("p4");
                args.Add("-tune");
                args.Add("hq");
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-cq");
                args.Add(_options.Crf.ToString());
                args.Add("-b:v");
                args.Add("0");
                args.Add("-multipass");
                args.Add("0");
                args.Add("-tag:v");
                args.Add("hvc1");
                break;
            case VideoEncoder.H264Qsv:
                args.Add("-c:v");
                args.Add("h264_qsv");
                args.Add("-global_quality");
                args.Add(_options.Crf.ToString());
                break;
            case VideoEncoder.HevcQsv:
                args.Add("-c:v");
                args.Add("hevc_qsv");
                args.Add("-global_quality");
                args.Add(_options.Crf.ToString());
                args.Add("-tag:v");
                args.Add("hvc1");
                break;
            case VideoEncoder.Libx265:
                args.Add("-c:v");
                args.Add("libx265");
                args.Add("-crf");
                args.Add(_options.Crf.ToString());
                args.Add("-preset");
                args.Add("medium");
                args.Add("-tag:v");
                args.Add("hvc1");
                break;
            case VideoEncoder.Av1Nvenc:
                args.Add("-c:v");
                args.Add("av1_nvenc");
                args.Add("-preset");
                args.Add("p4");
                args.Add("-tune");
                args.Add("hq");
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-cq");
                args.Add(_options.Crf.ToString());
                args.Add("-b:v");
                args.Add("0");
                args.Add("-multipass");
                args.Add("0");
                break;
            case VideoEncoder.Av1Qsv:
                args.Add("-c:v");
                args.Add("av1_qsv");
                args.Add("-global_quality");
                args.Add(_options.Crf.ToString());
                break;
            // AMF（AMD 显卡）统一 CQP：恒定 QP 不受码率参数影响，是 AMF 各代驱动上
            // 行为最稳的恒质量方式。h264/hevc 的 QP 与 CRF 同为 0-51 标度可直用。
            case VideoEncoder.H264Amf:
                args.Add("-c:v");
                args.Add("h264_amf");
                args.Add("-quality");
                args.Add("quality");
                args.Add("-rc");
                args.Add("cqp");
                args.Add("-qp_i");
                args.Add(_options.Crf.ToString());
                args.Add("-qp_p");
                args.Add(_options.Crf.ToString());
                args.Add("-qp_b");
                args.Add(_options.Crf.ToString());
                break;
            case VideoEncoder.HevcAmf:
                args.Add("-c:v");
                args.Add("hevc_amf");
                args.Add("-quality");
                args.Add("quality");
                args.Add("-rc");
                args.Add("cqp");
                args.Add("-qp_i");
                args.Add(_options.Crf.ToString());
                args.Add("-qp_p");
                args.Add(_options.Crf.ToString());
                args.Add("-tag:v");
                args.Add("hvc1");
                break;
            case VideoEncoder.Av1Amf:
            {
                // av1_amf 的 QP 是 0-255 标度（h264/hevc 是 0-51），CRF 值按 ×4 映射，
                // 封顶 255；21 → 84 与 hevc CRF21 的观感大致相当。
                var av1Qp = Math.Min(255, Math.Max(0, _options.Crf) * 4).ToString();
                args.Add("-c:v");
                args.Add("av1_amf");
                args.Add("-quality");
                args.Add("quality");
                args.Add("-rc");
                args.Add("cqp");
                args.Add("-qp_i");
                args.Add(av1Qp);
                args.Add("-qp_p");
                args.Add(av1Qp);
                break;
            }
            case VideoEncoder.LibSvtAv1:
                args.Add("-c:v");
                args.Add("libsvtav1");
                args.Add("-crf");
                args.Add(_options.Crf.ToString());
                args.Add("-preset");
                args.Add("8");
                break;
            default:
                var x264 = _options.UseComplexConfig
                    ? _x264Params.GetX264Params()
                    : _x264Params.GetSimpleX264Params();
                args.Add("-c:v");
                args.Add("libx264");
                args.Add("-x264-params");
                args.Add(x264);
                break;
        }
    }

    private static void BoostProcessPriority(Process? p)
    {
        if (p == null) return;
        try
        {
            p.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch { /* 权限不足时忽略 */ }
    }

    private static void TryKill(Process? p)
    {
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已退出 / 句柄失效 → 忽略。
        }
    }

    private void AbortFailedStart()
    {
        CleanupTerminalState(terminateProcesses: true);
    }

    private void CleanupTerminalState(bool terminateProcesses)
    {
        var cancellation = Interlocked.Exchange(ref _cts, null);
        try { cancellation?.Cancel(); } catch { /* ignored */ }

        var vapour = Interlocked.Exchange(ref _vProcess, null);
        var ffmpeg = Interlocked.Exchange(ref _fProcess, null);
        TerminateWaitAndDispose(vapour, terminateProcesses);
        TerminateWaitAndDispose(ffmpeg, terminateProcesses);

        var frameProbe = Interlocked.Exchange(ref _frameProbeTask, null);
        if (frameProbe is not null && !frameProbe.IsCompleted)
        {
            try { frameProbe.Wait(TimeSpan.FromSeconds(3)); }
            catch { /* 探测失败/取消不影响主任务收尾。 */ }
        }

        try { cancellation?.Dispose(); } catch { /* ignored */ }
        TryDeleteProgressFile();

        _runtime = null;
        _pipeTask = null;
        _logTask = null;
        _vLogTask = null;
        _stdoutTask = null;
        _progressTask = null;
        _completionTask = null;
    }

    private static void TerminateWaitAndDispose(Process? process, bool terminate)
    {
        if (process is null) return;
        try
        {
            if (terminate || !process.HasExited)
                TryKill(process);
            if (!process.HasExited)
                process.WaitForExit(5000);
        }
        catch
        {
            // 未启动、已释放或系统拒绝等待；Dispose 仍会执行。
        }
        finally
        {
            try { process.Dispose(); } catch { /* ignored */ }
        }
    }

    private void TryDeleteProgressFile()
    {
        var path = _progressFilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.Delete(path);
            _progressFilePath = null;
        }
        catch
        {
            // 进程刚被 Kill 时 Windows 可能仍短暂持有句柄；Dispose/收尾会再试，
            // 最坏只在系统临时目录留一个很小的文本文件，不影响成品。
        }
    }

    private void EnsureSourceExists()
    {
        if (string.IsNullOrEmpty(_options.SourceVideo) || !File.Exists(_options.SourceVideo))
            throw new FileNotFoundException("视频文件不存在。", _options.SourceVideo);
        if (!string.IsNullOrEmpty(_options.SourceSubtitle) && !File.Exists(_options.SourceSubtitle))
            throw new FileNotFoundException("字幕文件不存在。", _options.SourceSubtitle);
        var dir = Path.GetDirectoryName(_options.OutputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"输出目录不存在：{dir}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _suppressCompletionCallback, 1);
        CleanupTerminalState(terminateProcesses: true);
    }

    [GeneratedRegex(@"^frame=\s{0,}(?<FrameNumber>\d*)\s+fps=\s{0,}(?<FramesPerSecond>[\d\.]+)")]
    private static partial Regex FfmpegProgressPattern();

    // 进度行里的 out_time：time=HH:MM:SS(.ms)。用于按时间比例算真实百分比。
    [GeneratedRegex(@"\btime=\s*(?<Time>\d+:\d+:\d+(?:\.\d+)?)")]
    private static partial Regex FfmpegTimePattern();

    // 首部 "Duration: HH:MM:SS.ms, ..." —— 总时长(百分比分母的时间基准)。
    [GeneratedRegex(@"Duration:\s*(?<H>\d+):(?<M>\d+):(?<S>\d+(?:\.\d+)?)")]
    private static partial Regex FfmpegDurationPattern();

    // 视频流行里的 "..., 60 fps, ..." —— 源帧率(EmguCV 拿不到帧数时的兜底)。
    [GeneratedRegex(@",\s*(?<Fps>\d+(?:\.\d+)?)\s+fps\b")]
    private static partial Regex FfmpegVideoFpsPattern();
}
