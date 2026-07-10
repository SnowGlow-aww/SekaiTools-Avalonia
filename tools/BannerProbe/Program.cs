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

    public static void Main(string[] args)
    {
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
