using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SekaiToolsCore;

namespace SekaiToolsApp.Services;

public enum SuppressBackend
{
    VapourSynth,
    Ffmpeg,
}

public sealed record SuppressRuntimeDescriptor(
    SuppressBackend Backend,
    string FfmpegPath,
    string? VapourSynthPath = null,
    string? VapourScriptPath = null,
    string? FfprobePath = null);

internal sealed record ExecutableValidation(bool IsValid, string Message, string Output = "");

public sealed record SuppressRuntimeProbe(
    bool IsReady,
    string Message,
    SuppressRuntimeDescriptor? Descriptor = null);

/// <summary>试编码探测的完整结果：可用列表 + 每个未通过的硬件编码器的失败原因
/// （key=VideoEncoder 名，value=原因摘要）。此前只回列表，NVENC 之类
/// "该在却不在"的情况完全是黑盒。</summary>
public sealed record EncoderProbeResult(
    List<VideoEncoder> Available,
    IReadOnlyDictionary<string, string> Failures);

/// <summary>字体子系统体检结果。Status: ok / slow / hung / skipped。</summary>
public sealed record FontSubsystemCheck(string Status, int ElapsedMs, string Message);

public static class SuppressRuntimeService
{
    private static readonly string[] FfmpegExecutableNames =
        OperatingSystem.IsWindows() ? ["ffmpeg.exe", "ffmpeg"] : ["ffmpeg"];

    private static readonly string[] VapourExecutableNames =
        OperatingSystem.IsWindows() ? ["VSPipe.exe"] : ["VSPipe", "vspipe"];

    private static readonly string[] FfprobeExecutableNames =
        OperatingSystem.IsWindows() ? ["ffprobe.exe", "ffprobe"] : ["ffprobe"];

    private static readonly ConcurrentDictionary<string, ExecutableValidation> FfmpegValidationCache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ExecutableValidation> BasicValidationCache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // preferFfmpeg：优先纯 ffmpeg 管线，机器上残留的 VapourSynth 只作兜底。
    // SekaiText 引擎（IPC）必须传 true——用户目录里老 SekaiTools 装的 VSPipe 版本不受
    // 我们控制，坏掉时 VSPipe 无输出、ffmpeg 只会报一句莫名其妙的
    // "yuv4mpegpipe … Header too large."；而且那条管线用 VSFilter 烧字幕，
    // 拿不到我们随引擎发布的字体。Avalonia 桌面版保持默认 false（老行为）。
    public static SuppressRuntimeProbe Probe(string? ffmpegPathHint = null, bool preferFfmpeg = false)
    {
        var diagnostic = string.Empty;
        if (!preferFfmpeg)
        {
            if (TryResolveVapourSynth(ffmpegPathHint, out var legacyDescriptor, out var legacyMessage))
                return new SuppressRuntimeProbe(true, legacyMessage, legacyDescriptor);
            diagnostic = legacyMessage;
        }

        if (TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out var ffmpegMessage))
        {
            return new SuppressRuntimeProbe(true, ffmpegMessage,
                new SuppressRuntimeDescriptor(
                    SuppressBackend.Ffmpeg,
                    ffmpegPath,
                    FfprobePath: ResolveFfprobe(ffmpegPath)));
        }
        diagnostic = ffmpegMessage;

        if (preferFfmpeg)
        {
            if (TryResolveVapourSynth(ffmpegPathHint, out var fallbackDescriptor, out var fallbackMessage))
                return new SuppressRuntimeProbe(true, fallbackMessage, fallbackDescriptor);
            if (!string.IsNullOrWhiteSpace(fallbackMessage)) diagnostic = fallbackMessage;
        }

