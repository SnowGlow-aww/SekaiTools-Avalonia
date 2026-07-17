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
    string? VapourScriptPath = null);

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

    // preferFfmpeg：优先纯 ffmpeg 管线，机器上残留的 VapourSynth 只作兜底。
    // SekaiText 引擎（IPC）必须传 true——用户目录里老 SekaiTools 装的 VSPipe 版本不受
    // 我们控制，坏掉时 VSPipe 无输出、ffmpeg 只会报一句莫名其妙的
    // "yuv4mpegpipe … Header too large."；而且那条管线用 VSFilter 烧字幕，
    // 拿不到我们随引擎发布的字体。Avalonia 桌面版保持默认 false（老行为）。
    public static SuppressRuntimeProbe Probe(string? ffmpegPathHint = null, bool preferFfmpeg = false)
    {
        if (!preferFfmpeg && TryResolveVapourSynth(ffmpegPathHint, out var legacyDescriptor, out var legacyMessage))
            return new SuppressRuntimeProbe(true, legacyMessage, legacyDescriptor);

        if (TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out var ffmpegMessage))
        {
            return new SuppressRuntimeProbe(true, ffmpegMessage,
                new SuppressRuntimeDescriptor(SuppressBackend.Ffmpeg, ffmpegPath));
        }

        if (preferFfmpeg && TryResolveVapourSynth(ffmpegPathHint, out var fallbackDescriptor, out var fallbackMessage))
            return new SuppressRuntimeProbe(true, fallbackMessage, fallbackDescriptor);

        return new SuppressRuntimeProbe(false, BuildFailureMessage());
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
        if (!TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out _))
            return Task.FromResult(new EncoderProbeResult(
                [VideoEncoder.Libx264], new Dictionary<string, string>()));

        // GetOrAdd 的 valueFactory 可能并发跑两次，但探测幂等、只是浪费几秒，无需加锁。
        return EncoderProbeCache.GetOrAdd(ffmpegPath, ProbeEncodersUncachedAsync);
    }

    private static async Task<EncoderProbeResult> ProbeEncodersUncachedAsync(string ffmpegPath)
    {
        // x264 是所有构建的保底编码器，永远在列。
        var available = new List<VideoEncoder> { VideoEncoder.Libx264 };
        var failures = new ConcurrentDictionary<string, string>();

        string output;
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
            psi.ArgumentList.Add("-encoders");

            using var proc = Process.Start(psi);
            if (proc == null) return new EncoderProbeResult(available, failures);

            var stderrDrain = proc.StandardError.ReadToEndAsync();
            output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            await stderrDrain;
        }
        catch
        {
            return new EncoderProbeResult(available, failures); // probe 失败不阻塞，保底 x264
        }

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
    /// 失败时带回原因摘要（最后一行 stderr / 超时 / 异常），供探测结果与日志展示。</summary>
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
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("color=black:s=640x360:r=30:d=0.2");
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add(encoderName);
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

            // -loglevel error 下 stderr 基本只剩真正的错误，取最后一行非空即病灶。
            var lastError = stderrDrain.Result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .LastOrDefault(l => l.Length > 0) ?? "";
            if (lastError.Length > 200) lastError = lastError[..200];
            return (false, $"退出码 {proc.ExitCode}" + (lastError.Length > 0 ? "：" + lastError : ""));
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

        return FontCheckCache.GetOrAdd(ffmpegPath, FontCheckUncachedAsync);
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
        message = string.Empty;

        var vapourPath = ResolveExecutable(VapourExecutableNames);
        if (vapourPath is null)
            return false;

        var scriptPath = ResolveScript();
        if (scriptPath is null)
            return false;

        if (!TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out _))
            return false;

        descriptor = new SuppressRuntimeDescriptor(
            SuppressBackend.VapourSynth,
            ffmpegPath,
            vapourPath,
            scriptPath);
        message = $"已检测到 VapourSynth 压制环境（{Path.GetFileName(vapourPath)} + ffmpeg）。";
        return true;
    }

    private static bool TryResolveFfmpeg(
        string? ffmpegPathHint,
        out string ffmpegPath,
        out string message)
    {
        ffmpegPath = ResolveExecutable(FfmpegExecutableNames, ffmpegPathHint) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            message = string.Empty;
            return false;
        }

        message = $"已检测到 ffmpeg 压制环境（{ffmpegPath}）。";
        return true;
    }

    private static string? ResolveExecutable(IEnumerable<string> candidateNames, string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = Path.GetFullPath(configuredPath);
            if (File.Exists(full))
                return full;

            var fromPath = FindOnPath(configuredPath);
            if (fromPath is not null)
                return fromPath;
        }

        foreach (var candidate in candidateNames)
        {
            foreach (var root in SearchRoots())
            {
                var path = Path.Combine(root, candidate);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
        }

        foreach (var candidate in candidateNames)
        {
            var path = FindOnPath(candidate);
            if (path is not null)
                return path;
        }

        return null;
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

    private static string? FindOnPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        var directories = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = ExpandPathCandidates(command);

        foreach (var directory in directories)
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
        }

        return null;
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

    private static string BuildFailureMessage()
    {
        return
            "未找到可用的压制运行环境。\n" +
            "请在设置里指定 ffmpeg 路径，或把 ffmpeg 放到 PATH。\n" +
            "如果你已有 VapourSynth / VSPipe，也可以把它们放到应用目录或 PATH。";
    }
}
