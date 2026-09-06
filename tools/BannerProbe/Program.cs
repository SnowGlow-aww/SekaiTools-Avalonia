using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SekaiToolsCore.Match.TemplateMatcher;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;
using SekaiToolsCore.Utils;
using SekaiStory = SekaiToolsBase.Story.Story;

namespace BannerProbe;

// 横幅逐帧匹配值探针：对指定视频的若干时间窗，逐帧输出 BannerContent 模板的
// 匹配值 CSV（banner,frame,val），用真实成品视频 + 人工校对基准标定
// 「低阈值起点回溯 / 尾帧延展」的参数。裁剪与模板处理逻辑逐行镜像
// BannerTemplateMatcher.LocalMatch，保证测得的值与正式识别一致。
internal static class Program
{
    private static string TrimContent(string content)
    {
        var trimmed = "";
        var len = 0D;
        foreach (var c in content)
        {
            trimmed += c;
            len += char.IsAscii(c) ? 0.5 : 1;
            if (len >= 5) break;
        }

        foreach (var c in new[] { '・', '　', ' ' })
            if (trimmed.Contains(c))
                trimmed = trimmed[..trimmed.IndexOf(c)];

        return trimmed;
    }

    private static void RunDialogueProbe(double fromSec, double toSec, double graceSeconds)
    {
        var videoPath = "/Users/amia/Downloads/216/event_216_4.mp4";
        var scriptPath = "/Users/amia/Downloads/216/event_216_04.json";
        var vInfo = new VideoInfo(videoPath);
        var fps = vInfo.Fps.Fps();
        var tm = new TemplateManager(vInfo.Resolution);
        var story = SekaiStory.FromFile(scriptPath);
        var config = new Config(videoPath, scriptPath, "", matchingThreshold: new MatchingThreshold
        {
            DialogDropGraceSeconds = graceSeconds,
            DialogNametagNormal = 0.80,
            DialogNametagSpecial = 0.60,
            DialogContentNormal = 0.80,
            DialogContentSpecial = 0.60,
            BannerNormal = 0.80,
            MarkerNormal = 0.80
        });

        var matcher = new DialogTemplateMatcher(vInfo, story, tm, config);
        // Fast-forward finished dialogues up to talk 57
        for (var i = 0; i < 58; i++)
        {
            matcher.Set[i].Finished = true;
        }

        using var cap = new VideoCapture(videoPath);
        var f0 = (int)(fromSec * fps);
        var f1 = (int)(toSec * fps);
        cap.Set(CapProp.PosFrames, f0);
        var frame = new Mat();

        Console.WriteLine($"Probing Dialogs from {f0} ({fromSec}s) to {f1} ({toSec}s) with grace={graceSeconds}s...");
        var sw = Stopwatch.StartNew();
        var processedFrames = 0;

        for (var f = f0; f <= f1 && !matcher.Finished; f++)
        {
            if (!cap.Read(frame) || frame.IsEmpty) break;
            var curIdx = matcher.LastNotProcessedIndex();
            var tic = Stopwatch.GetTimestamp();
            var matched = matcher.Process(frame, f);
            var elapsedMs = Stopwatch.GetElapsedTime(tic).TotalMilliseconds;
            processedFrames++;

            if (f % 30 == 0 || elapsedMs > 20 || curIdx != matcher.LastNotProcessedIndex())
            {
                var curTalk = curIdx >= 0 && curIdx < matcher.Set.Count ? matcher.Set[curIdx].Data.CharacterOriginal + ": " + matcher.Set[curIdx].Data.BodyOriginal.Replace("\n", "\\N") : "DONE";
                Console.WriteLine($"Frame {f} ({f / fps:F2}s): curIdx={curIdx} ({curTalk}), cost={elapsedMs:F1}ms, matched={matched}, framesCount={matcher.Set[Math.Max(0, curIdx)].Frames.Count}, finished={matcher.Set[Math.Max(0, curIdx)].Finished}");
            }
        }
        Console.WriteLine($"Total: {processedFrames} frames in {sw.ElapsedMilliseconds}ms (avg {processedFrames * 1000.0 / sw.ElapsedMilliseconds:F1} fps)");
        for (var i = 57; i < Math.Min(65, matcher.Set.Count); i++)
        {
            var d = matcher.Set[i];
            Console.WriteLine($"Talk {i} ({d.Data.CharacterOriginal}: {d.Data.BodyOriginal.Replace("\n", "\\N")}): finished={d.Finished}, frames={d.Frames.Count} {(d.IsEmpty() ? "EMPTY" : $"[{d.StartIndex()}..{d.EndIndex()}]")}");
        }
    }

