using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using SekaiToolsApp.Services;
using SekaiToolsBase.GameScript;
using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsBase.Story.Translation;
using SekaiToolsBase.Utils;
using SekaiToolsCore;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;

var failures = 0;

void Check<T>(string name, T actual, T expected)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
    Console.Error.WriteLine($"[FAIL] {name}: expected {expected}, got {actual}");
    failures++;
}

void CheckThrows<TException>(string name, Action action) where TException : Exception
{
    try
    {
        action();
        Console.Error.WriteLine($"[FAIL] {name}: expected {typeof(TException).Name}");
        failures++;
    }
    catch (TException)
    {
    }
}

async Task CheckThrowsAsync<TException>(string name, Func<Task> action) where TException : Exception
{
    try
    {
        await action();
        Console.Error.WriteLine($"[FAIL] {name}: expected {typeof(TException).Name}");
        failures++;
    }
    catch (TException)
    {
    }
}

var cases = new (string Name, string Text, int? Expected)[]
{
    ("literal-N", "前半\\N后半", 2),
    ("literal-n", "abc\\ndef", 3),
    ("real-newline", "前半\n后半", 2),
    // \R 是专用时间分轴点；同一文本中更早的 \N 只是排版换行。
    ("R-over-N", "甲\\N乙\\R丙", 2),
    ("none", "没有分轴", null),
};

foreach (var c in cases)
    Check(c.Name, c.Text.ExplicitSeparatorContentIndex(), c.Expected);
Check("trim-literal-n", "a\\nb".TrimAll().Length, 2);

// 翻译文件中的角色命名、冒号格式和行数都不是 scenario 的强约束。
// 不同命名必须可直接使用；缺行保留原文，无姓名行只替换正文。
var relaxedTranslationPath = Path.Combine(Path.GetTempPath(), $"sekaitools-translation-{Guid.NewGuid():N}.txt");
try
{
    var relaxedScript = new GameScript
    {
        TalkData =
        [
            new Talk("日文原名一", "原文一", 0),
            new Talk("日文原名二", "原文二", 0),
        ],
        Snippets =
        [
            new Snippet(1, 0, 0, 0, 0),
            new Snippet(1, 1, 0, 0, 0),
        ],
    };

    await File.WriteAllTextAsync(relaxedTranslationPath, "完全不同的译名：翻译正文");
    var namedTranslation = new TranslationData(relaxedTranslationPath);
    Check("translation-different-name-applicable", namedTranslation.IsApplicable(relaxedScript), true);
    var namedStory = new SekaiToolsBase.Story.Story(relaxedScript, namedTranslation);
    var namedDialog = (DialogStoryEvent)namedStory.Events[0];
    var missingDialog = (DialogStoryEvent)namedStory.Events[1];
    Check("translation-different-name-kept", namedDialog.FinalCharacter, "完全不同的译名");
    Check("translation-different-name-body", namedDialog.FinalContent, "翻译正文");
    Check("translation-missing-line-keeps-original", missingDialog.FinalContent, "原文二");

    await File.WriteAllTextAsync(relaxedTranslationPath, "不带姓名的翻译正文");
    var unnamedTranslation = new TranslationData(relaxedTranslationPath);
    Check("translation-unnamed-line-applicable", unnamedTranslation.IsApplicable(relaxedScript), true);
    var unnamedStory = new SekaiToolsBase.Story.Story(relaxedScript, unnamedTranslation);
    var unnamedDialog = (DialogStoryEvent)unnamedStory.Events[0];
    Check("translation-unnamed-line-keeps-original-name", unnamedDialog.FinalCharacter, "日文原名一");
    Check("translation-unnamed-line-body", unnamedDialog.FinalContent, "不带姓名的翻译正文");
}
finally
{
    File.Delete(relaxedTranslationPath);
}

DialogBaseFrameSet MakeSet(string translation)
{
    var data = new DialogStoryEvent(0, "一\n二\n三", 0, "测试", false, false)
    {
        BodyTranslated = translation,
    };
    return new DialogBaseFrameSet(data, FrameRate.Fps60);
}

