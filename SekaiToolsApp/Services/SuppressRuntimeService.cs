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

    private static readonly ConcurrentDictionary<string, Task<List<VideoEncoder>>> EncoderProbeCache = new();

    public static Task<List<VideoEncoder>> ProbeAvailableEncodersAsync(string? ffmpegPathHint = null)
    {
        if (!TryResolveFfmpeg(ffmpegPathHint, out var ffmpegPath, out _))
            return Task.FromResult(new List<VideoEncoder> { VideoEncoder.Libx264 });

        // GetOrAdd 的 valueFactory 可能并发跑两次，但探测幂等、只是浪费几秒，无需加锁。
        return EncoderProbeCache.GetOrAdd(ffmpegPath, ProbeEncodersUncachedAsync);
    }

    private static async Task<List<VideoEncoder>> ProbeEncodersUncachedAsync(string ffmpegPath)
    {
        // x264 是所有构建的保底编码器，永远在列。
        var available = new List<VideoEncoder> { VideoEncoder.Libx264 };

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
            if (proc == null) return available;

            var stderrDrain = proc.StandardError.ReadToEndAsync();
            output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            await stderrDrain;
        }
        catch
        {
            return available; // probe 失败不阻塞，保底 x264
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
                    if (await VerifyEncoderAsync(ffmpegPath, name))
                        ok.Add(encoder);
                }

                return ok;
            })
            .ToList();
        foreach (var check in groupChecks)
            available.AddRange(await check);

        return available;
    }

    /// <summary>ffmpeg 硬件编码器命名恒为 codec_vendor（如 hevc_nvenc），取家族名分组。</summary>
    private static string EncoderVendor(string encoderName)
    {
        var idx = encoderName.IndexOf('_');
        return idx >= 0 ? encoderName[(idx + 1)..] : encoderName;
    }

    /// <summary>用几帧黑场真实跑一遍编码器，验证对应硬件/驱动确实存在且能初始化。</summary>
    private static async Task<bool> VerifyEncoderAsync(string ffmpegPath, string encoderName)
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
            if (proc == null) return false;

            var drain = Task.WhenAll(
                proc.StandardOutput.ReadToEndAsync(),
                proc.StandardError.ReadToEndAsync());

            // 坏驱动可能在初始化里挂死——超时按不可用处理并回收进程。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await proc.WaitForExitAsync(cts.Token);
            await drain;
            return proc.ExitCode == 0;
        }
        catch
        {
            try
            {
                if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已退出/句柄失效，忽略。
            }

            return false;
        }
        finally
        {
            proc?.Dispose();
        }
    }

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