    private static void TestNote()
    {
        var img80Path = "/Volumes/Amia/Akiyama_mizuki/Coding/sessions/timing_test/frame_80s.png";
        var img58Path = "/Volumes/Amia/Akiyama_mizuki/Coding/sessions/timing_test/frame_58s.png";
        var img350Path = "/Volumes/Amia/Akiyama_mizuki/Coding/sessions/timing_test/sub_350.png";
        using var img80 = CvInvoke.Imread(img80Path, ImreadModes.Unchanged);
        using var img58 = CvInvoke.Imread(img58Path, ImreadModes.Unchanged);
        using var img350 = CvInvoke.Imread(img350Path, ImreadModes.Unchanged);
        Console.WriteLine($"img80: {img80.Size.Width}x{img80.Size.Height}, channels={img80.NumberOfChannels}");
        Console.WriteLine($"img58: {img58.Size.Width}x{img58.Size.Height}, channels={img58.NumberOfChannels}");

        var tm = new TemplateManager(img80.Size);

        void TestFrame(Mat frame, string name, string[] contents, string expectedSpeaker)
        {
            Console.WriteLine($"\n--- Testing {name} ---");
            var nameTpl = tm.GetGaTemplate(TemplateUsage.DialogNameTag, expectedSpeaker);
            CvInvoke.Imwrite($"/Volumes/Amia/Akiyama_mizuki/Coding/sessions/timing_test/tpl_name_{expectedSpeaker}.png", nameTpl.Gray);

            // Match nametag with NameTagCropArea
            var dialogAreaSize = frame.Size.Width / (double)frame.Size.Height > 16.0 / 9
                ? new Size((int)(1.389 * frame.Size.Height), (int)(0.237 * frame.Size.Height))
                : new Size((int)(0.781 * frame.Size.Width), (int)(0.133 * frame.Size.Width));
            var letterbox = frame.Size.Width / (double)frame.Size.Height <= 16.0 / 9
                ? Math.Max(0, (int)((frame.Size.Height - frame.Size.Width * 9.0 / 16.0) / 2))
                : 0;
            var nameRoi = new Rectangle
            {
                X = (frame.Size.Width - dialogAreaSize.Width) / 2,
                Y = frame.Size.Height - dialogAreaSize.Height - (int)(nameTpl.Size.Height * 1.1) - letterbox,
                Height = (int)(nameTpl.Size.Height * 1.8) + letterbox,
                Width = (int)(nameTpl.Size.Width + nameTpl.Size.Height * 1.8)
            };
            nameRoi.Limit(new Rectangle(Point.Empty, frame.Size));
            Console.WriteLine($"Name ROI: {nameRoi.X},{nameRoi.Y} {nameRoi.Width}x{nameRoi.Height}");
            using var nameCrop = new Mat(frame, nameRoi);
            var nameRes = TemplateMatcher.Match(nameCrop, nameTpl, TemplateMatchCachePool.MatchUsage.DialogNameTag);
            var point = new Point(nameRes.MaxLoc.X + nameRoi.X, nameRes.MaxLoc.Y + nameRoi.Y);
            Console.WriteLine($"Nametag '{expectedSpeaker}' in ROI: MaxVal={nameRes.MaxVal:F4} at ({point.X},{point.Y})");
            var offset = TemplateManager.GetFontSize(frame.Size);
            var crect = new Rectangle(point.X + (int)(0.1 * offset), point.Y + (int)(1.1 * offset),
                (int)(7.5 * offset), (int)(2.0 * offset));
            crect.Limit(new Rectangle(Point.Empty, frame.Size));
            using var contentCrop = new Mat(frame, crect);
            CvInvoke.Imwrite($"/Volumes/Amia/Akiyama_mizuki/Coding/sessions/timing_test/{name}_content_crop.png", contentCrop);
            Console.WriteLine($"Content ROI: {crect.X},{crect.Y} {crect.Width}x{crect.Height}");

            foreach (var txt in contents)
            {
                var tpl = tm.GetGaTemplate(TemplateUsage.DialogContent, txt);
                var safeName = txt.Replace('『', '_').Replace('』', '_').Replace(' ', 'S');
                CvInvoke.Imwrite($"/Volumes/Amia/Akiyama_mizuki/Coding/sessions/timing_test/tpl_{safeName}.png", tpl.Gray);
                if (tpl.Size.Width > contentCrop.Cols || tpl.Size.Height > contentCrop.Rows)
                {
                    Console.WriteLine($"Content '{txt}': SKIPPED (tplSize={tpl.Size.Width}x{tpl.Size.Height} > crop={contentCrop.Cols}x{contentCrop.Rows})");
                    continue;
                }
                var res = TemplateMatcher.Match(contentCrop, tpl, TemplateMatchCachePool.MatchUsage.Misc);
                Console.WriteLine($"Content '{txt}': MaxVal={res.MaxVal:F4} at ({res.MaxLoc.X},{res.MaxLoc.Y}) tplSize={tpl.Size.Width}x{tpl.Size.Height}");
            }
        }

        TestFrame(img58, "frame_58s_akito_only", new[] { "『♪————" }, "中学生の彰人");
        TestFrame(img350, "sub_350_akito_only", new[] { "『♪————" }, "中学生の彰人");
        TestFrame(img58, "frame_58s_full", new[] { "『♪————" }, "中学生の彰人・中学生の冬弥");
        TestFrame(img350, "sub_350_full", new[] { "♪—————" }, "中学生の冬弥");
        TestFrame(img350, "sub_350_cross_talk58_speaker", new[] { "『♪————" }, "中学生の彰人・中学生の冬弥");
    }

    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "test-note")
        {
            TestNote();
            return;
        }
        if (args.Length > 0 && args[0] == "probe-dialog")
        {
            var from = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 345.0;
            var to = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 360.0;
            var grace = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0.30;
            RunDialogueProbe(from, to, grace);
            return;
        }
        var videoPath = args.Length > 0 ? args[0] : throw new ArgumentException("usage: BannerProbe <video> [outCsv]");
        var outCsv = args.Length > 1 ? args[1] : "banner-probe.csv";

        var vInfo = new VideoInfo(videoPath);
        var fps = vInfo.Fps.Fps();
        Console.WriteLine($"video: {vInfo.Resolution.Width}x{vInfo.Resolution.Height} fps={fps} frames={vInfo.FrameCount}");

        var manager = new TemplateManager(vInfo.Resolution);

        // (原文全文, 探测窗口秒) —— 窗口取人工基准 ±1.5s
        var banners = new (string Text, double From, double To)[]
        {
            ("宮益坂", 6.0, 10.5),
            ("宵崎家　キッチン", 53.0, 57.0),
            ("誰もいないセカイ", 103.5, 107.5),
        };

        using var capture = new VideoCapture(videoPath);
        using var sw = new StreamWriter(outCsv);
        sw.WriteLine("banner,frame,val");

        for (var b = 0; b < banners.Length; b++)
        {
            var (text, from, to) = banners[b];
            var sText = TrimContent(text);
            var template = new GaMat(manager.GetTemplate(TemplateUsage.BannerContent, sText));
            var f0 = (int)(from * fps);
            var f1 = (int)(to * fps);
            capture.Set(CapProp.PosFrames, f0);
            var frame = new Mat();
            for (var f = f0; f <= f1; f++)
            {
                if (!capture.Read(frame) || frame.IsEmpty) break;
                var cropArea = UtilFunc.FromCenter(frame.Size.Center(),
                    new Size((int)(template.Size.Height * text.Length * 1.5), (int)(template.Size.Height * 1.5)));
                cropArea.Limit(new Rectangle(Point.Empty, frame.Size));
                double val = 0;
                if (!cropArea.IsEmpty && cropArea.Width >= template.Size.Width && cropArea.Height >= template.Size.Height)
                {
                    using var cropped = new Mat(frame, cropArea);
                    val = TemplateMatcher.Match(cropped, template, TemplateMatchCachePool.MatchUsage.Banner).MaxVal;
                }

                sw.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{b},{f},{val:F4}"));
            }

            Console.WriteLine($"banner {b} ({sText}) window [{f0},{f1}] done");
        }

        Console.WriteLine($"csv -> {outCsv}");

        // ── e2e：驱动真正的 BannerTemplateMatcher 跑三个窗口，验证精修边界与最终 ass 时间 ──
        if (args.Length > 2)
        {
            var scriptPath = args[2];
            var config = new Config(videoPath, scriptPath, "");
            var matcher = new BannerTemplateMatcher(vInfo, SekaiStory.FromFile(scriptPath), manager, config);
            using var cap2 = new VideoCapture(videoPath);
            var fr = new Mat();
            foreach (var (_, from, to) in banners)
            {
                var f0 = (int)(from * fps);
                var f1 = (int)(to * fps);
                cap2.Set(CapProp.PosFrames, f0);
                for (var f = f0; f <= f1 && !matcher.Finished; f++)
                {
                    if (!cap2.Read(fr) || fr.IsEmpty) break;
                    matcher.Process(fr, f);
                }
            }

            foreach (var set in matcher.Set)
            {
                // 与 SubtitleMaker.GenerateBannerEvent 相同的精修换算
                var onset = set.OnsetFrame >= 0 ? set.OnsetFrame : set.StartIndex();
                var tail = set.FadeTailFrame >= set.EndIndex() ? set.FadeTailFrame : set.EndIndex();
                var textStartF = Math.Max(0, onset - (int)Math.Round(fps * 0.033));
                var maskStartF = Math.Max(0, textStartF - (int)Math.Round(fps * 0.050));
                var endF = tail + (int)Math.Round(fps * 0.033);
                Console.WriteLine(
                    $"e2e {set.Data.FinalContent}: matched=[{set.StartIndex()},{set.EndIndex()}] onset={set.OnsetFrame} tail={set.FadeTailFrame} " +
                    $"mask={new ProcessFrame(maskStartF, vInfo.Fps).StartTime()} text={new ProcessFrame(textStartF, vInfo.Fps).StartTime()} end={new ProcessFrame(endF, vInfo.Fps).EndTime()}");
            }
        }
    }
}