// 覆盖旧代码把真实换行误写成 IndexOf("\\R")、最终得到 -1 的回归。
var actualNewline = MakeSet("前半\n后半");
Check("constructor-real-newline-index", actualNewline.Separate.SeparatorContentIndex, 2);
Check("constructor-real-newline-enabled", actualNewline.UseSeparator, false);
Check("constructor-literal-N-enabled", MakeSet("前半\\N后半").UseSeparator, false);

// 模拟截图：先有旧分割点，再把译文改为在更后面的 \N 处断开；新值必须覆盖旧值。
var edited = MakeSet("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCD");
edited.SetSeparator(200, 7);
edited.ApplyTranslation("ABCDEFGHIJ\\NKLMNOPQRSTUVWXYZ0123456789", true);
Check("edited-literal-N-index", edited.Separate.SeparatorContentIndex, 10);
Check("edited-literal-N-frame-preserved", edited.Separate.SeparateFrame, 200);
Check("edited-literal-N-enabled", edited.UseSeparator, true);

// UI 明确关闭分轴时优先于文本自动判断。
edited.ApplyTranslation("ABCDEFGHIJ\\NKLMNOPQRSTUVWXYZ0123456789", false);
Check("explicit-disable-preserved", edited.UseSeparator, false);

// 首尾空行不是有效文本边界，不能被 Math.Clamp 偷偷改成第一个/最后一个字处断开。
var boundary = MakeSet("ABCDEFGHIJ");
boundary.SetSeparator(200, 4);
boundary.ApplyTranslation("\\NABCDEFGHIJ", true);
Check("leading-marker-keeps-index", boundary.Separate.SeparatorContentIndex, 4);
boundary.ApplyTranslation("ABCDEFGHIJ\\N", true);
Check("trailing-marker-keeps-index", boundary.Separate.SeparatorContentIndex, 4);

// 导出副本必须保留帧和分隔状态，但后续规范化不能改写编辑中的原对象。
var sourceSet = MakeSet("甲\\N乙");
sourceSet.Add(10, new Point(12, 34));
sourceSet.SetSeparator(9, 1);
sourceSet.UseSeparator = true;
sourceSet.FirstProgress2Frame = 5;
sourceSet.FirstProgress3Frame = 8;
var exportSet = SubtitleMaker.CloneDialogForExport(sourceSet);
exportSet.Data.SetTranslationContent(exportSet.Data.BodyTranslated.TrimAll());
exportSet.SetSeparator(10, 2);
Check("export-clone-source-text", sourceSet.Data.BodyTranslated, "甲\\N乙");
Check("export-clone-source-separator", sourceSet.Separate.SeparateFrame, 9);
Check("export-clone-frame-count", exportSet.Frames.Count, sourceSet.Frames.Count);
Check("export-clone-progress-2", exportSet.FirstProgress2Frame, 5);
Check("export-clone-progress-3", exportSet.FirstProgress3Frame, 8);
Check("export-clone-use-separator", exportSet.UseSeparator, true);

Check("ffprobe-explicit-frame-count", Suppressor.ParseFrameProbe(
    """{"streams":[{"nb_frames":"300","avg_frame_rate":"30/1","duration":"10"}]}"""), 300);
Check("ffprobe-duration-fallback", Suppressor.ParseFrameProbe(
    """{"streams":[{"nb_frames":"N/A","avg_frame_rate":"30000/1001"}],"format":{"duration":"10.01"}}"""), 300);
Check("ffprobe-invalid-output", Suppressor.ParseFrameProbe("not-json"), 0);
Check("watchdog-zero-frame-is-not-progress",
    Suppressor.ProgressValueShowsAdvancement("frame", "0"), false);
Check("watchdog-malformed-frame-is-not-progress",
    Suppressor.ProgressValueShowsAdvancement("frame", "not-a-number"), false);
Check("watchdog-zero-time-is-not-progress",
    Suppressor.ProgressValueShowsAdvancement("out_time", "00:00:00.000000"), false);
Check("watchdog-na-time-is-not-progress",
    Suppressor.ProgressValueShowsAdvancement("out_time", "N/A"), false);
Check("watchdog-positive-frame-is-progress",
    Suppressor.ProgressValueShowsAdvancement("frame", "1"), true);
