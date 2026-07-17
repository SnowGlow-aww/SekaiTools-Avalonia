using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SekaiToolsApp.Services;

/// <summary>
/// 压制日志用的环境概览（引擎版本 / 系统 / CPU / 内存 / 显卡+驱动 / ffmpeg 版本）。
/// 真机故障全靠用户导出的日志定位——坏掉的显卡驱动会让硬件解码首帧挂死（2.3.5 降级
/// 阶梯的由来）、nightly ffmpeg 无 stats 行（2.3.3 的由来），这些行能省掉
/// "请问你的显卡驱动/ffmpeg 版本是……"的整轮来回。
/// 全部读取都容错：拿不到的项省略或写"未知"，绝不影响压制本身。
/// </summary>
public static class SystemEnvironmentInfo
{
    private static readonly Lazy<IReadOnlyList<string>> Cached = new(Build);
    private static readonly ConcurrentDictionary<string, string> FfmpegVersions = new();

    /// <summary>机器概览行（进程内只收集一次）。</summary>
    public static IReadOnlyList<string> DescribeLines() => Cached.Value;

    /// <summary>ffmpeg -version 首行（去掉 Copyright 尾巴，按可执行路径缓存）。失败返回 null。</summary>
    public static string? DescribeFfmpeg(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return null;
        var line = FfmpegVersions.GetOrAdd(ffmpegPath, ReadFfmpegVersionLine);
        return line.Length > 0 ? line : null;
    }

    private static IReadOnlyList<string> Build()
    {
        var lines = new List<string>(3);
        try
        {
            lines.Add($"[Sekai] 引擎 {EngineVersion()} · {RuntimeInformation.FrameworkDescription} · " +
                      $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})");
            lines.Add($"[Sekai] CPU: {CpuName()} · 内存 {TotalMemoryGb().ToString("0.#", CultureInfo.InvariantCulture)} GB");
            var gpus = GpuDescriptions();
            if (gpus.Count > 0)
                lines.Add("[Sekai] 显卡: " + string.Join(" / ", gpus));
        }
        catch
        {
            // 概览是附加信息，收集失败不能影响压制。
        }

        return lines;
    }

    private static string EngineVersion()
    {
        var v = (Assembly.GetEntryAssembly() ?? typeof(SystemEnvironmentInfo).Assembly).GetName().Version;
        return v is null ? "未知" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var name = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null) as string;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")?.Trim() ?? "未知";
            }

            if (OperatingSystem.IsMacOS())
            {
                // Apple Silicon 的芯片名同时就是 GPU 身份，macOS 不再单列显卡行。
                var brand = RunCaptureFirstLine("/usr/sbin/sysctl", "-n", "machdep.cpu.brand_string");
                if (!string.IsNullOrWhiteSpace(brand)) return brand.Trim();
            }
        }
        catch
        {
            // 落到"未知"。
        }

        return "未知";
    }

    private static double TotalMemoryGb()
        => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;

    /// <summary>Windows：显示适配器类键（无需 WMI，wmic 在新系统上已被移除）逐卡读
    /// DriverDesc/DriverVersion/DriverDate。其余平台返回空。</summary>
    private static List<string> GpuDescriptions()
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;

        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return result;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue;
                try
                {
                    using var key = cls.OpenSubKey(sub);
                    var desc = (key?.GetValue("DriverDesc") as string)?.Trim();
                    if (string.IsNullOrEmpty(desc)) continue;

                    var version = (key?.GetValue("DriverVersion") as string)?.Trim();
                    var date = (key?.GetValue("DriverDate") as string)?.Trim();
                    var extras = new List<string>(2);
                    if (!string.IsNullOrEmpty(version)) extras.Add("驱动 " + version);
                    if (!string.IsNullOrEmpty(date)) extras.Add(date);
                    result.Add(extras.Count > 0 ? $"{desc}（{string.Join(", ", extras)}）" : desc);
                }
                catch
                {
                    // 单张卡读不到就跳过。
                }
            }
        }
        catch
        {
            // 无权限/键不存在 → 不出显卡行。
        }

        return result.Distinct().ToList();
    }

    private static string ReadFfmpegVersionLine(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "-version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return "";

            // 限时读首行后直接收掉进程：不能被不产出任何输出的坏 ffmpeg 拖死。
            var lineTask = p.StandardOutput.ReadLineAsync();
            var first = lineTask.Wait(3000) ? lineTask.Result ?? "" : "";
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* 已退出。 */ }
            p.WaitForExit(1000);

            first = first.Trim();
            var copyright = first.IndexOf(" Copyright", StringComparison.Ordinal);
            return copyright > 0 ? first[..copyright] : first;
        }
        catch
        {
            return "";
        }
    }

    private static string? RunCaptureFirstLine(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        using var p = Process.Start(startInfo);
        if (p is null) return null;
        var lineTask = p.StandardOutput.ReadLineAsync();
        var line = lineTask.Wait(2000) ? lineTask.Result : null;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* 已退出。 */ }
        p.WaitForExit(1000);
        return line;
    }
}