        return new SuppressRuntimeProbe(false, BuildFailureMessage(diagnostic));
    }

    public static SuppressRuntimeDescriptor Resolve(string? ffmpegPathHint = null, bool preferFfmpeg = false)
    {
        var probe = Probe(ffmpegPathHint, preferFfmpeg);
        if (!probe.IsReady || probe.Descriptor is null)
            throw new FileNotFoundException(probe.Message);

        return probe.Descriptor;
    }

    // 硬件编码器仅"编译进 ffmpeg"不代表能用——Windows 全量构建三家（NVENC/QSV/AMF）
    // 都编进去了，真正可用性取决于插的是哪块显卡。因此每个硬件编码器都用一次
    // 微型试编码（lavfi 黑帧 → -f null）验证驱动真的能初始化，失败的剔除。
    // 结果按 ffmpeg 路径缓存：同一引擎进程内只探测一次。
    private static readonly Dictionary<string, VideoEncoder> HardwareEncoderMap = new()
    {
        ["h264_videotoolbox"] = VideoEncoder.H264VideoToolbox,
        ["hevc_videotoolbox"] = VideoEncoder.HevcVideoToolbox,
        ["h264_nvenc"] = VideoEncoder.H264Nvenc,
        ["hevc_nvenc"] = VideoEncoder.HevcNvenc,
        ["av1_nvenc"] = VideoEncoder.Av1Nvenc,
        ["h264_qsv"] = VideoEncoder.H264Qsv,
        ["hevc_qsv"] = VideoEncoder.HevcQsv,
        ["av1_qsv"] = VideoEncoder.Av1Qsv,
        ["h264_amf"] = VideoEncoder.H264Amf,
        ["hevc_amf"] = VideoEncoder.HevcAmf,
        ["av1_amf"] = VideoEncoder.Av1Amf,
    };

    private static readonly Dictionary<string, VideoEncoder> SoftwareEncoderMap = new()
    {
        ["libx265"] = VideoEncoder.Libx265,
        ["libsvtav1"] = VideoEncoder.LibSvtAv1,
    };

    private static readonly ConcurrentDictionary<string, Task<EncoderProbeResult>> EncoderProbeCache = new();

    public static async Task<List<VideoEncoder>> ProbeAvailableEncodersAsync(string? ffmpegPathHint = null)
        => (await ProbeEncodersDetailedAsync(ffmpegPathHint)).Available;

    public static Task<EncoderProbeResult> ProbeEncodersDetailedAsync(string? ffmpegPathHint = null)
    {
        if (!TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out var failure))
            return Task.FromResult(new EncoderProbeResult(
                [], new Dictionary<string, string> { [VideoEncoder.Libx264.ToString()] = failure }));

        // GetOrAdd 的 valueFactory 可能并发跑两次，但探测幂等、只是浪费几秒，无需加锁。
        var cacheKey = ValidationCacheKey(ffmpegPath, "encoder-probe");
        return EncoderProbeCache.GetOrAdd(cacheKey, _ => ProbeEncodersUncachedAsync(ffmpegPath));
    }

    private static async Task<EncoderProbeResult> ProbeEncodersUncachedAsync(string ffmpegPath)
    {
        // x264 是所有构建的保底编码器，永远在列。
        var available = new List<VideoEncoder> { VideoEncoder.Libx264 };
        var failures = new ConcurrentDictionary<string, string>();

        var encoderList = await Task.Run(() =>
            RunExecutable(ffmpegPath, ["-hide_banner", "-nostdin", "-encoders"], TimeSpan.FromSeconds(12)));
        if (!encoderList.IsValid)
            return new EncoderProbeResult(available, failures); // 已通过启动校验；瞬时失败不阻塞，保底 x264
        var output = encoderList.Output;

        foreach (var (name, encoder) in SoftwareEncoderMap)
        {
            if (output.Contains(name))
                available.Add(encoder);
        }

        // 编译进构建的硬件编码器逐个试编码。同家族（NVENC/QSV/AMF/VideoToolbox）必须
        // 串行：同一驱动栈并发初始化编码会话会互踩——AMF 并发 InitDX11 直接
        // AVERROR(ENODEV)=-19、NVENC 消费级卡有并发会话数上限——导致本来可用的编码器
        // 被概率性误判剔除。不同家族各有各的驱动栈，跨家族保持并发不拖慢首跑。
        var candidates = new List<(string Name, VideoEncoder Encoder)>();
        foreach (var (name, encoder) in HardwareEncoderMap)
        {
            if (output.Contains(name))
                candidates.Add((name, encoder));
        }

        var groupChecks = candidates
            .GroupBy(c => EncoderVendor(c.Name))
            .Select(async group =>
            {
                var ok = new List<VideoEncoder>();
                foreach (var (name, encoder) in group)
                {
                    var (success, reason) = await VerifyEncoderAsync(ffmpegPath, name);
                    if (success)
                        ok.Add(encoder);
                    else
                        failures[encoder.ToString()] = reason;
                }

                return ok;
            })
            .ToList();
        foreach (var check in groupChecks)
            available.AddRange(await check);

        return new EncoderProbeResult(available, failures);
    }

    /// <summary>ffmpeg 硬件编码器命名恒为 codec_vendor（如 hevc_nvenc），取家族名分组。</summary>
    private static string EncoderVendor(string encoderName)
    {
        var idx = encoderName.IndexOf('_');
        return idx >= 0 ? encoderName[(idx + 1)..] : encoderName;
    }

    /// <summary>用几帧黑场真实跑一遍编码器，验证对应硬件/驱动确实存在且能初始化。
    /// 失败时带回原因摘要（stderr 首个具体错误 / 超时 / 异常），供探测结果与日志展示。</summary>
    private static async Task<(bool Ok, string Reason)> VerifyEncoderAsync(string ffmpegPath, string encoderName)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // 640x360：高于所有硬件编码器的最小分辨率限制，又足够小到瞬间完成。
            // 旧探测只送 3 帧且完全依赖编码器默认码控：新版 QSV/NVENC/AMF 驱动会
            // 因异步深度尚未排空或默认码控参数不完整而零包退出，实际正式压制却能跑。
            // 这里送足 30 帧，并使用与正式压制相同家族的恒质量参数。
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("color=black:s=640x360:r=30:d=1");
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("30");
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add(encoderName);
            AddProbeEncoderArgs(psi.ArgumentList, encoderName);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            proc = Process.Start(psi);
            if (proc == null) return (false, "无法启动 ffmpeg 试编码进程");

            var stdoutDrain = proc.StandardOutput.ReadToEndAsync();
            var stderrDrain = proc.StandardError.ReadToEndAsync();

            // 坏驱动可能在初始化里挂死——超时按不可用处理并回收进程。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await proc.WaitForExitAsync(cts.Token);
            await Task.WhenAll(stdoutDrain, stderrDrain);
            if (proc.ExitCode == 0) return (true, "");

            // FFmpeg 的最后一句经常只是 "Nothing was written..."，它会把前一行真正的
            // 驱动/参数错误盖掉。优先回传最后两条具体错误，界面才能给出可操作原因。
            var errorLines = stderrDrain.Result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            var concrete = errorLines
                .Where(l => !l.Contains("Nothing was written into output file", StringComparison.OrdinalIgnoreCase))
                // stderr 按“根因 → 连锁收尾错误”排列；首两条通常才是驱动拒绝原因，
                // 末尾的 Task finished / Terminating 只是同一错误的包装。
                .Take(2)
                .ToList();
            var summaryLines = concrete.Count > 0 ? concrete : errorLines.TakeLast(1).ToList();
            var summary = string.Join(" | ", summaryLines);
            if (summary.Length > 320) summary = summary[..320];
            return (false, $"退出码 {proc.ExitCode}" + (summary.Length > 0 ? "：" + summary : ""));
        }
        catch (OperationCanceledException)
        {
            KillQuiet(proc);
            return (false, "试编码 20 秒未完成（已终止）——疑似驱动初始化挂起");
        }
        catch (Exception ex)
        {
            KillQuiet(proc);
            return (false, "试编码异常：" + ex.Message);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    /// <summary>试编码沿用正式压制的硬件码控核心参数，避免“默认参数不可用、正式参数可用”的
    /// 假阴性。容器专用参数（如 hvc1 tag）不应传给 null muxer。</summary>
    private static void AddProbeEncoderArgs(IList<string> args, string encoderName)
    {
        if (encoderName.EndsWith("_videotoolbox", StringComparison.Ordinal))
        {
            args.Add("-q:v"); args.Add("65");
            if (encoderName.StartsWith("h264_", StringComparison.Ordinal))
            {
                args.Add("-profile:v"); args.Add("high");
            }
            args.Add("-allow_sw"); args.Add("1");
            return;
        }

        if (encoderName.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            args.Add("-preset"); args.Add("p4");
            args.Add("-tune"); args.Add("hq");
            args.Add("-rc"); args.Add("vbr");
            args.Add("-cq"); args.Add("21");
            args.Add("-b:v"); args.Add("0");
            args.Add("-multipass"); args.Add("0");
            return;
        }

        if (encoderName.EndsWith("_qsv", StringComparison.Ordinal))
        {
            args.Add("-global_quality"); args.Add("21");
            return;
        }

        if (encoderName.EndsWith("_amf", StringComparison.Ordinal))
        {
            var qp = encoderName.StartsWith("av1_", StringComparison.Ordinal) ? "84" : "21";
            args.Add("-quality"); args.Add("quality");
            args.Add("-rc"); args.Add("cqp");
            args.Add("-qp_i"); args.Add(qp);
            args.Add("-qp_p"); args.Add(qp);
            if (encoderName.StartsWith("h264_", StringComparison.Ordinal))
            {
                args.Add("-qp_b"); args.Add(qp);
            }
        }
    }

    private static void KillQuiet(Process? proc)
    {
        try
        {
            if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已退出/句柄失效，忽略。
        }
    }

    private static readonly ConcurrentDictionary<string, Task<FontSubsystemCheck>> FontCheckCache = new();

    /// <summary>字体子系统体检：lavfi 黑帧两帧 + 内置字体/系统字体各一行字幕的迷你压制。
    /// libass 在 Windows 上靠 DirectWrite/GDI 枚举系统字体做匹配与缺字回退，字体缓存
    /// 损坏的机器上单次字体查询能慢到数秒甚至无限挂起（真机实证：压制起步零输出、
    /// 日志停在 fontselect 序列里，纯软编管线同样挂）——健康机器亚秒完成，病机在
    /// 压制前就能暴露。结果按 ffmpeg 路径缓存（进程内一次）。
    /// SEKAI_SUPPRESS_FONTCHECK_TIMEOUT_SECONDS 可调超时（默认 20）。</summary>
    public static Task<FontSubsystemCheck> ProbeFontSubsystemAsync(string? ffmpegPathHint = null)
    {
        if (!TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out _))
            return Task.FromResult(new FontSubsystemCheck("skipped", 0, "未找到 ffmpeg，跳过检测"));

        var cacheKey = ValidationCacheKey(ffmpegPath, "font-check");
        return FontCheckCache.GetOrAdd(cacheKey, _ => FontCheckUncachedAsync(ffmpegPath));
    }

    private static async Task<FontSubsystemCheck> FontCheckUncachedAsync(string ffmpegPath)
    {
        var timeoutSeconds = 20;
        var env = Environment.GetEnvironmentVariable("SEKAI_SUPPRESS_FONTCHECK_TIMEOUT_SECONDS");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var custom) && custom > 0)
            timeoutSeconds = custom;

        string assPath;
        try
        {
            assPath = Path.Combine(Path.GetTempPath(), "sekai-fontcheck.ass");
            File.WriteAllText(assPath, FontCheckAss);
        }
        catch (Exception ex)
        {
            return new FontSubsystemCheck("skipped", 0, "无法写入临时字幕文件，跳过检测：" + ex.Message);
        }

        var filter = $"subtitles=filename={Suppressor.EscapeFfmpegFilterValue(assPath)}";
        var fontsDir = Suppressor.BundledFontsDir();
        if (fontsDir is not null)
            filter += $":fontsdir={Suppressor.EscapeFfmpegFilterValue(fontsDir)}";

        Process? proc = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("color=black:s=640x360:r=30:d=0.2");
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(filter);
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            proc = Process.Start(psi);
            if (proc == null)
                return new FontSubsystemCheck("skipped", 0, "无法启动检测进程，跳过检测");

            var drain = Task.WhenAll(
                proc.StandardOutput.ReadToEndAsync(),
                proc.StandardError.ReadToEndAsync());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            await proc.WaitForExitAsync(cts.Token);
            await drain;
            stopwatch.Stop();

            var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
            if (proc.ExitCode != 0)
                return new FontSubsystemCheck("skipped", elapsedMs,
                    $"检测未能完成（退出码 {proc.ExitCode}），不代表字体异常");
            if (elapsedMs > 6000)
                return new FontSubsystemCheck("slow", elapsedMs,
                    $"异常缓慢（{elapsedMs / 1000.0:0.#} 秒）——本机字体子系统（Windows 字体缓存 / DirectWrite）" +
                    "疑似异常，压制可能长时间无进度；建议重建系统字体缓存后重启，并检查最近安装的字体");
            return new FontSubsystemCheck("ok", elapsedMs, $"正常（{elapsedMs} ms）");
        }
        catch (OperationCanceledException)
        {
            KillQuiet(proc);
            return new FontSubsystemCheck("hung", (int)stopwatch.ElapsedMilliseconds,
                $"检测 {timeoutSeconds} 秒未完成（已终止）——本机字体子系统（Windows 字体缓存 / DirectWrite）" +
                "疑似损坏，压制的字幕渲染会挂起；建议重建系统字体缓存后重启，并检查 / 清理最近安装的字体");
        }
        catch (Exception ex)
        {
            KillQuiet(proc);
            return new FontSubsystemCheck("skipped", (int)stopwatch.ElapsedMilliseconds,
                "检测异常，跳过：" + ex.Message);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    // 两行字幕分别命中内置字体（走 fontsdir 内存字体）与系统字体（走 DirectWrite/
    // fontconfig 系统枚举）——病灶在系统枚举侧，缺了第二行测不出来。
    private const string FontCheckAss = """
[Script Info]
ScriptType: v4.00+
PlayResX: 640
PlayResY: 360

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Bundled,Source Han Sans CN Medium,40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
Style: System,Arial,40,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,8,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:00.20,Bundled,,0,0,0,,字体子系统检测 テスト
Dialogue: 0,0:00:00.00,0:00:00.20,System,,0,0,0,,Font subsystem check 123
""";

    /// <summary>
    /// 从可用列表里挑默认编码器：优先本平台的 HEVC 硬编（质量/体积比好、够快），
    /// 依次退到 H264 硬编，最后保底 x264 软编。列表为空或全不认识时也回 x264。
    /// </summary>
    public static VideoEncoder RecommendEncoder(IReadOnlyCollection<VideoEncoder> available)
    {
        VideoEncoder[] preference = OperatingSystem.IsMacOS()
            ?
            [
                VideoEncoder.HevcVideoToolbox, VideoEncoder.H264VideoToolbox,
            ]
            :
            [
                // Windows/Linux：独显优先——NVENC（N 卡）与 AMF（A 卡）整组排在
                // QSV（绝大多数机器上是 CPU 核显）前面：双显卡机器上核显吞吐/画质
                // 都不如独显，不该因为"HEVC"标签就把推荐落到核显上。
                // 同一块卡内部 HEVC 优先于 H264。
                VideoEncoder.HevcNvenc, VideoEncoder.HevcAmf,
                VideoEncoder.H264Nvenc, VideoEncoder.H264Amf,
                VideoEncoder.HevcQsv, VideoEncoder.H264Qsv,
            ];

        foreach (var encoder in preference)
        {
            if (available.Contains(encoder))
                return encoder;
        }

        return VideoEncoder.Libx264;
    }

    private static bool TryResolveVapourSynth(
        string? ffmpegPathHint,
        out SuppressRuntimeDescriptor? descriptor,
        out string message)
    {
        descriptor = null;
        var vapourPath = ResolveExecutable(
            VapourExecutableNames,
            null,
            path => ValidateBasicExecutable(path, "--version"),
            out var vapourFailure);
        if (vapourPath is null)
        {
            message = vapourFailure;
            return false;
        }

        var scriptPath = ResolveScript();
        if (scriptPath is null)
        {
            message = "已找到 VSPipe，但未找到 lim5994.vpy 脚本。";
            return false;
        }

        if (!TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out var ffmpegFailure))
        {
            message = ffmpegFailure;
            return false;
        }

        descriptor = new SuppressRuntimeDescriptor(
            SuppressBackend.VapourSynth,
            ffmpegPath,
            vapourPath,
            scriptPath,
            ResolveFfprobe(ffmpegPath));
        message = $"已检测到 VapourSynth 压制环境（{Path.GetFileName(vapourPath)} + ffmpeg）。";
        return true;
    }

    private static bool TryResolveFfmpeg(
        string? ffmpegPathHint,
        out string ffmpegPath,
        out string message)
    {
        ffmpegPath = ResolveExecutable(
                          FfmpegExecutableNames,
                          ffmpegPathHint,
                          ValidateFfmpegExecutable,
                          out var failure)
                      ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            message = string.IsNullOrWhiteSpace(failure)
                ? "未找到可执行的 ffmpeg。"
                : failure;
            return false;
        }

        message = $"已检测到 ffmpeg 压制环境（{ffmpegPath}，已验证 libx264 与 subtitles 滤镜）。";
        return true;
    }

    private static string? ResolveFfprobe(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        var adjacent = directory is null
            ? null
            : Path.Combine(directory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        return ResolveExecutable(
            FfprobeExecutableNames,
            adjacent,
            ValidateFfprobeExecutable,
            out _);
    }

    private static string? ResolveExecutable(
        IEnumerable<string> candidateNames,
        string? configuredPath,
        Func<string, ExecutableValidation> validate,
        out string failure)
    {
        failure = string.Empty;
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var candidate in EnumerateExecutableCandidates(candidateNames, configuredPath))
        {
            if (!seen.Add(candidate)) continue;
            var result = validate(candidate);
            if (result.IsValid) return candidate;
            failure = $"{candidate} 不可用：{result.Message}";
        }

        return null;
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(
        IEnumerable<string> candidateNames,
        string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string? full = null;
            try { full = Path.GetFullPath(configuredPath); }
            catch { /* 非法路径继续尝试 PATH。 */ }
            if (full is not null && File.Exists(full)) yield return full;

            foreach (var path in FindAllOnPath(configuredPath))
                yield return path;
        }

        foreach (var candidate in candidateNames)
        {
            foreach (var root in SearchRoots())
            {
                var path = Path.Combine(root, candidate);
                if (File.Exists(path)) yield return Path.GetFullPath(path);
            }
        }

        foreach (var candidate in candidateNames)
        {
            foreach (var path in FindAllOnPath(candidate))
                yield return path;
        }
    }

    private static ExecutableValidation ValidateFfmpegExecutable(string path)
    {
        var key = ValidationCacheKey(path, "ffmpeg-capabilities");
        return FfmpegValidationCache.GetOrAdd(key, _ => ValidateFfmpegExecutableUncached(path));
    }

    private static ExecutableValidation ValidateFfmpegExecutableUncached(string path)
    {
        var version = RunExecutable(path, ["-hide_banner", "-version"], TimeSpan.FromSeconds(8));
        if (!version.IsValid)
            return version with { Message = "无法执行 ffmpeg：" + version.Message };

        var encoders = RunExecutable(path, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(12));
        if (!encoders.IsValid)
            return encoders with { Message = "无法读取 ffmpeg 编码器列表：" + encoders.Message };
        if (!encoders.Output.Contains("libx264", StringComparison.Ordinal))
            return new ExecutableValidation(false, "该 ffmpeg 未编译 libx264，无法提供软件编码保底。");

        var filters = RunExecutable(path, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(12));
        if (!filters.IsValid)
            return filters with { Message = "无法读取 ffmpeg 滤镜列表：" + filters.Message };
        if (!filters.Output.Contains("subtitles", StringComparison.Ordinal))
            return new ExecutableValidation(false, "该 ffmpeg 未编译 subtitles/libass 滤镜，无法烧录字幕。");

        return new ExecutableValidation(true, "ok", version.Output);
    }

    private static ExecutableValidation ValidateFfprobeExecutable(string path)
    {
        var key = ValidationCacheKey(path, "ffprobe-version");
        return BasicValidationCache.GetOrAdd(key, _ =>
        {
            var result = RunExecutable(path, ["-hide_banner", "-version"], TimeSpan.FromSeconds(8));
            if (!result.IsValid) return result;
            if (!result.Output.Contains("ffprobe version", StringComparison.OrdinalIgnoreCase))
                return new ExecutableValidation(false, "可执行文件未报告 ffprobe 版本标识。");
            return result;
        });
    }

    private static ExecutableValidation ValidateBasicExecutable(string path, string versionArgument)
    {
        var key = ValidationCacheKey(path, versionArgument);
        return BasicValidationCache.GetOrAdd(key,
            _ => RunExecutable(path, [versionArgument], TimeSpan.FromSeconds(8)));
    }

    private static string ValidationCacheKey(string path, string probe)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{probe}";
        }
        catch
        {
            return path + "\n" + probe;
        }
    }

    private static ExecutableValidation RunExecutable(
        string path,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            process = Process.Start(startInfo);
            if (process is null)
                return new ExecutableValidation(false, "进程启动返回空句柄。");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                KillQuiet(process);
                try { process.WaitForExit(2000); } catch { /* ignored */ }
                return new ExecutableValidation(false, $"执行超过 {timeout.TotalSeconds:0} 秒，已终止。");
            }

            Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2));
            var output = stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty;
            var error = stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty;
            var combined = output + Environment.NewLine + error;
            if (process.ExitCode != 0)
            {
                var detail = combined
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? "没有错误输出";
                if (detail.Length > 240) detail = detail[..240];
                return new ExecutableValidation(false, $"退出码 {process.ExitCode}：{detail}", combined);
            }

            return new ExecutableValidation(true, "ok", combined);
        }
        catch (Exception ex)
        {
            KillQuiet(process);
            return new ExecutableValidation(false,
                $"{ex.GetType().Name}: {ex.Message}（可能是文件不可执行或架构不兼容）");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string? ResolveScript()
    {
        foreach (var path in ScriptSearchPaths())
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        return null;
    }

    private static IEnumerable<string> SearchRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "Resources");
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources"));
        yield return Path.Combine(ResourceManager.DataBaseDir, "Resource", "vapourSynth");
    }

    private static IEnumerable<string> ScriptSearchPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Resources", "lim5994.vpy");
        yield return Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources")), "lim5994.vpy");
        yield return Path.Combine(AppContext.BaseDirectory, "lim5994.vpy");
        yield return Path.Combine(ResourceManager.DataBaseDir, "Resource", "vapourSynth", "lim5994.vpy");
    }

    private static IEnumerable<string> FindAllOnPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            yield break;

        var directories = pathEnv.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = ExpandPathCandidates(command).ToArray();

        foreach (var directory in directories)
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                    yield return Path.GetFullPath(path);
            }
        }
    }

    private static IEnumerable<string> ExpandPathCandidates(string command)
    {
        if (Path.HasExtension(command))
        {
            yield return command;
            yield break;
        }

        yield return command;
        if (!OperatingSystem.IsWindows())
            yield break;

        var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        foreach (var ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return command + ext;
        }
    }

    private static string BuildFailureMessage(string? diagnostic)
    {
        var message =
            "未找到可用的压制运行环境。\n" +
            "请在设置里指定可执行且包含 libx264 与 subtitles/libass 滤镜的 ffmpeg，或把它放到 PATH。\n" +
            "如果你已有 VapourSynth / VSPipe，也可以把它们放到应用目录或 PATH。";
        return string.IsNullOrWhiteSpace(diagnostic)
            ? message
            : message + "\n检测详情：" + diagnostic;
    }
}