Check("watchdog-positive-time-is-progress",
    Suppressor.ProgressValueShowsAdvancement("out_time", "00:00:00.000001"), true);

Check("bundled-video-process-resource-check",
    ResourceManager.Instance.CheckLocalResource(ResourceType.VideoProcess), true);

var manifestRoot = Path.Combine(Path.GetTempPath(), $"sekaitools-resource-path-{Guid.NewGuid():N}");
var validResource = new Resource
{
    Path = "videoProcess/sub/file.bin",
    Size = 0,
    Md5 = "d41d8cd98f00b204e9800998ecf8427e",
};
var resolved = ResourceManager.ResolveResourcePath(ResourceType.VideoProcess, validResource.Path, manifestRoot);
Check("resource-contained-path", resolved,
    Path.GetFullPath(Path.Combine(manifestRoot, "videoProcess", "sub", "file.bin")));
CheckThrows<InvalidDataException>("resource-parent-traversal",
    () => ResourceManager.ResolveResourcePath(ResourceType.VideoProcess, "videoProcess/../escape.bin", manifestRoot));
CheckThrows<InvalidDataException>("resource-backslash-traversal",
    () => ResourceManager.ResolveResourcePath(ResourceType.VideoProcess, "videoProcess\\..\\escape.bin", manifestRoot));
CheckThrows<InvalidDataException>("resource-absolute-path",
    () => ResourceManager.ResolveResourcePath(ResourceType.VideoProcess, "/tmp/escape.bin", manifestRoot));
CheckThrows<InvalidDataException>("resource-wrong-type-root",
    () => ResourceManager.ResolveResourcePath(ResourceType.VideoProcess, "vapourSynth/file.bin", manifestRoot));
CheckThrows<InvalidDataException>("resource-invalid-md5",
    () => ResourceManager.ValidateResourceList(ResourceType.VideoProcess,
    [validResource with { Md5 = "not-a-hash" }]));
CheckThrows<InvalidDataException>("resource-negative-size",
    () => ResourceManager.ValidateResourceList(ResourceType.VideoProcess,
    [validResource with { Size = -1 }]));
CheckThrows<InvalidDataException>("resource-duplicate-path",
    () => ResourceManager.ValidateResourceList(ResourceType.VideoProcess,
    [validResource, validResource with { Path = "videoProcess/SUB/FILE.bin" }]));

if (!OperatingSystem.IsWindows())
{
    var linkRoot = Path.Combine(Path.GetTempPath(), $"sekaitools-resource-link-{Guid.NewGuid():N}");
    var outside = Path.Combine(Path.GetTempPath(), $"sekaitools-resource-outside-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(linkRoot, "videoProcess"));
    Directory.CreateDirectory(outside);
    try
    {
        Directory.CreateSymbolicLink(Path.Combine(linkRoot, "videoProcess", "linked"), outside);
        CheckThrows<InvalidDataException>("resource-symlink-escape",
            () => ResourceManager.ResolveResourcePath(
                ResourceType.VideoProcess,
                "videoProcess/linked/file.bin",
                linkRoot));
    }
    finally
    {
        Directory.Delete(linkRoot, true);
        Directory.Delete(outside, true);
    }
}

var installRoot = Path.Combine(Path.GetTempPath(), $"sekaitools-resource-install-{Guid.NewGuid():N}");
Directory.CreateDirectory(installRoot);
try
{
    var destination = Path.Combine(installRoot, "resource.bin");
    await File.WriteAllTextAsync(destination, "old-cache");
    var verifiedBytes = Encoding.UTF8.GetBytes("verified");
    var installResource = new Resource
    {
        Path = "videoProcess/resource.bin",
        Size = verifiedBytes.Length,
        Md5 = Convert.ToHexString(MD5.HashData(verifiedBytes)),
    };

    await CheckThrowsAsync<InvalidDataException>("resource-failed-download-preserves-cache", async () =>
        await ResourceManager.InstallVerifiedResourceAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("tampered")),
            destination,
            installResource,
            containmentRoot: installRoot));
    Check("resource-preserved-cache", await File.ReadAllTextAsync(destination), "old-cache");

    await ResourceManager.InstallVerifiedResourceAsync(
        new MemoryStream(verifiedBytes),
        destination,
        installResource,
        containmentRoot: installRoot);
    Check("resource-verified-replacement", await File.ReadAllTextAsync(destination), "verified");
    Check("resource-temp-cleanup", Directory.GetFiles(installRoot, "*.tmp").Length, 0);
}
finally
{
    Directory.Delete(installRoot, true);
}

if (!OperatingSystem.IsWindows())
{
    var runtimeRoot = Path.Combine(Path.GetTempPath(), $"sekaitools-runtime-{Guid.NewGuid():N}");
    Directory.CreateDirectory(runtimeRoot);
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        var fakeFfmpeg = Path.Combine(runtimeRoot, "ffmpeg");
        var fakeScript = """
                         #!/bin/sh
                         PATH=/bin:/usr/bin
                         case " $* " in
                           *" -version "*) echo "ffmpeg fake audit build"; exit 0 ;;
                           *" -encoders "*) echo " V..... libx264 fake"; exit 0 ;;
                           *" -filters "*) echo " ... subtitles fake"; exit 0 ;;
                         esac
                         sleep 30 &
                         echo "$$ $!" > "${0%/*}/started.pid"
                         wait
                         """;
        await File.WriteAllTextAsync(fakeFfmpeg, fakeScript);
        File.SetUnixFileMode(fakeFfmpeg,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("PATH", runtimeRoot);

        var sourceVideo = Path.Combine(runtimeRoot, "source.mp4");
        var pidPath = Path.Combine(runtimeRoot, "started.pid");
        await File.WriteAllBytesAsync(sourceVideo, [0]);
        var startedCount = 0;
        var finishedCount = 0;
        long callbackFailureTimestamp = 0;
        using var suppressor = new Suppressor(new SuppressorOptions
        {
            SourceVideo = sourceVideo,
            OutputPath = Path.Combine(runtimeRoot, "output.mp4"),
            FfmpegPath = fakeFfmpeg,
            PreferFfmpegPipeline = true,
        }, new SuppressorCallbacks
        {
            OnStarted = () =>
            {
                Interlocked.Increment(ref startedCount);
                // Process.Start only guarantees that the child was created; under a
                // loaded CI host the shell may not execute the test script immediately.
                // Wait for the script's parent+child PID handshake before triggering
                // the callback failure whose process-tree cleanup we want to verify.
                for (var attempt = 0; attempt < 500 && !File.Exists(pidPath); attempt++)
                    Thread.Sleep(20);
                Volatile.Write(ref callbackFailureTimestamp, Stopwatch.GetTimestamp());
                throw new InvalidOperationException("startup callback failure");
            },
            OnFinished = (_, _) => Interlocked.Increment(ref finishedCount),
        });

        CheckThrows<InvalidOperationException>("transactional-start-callback-failure", suppressor.Start);
        var failureTimestamp = Volatile.Read(ref callbackFailureTimestamp);
        var cleanupElapsed = failureTimestamp == 0
            ? TimeSpan.MaxValue
            : Stopwatch.GetElapsedTime(failureTimestamp);
        Check("transactional-start-cleanup-returned", cleanupElapsed < TimeSpan.FromSeconds(12), true);
        Check("transactional-start-callback-reached", Volatile.Read(ref startedCount), 1);
        Check("transactional-start-not-running", suppressor.IsRunning, false);
        await Task.Delay(250);
        Check("transactional-start-no-finished-callback", Volatile.Read(ref finishedCount), 0);
        Check("transactional-start-pids-recorded", File.Exists(pidPath), true);
        if (File.Exists(pidPath))
        {
            foreach (var pidText in (await File.ReadAllTextAsync(pidPath))
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(pidText, out var pid)) continue;
                var alive = true;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    alive = !process.HasExited;
                }
                catch (ArgumentException)
                {
                    alive = false;
                }
                Check($"transactional-start-process-{pid}-reaped", alive, false);
            }
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(runtimeRoot, true);
    }
}

if (failures > 0) return 1;
Console.WriteLine("[PASS] line editing, export isolation, resource integrity, and engine lifecycle checks");
return 0;
