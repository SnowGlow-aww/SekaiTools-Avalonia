using System.Drawing;
using System.Globalization;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using SekaiToolsCore;
using SekaiToolsCore.Match.TemplateMatcher;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;

namespace TimingSelfTest;

// Frame-level self-test harness for the "onset backdate + separator estimate" fix.
// Synthesizes a 30fps 1280x720 video whose dialog boxes are rendered with the SAME
// SekaiToolsCore TemplateManager glyph pipeline the matcher uses, drives the real
// VideoProcessor A/B (old behavior via env flags vs new), and compares recorded
// StartIndex/EndIndex/SeparateFrame against synthesis ground-truth.
internal static class Program
{
    // ---- geometry / synthesis constants ----
    const int W = 1280, H = 720, FPS = 30, N = 780;
    const int Stride = 3;       // frames per typed character
    const int NlPause = 9;      // frames of pause per newline (0.3s @30fps == game)
    static readonly MCvScalar Bg = new(38, 30, 32); // dark BGR dialog-box-ish background

    static readonly string ScratchDir =
        "/private/tmp/claude-501/-Users-amia/5a7f190c-1dde-407f-ae83-779148ec5142/scratchpad/selftest";
    static string FramesDir => Path.Combine(ScratchDir, "frames");
    static string VideoPath => Path.Combine(ScratchDir, "selftest.mp4");
    static string ScriptPath => Path.Combine(ScratchDir, "script.json");
    static string TransPath => Path.Combine(ScratchDir, "translate.txt");
    const string Ffmpeg = "/opt/homebrew/opt/ffmpeg-full/bin/ffmpeg";

    static readonly Size Res = new(W, H);
    static readonly double FrameRatio = W / (double)H;

    // Two glyph pipelines: tmDraw renders at display (1x) scale for pasting onto frames;
    // tmMatch mirrors the matcher (5x render -> /5 downsample) purely to read exact
    // template sizes so our paste geometry matches the matcher's ROIs.
    static readonly TemplateManager TmDraw = new(Res, noScale: true);
    static readonly TemplateManager TmMatch = new(Res);

    sealed class Spec
    {
        public required string Scenario;
        public required string Speaker;
        public required string Body;       // BodyOriginal (may contain \n)
        public required string Trans;      // translation body (may be long, single line)
        public int Appear;                 // frame name/box turns on (1-based)
        public int Char0;                  // frame first char appears (1-based)
        public int HoldEnd;                // last frame content is shown
        public int Clear => HoldEnd + 1;   // first blank frame
        public bool WeightedTyping;        // F: k-th char at Char0+round(2.4*Wk)-round(2.4*W1)+NlPause*newlines

        // derived geometry (filled in Prepare)
        public string TrimName = "";
        public Size Ntt;
        public Rectangle NameRoi;
        public Point NamePos;
        public Point ContentPos;
    }

    static readonly List<Spec> Specs = new()
    {
        // A: fresh appearance (name 60, typing starts 66 == +0.2s)
        new Spec{Scenario="A_fresh",           Speaker="みのり", Body="こんにちは今日", Trans="こんにちは、今日はいい天気ですね",
                 Appear=60,  Char0=66,  HoldEnd=110},
        // B1: first of rapid same-speaker pair (fresh)
        new Spec{Scenario="B1_rapid_first",    Speaker="えむ",   Body="おはようございます", Trans="おはようございます",
                 Appear=140, Char0=146, HoldEnd=175},
        // B2: second of rapid pair, box reappears 3 frames after B1 clears -> the lag scenario
        new Spec{Scenario="B2_rapid_second",   Speaker="えむ",   Body="げんきですかとても", Trans="元気ですか、とっても",
                 Appear=179, Char0=179, HoldEnd=225},
        // C1: different-speaker pair, both bodies start with 『 (anti cross-talk)
        new Spec{Scenario="C1_prefix_first",   Speaker="ねね",   Body="『きこえてる", Trans="『聞こえてるよ",
                 Appear=260, Char0=266, HoldEnd=300},
        // C2: second, rapid, different speaker, same first char 『
        new Spec{Scenario="C2_prefix_second",  Speaker="しほ",   Body="『まかせてね", Trans="『任せてね",
                 Appear=304, Char0=304, HoldEnd=345},
        // D: 3-line original + long (>37) translation -> UseSeparator ; long linger to skew old midpoint
        new Spec{Scenario="D_separator",       Speaker="みずき", Body="あいうえおか\nきくけこさし\nすせそたちつ",
                 Trans="いろはにほへとちりぬるをわかよたれそつねならむうゐのおくやまけふこえてあさきゆめみし",
                 Appear=370, Char0=376, HoldEnd=470},
        // E: box disappears cleanly after (End must not regress)
        new Spec{Scenario="E_last",            Speaker="そら",   Body="さようならまた", Trans="さようなら、また",
                 Appear=500, Char0=506, HoldEnd=545},
        // F0: same-speaker predecessor of F (fresh appearance, ordinary 3-frame stride typing)
        new Spec{Scenario="F0_rapid_first",    Speaker="こはね", Body="まもなくしゅっぱつ", Trans="马上就要出发了",
                 Appear=560, Char0=566, HoldEnd=600},
        // F: rapid same-speaker (typing starts F0.Clear+3=604) + 3-line original + long (>37)
        // translation -> UseSeparator. Weighted 80ms/char typewriter: k-th char pasted at
        // Char0 + round(2.4*Wk) - round(2.4*W1) + 9 frames per crossed newline (Wk = cumulative
        // weighted length; full-width=1, half-width=0.5 -> exactly the CharTime prior speed).
        // Chars 1-4 are half-width so the near-complete fingerprint is on screen when the state
        // machine enters at F0.Clear+9=610 (drop-grace exhaustion) -> Matched1/2/3 within 1-2
        // frames = the production "chase" whose fake P2P3 slope the plausibility gate must
        // reject (raw <= 2f / 2.5wu = 0.8 f/wu < 0.4*2.4=0.96). Chars 5-6 are dense full-width
        // glyphs so the grace probe (full 6-char template vs partially typed content) cannot
        // fuzzy-match and cut the grace early (with 6 narrow ASCII chars it matched on 5/6 chars
        // at 609 and stole one frame of old lag). Prefix shares nothing with neighbours. Virtual
        // full typing ends at frame 664; HoldEnd=755 leaves ~3s linger so the old midpoint
        // separator is clearly late.
        new Spec{Scenario="F_sep_rapid3line",  Speaker="こはね",
                 Body="ABCD響鶴\nいろはにほへと\nちりぬるをわか",
                 Trans="あかさたなはまやらわいきしちにひみりをうくすつぬふむゆるんえけせてねへめれおこそとのほもよろ",
                 Appear=604, Char0=604, HoldEnd=755, WeightedTyping=true},
    };

    static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "all";
        if (mode == "real") return RunReal(args); // real 模式不用合成，直接跑真实素材
        Prepare();
        switch (mode)
        {
            case "probe": return Probe();
            case "gen": GenerateVideo(); WriteScriptAndTrans(); return 0;
            case "run": return RunAB();
            case "all":
            default:
                GenerateVideo();
                WriteScriptAndTrans();
                return RunAB();
        }
    }

    // 拿真实视频 + 剧情 json + 翻译 txt 跑一次完整打轴，打印每条对话的说话人/起止(秒)/正文，并导出 ass。
    // 用于验证「短第 1 行折行导致时长过短」的修复(第 72 句 うんっ☆)与「3 行原文塌 \N」的修复。
    static int RunReal(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: real <video> <script.json> <translate.txt>");
            return 2;
        }

        var video = args[1];
        var script = args[2];
        var trans = args[3];

        double fps;
        using (var cap = new VideoCapture(video)) fps = cap.Get(CapProp.Fps);
        if (fps <= 0) fps = 60;
        string Sec(int frameIndex) => TimeSpan.FromSeconds(frameIndex / fps).ToString(@"hh\:mm\:ss\.ff");

        TemplateMatchCachePool.ResetAll();
        var config = new Config(video, script, trans);
        var collected = new List<DialogBaseFrameSet>();
        var done = new ManualResetEventSlim(false);
        var callbacks = new VideoProcessCallbacks
        {
            OnNewDialog = d =>
            {
                int idx;
                lock (collected) { collected.Add(d); idx = collected.Count - 1; }
                var body = d.Data.BodyOriginal.Replace("\r", "").Replace("\n", "\\n");
                var dur = (d.EndIndex() - d.StartIndex()) / fps;
                Console.WriteLine(
                    $"[dlg {idx,3}] {d.Data.CharacterOriginal,-8} {Sec(d.StartIndex())}->{Sec(d.EndIndex())} ({dur,6:F2}s) {body}");
            },
            OnTaskFinished = () => done.Set(),
        };

        using var proc = new VideoProcessor(config, callbacks);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        proc.StartProcess();
        if (!done.Wait(TimeSpan.FromSeconds(2400)))
            Console.WriteLine("[real] TIMEOUT waiting for OnTaskFinished");
        sw.Stop();
        Console.WriteLine($"[real] collected={collected.Count} elapsed={sw.Elapsed} stopReason={proc.StopReason} fps={fps}");

        var subtitle = proc.GenerateSubtitle(
            new List<BannerBaseFrameSet>(), collected, new List<MarkerBaseFrameSet>());
        var ass = subtitle.ToString();
        Directory.CreateDirectory(ScratchDir);
        var outAss = Path.Combine(ScratchDir, "real_" + Path.GetFileNameWithoutExtension(video) + ".ass");
        File.WriteAllText(outAss, ass);
        Console.WriteLine($"[real] ass -> {outAss} ({ass.Length} chars)");
        return 0;
    }

    // ---------- geometry helpers (mirror DialogTemplateMatcher) ----------
    static Size DialogAreaSize() =>
        FrameRatio > 16.0 / 9
            ? new Size((int)(1.389 * H), (int)(0.237 * H))
            : new Size((int)(0.781 * W), (int)(0.133 * W));

    static Rectangle NameTagCropArea(Size ntt)
    {
        var das = DialogAreaSize();
        int letterbox = FrameRatio <= 16.0 / 9 ? Math.Max(0, (int)((H - W * 9.0 / 16.0) / 2)) : 0;
        var rect = new Rectangle(
            (W - das.Width) / 2,
            H - das.Height - (int)(ntt.Height * 1.1) - letterbox,
            ntt.Width + (int)(ntt.Height * 1.8),
            (int)(ntt.Height * 1.8) + letterbox);
        rect.Intersect(new Rectangle(0, 0, W, H));
        return rect;
    }

    static string TrimName(string origin, int maxLen = 3)
    {
        var trimmed = "";
        var len = 0d;
        foreach (var c in origin)
        {
            trimmed += c;
            len += char.IsAscii(c) ? 0.5 : 1;
            if (len >= maxLen) break;
        }
        if (trimmed.Contains('・')) trimmed = trimmed[..trimmed.IndexOf('・')];
        return trimmed;
    }

    static void Prepare()
    {
        foreach (var s in Specs)
        {
            s.TrimName = TrimName(s.Speaker);
            s.Ntt = new GaMat(TmMatch.GetTemplate(TemplateUsage.DialogNameTag, s.TrimName)).Size;
            s.NameRoi = NameTagCropArea(s.Ntt);
            s.NamePos = new Point(s.NameRoi.X + 6, s.NameRoi.Y + 6);
            int offset = TemplateManager.GetFontSize(Res); // static content offset (==matcher)
            s.ContentPos = new Point(
                s.NamePos.X + (int)(0.1 * offset) + 2,
                s.NamePos.Y + (int)(1.1 * offset) + 3);
        }
    }

    // number of chars typed for spec at frame t (1-based), capped at 6 (matcher only reads <=6 prefix)
    static int CharsTyped(Spec s, int t)
    {
        if (t < s.Char0) return 0;
        if (s.WeightedTyping)
        {
            // weighted typewriter; pasted prefix capped at 6 raw chars (F's line1 has >=6 chars,
            // so the raw slice never crosses a newline)
            int cap = Math.Min(6, s.Body.Length);
            int k = 0;
            while (k < cap && WeightedCharFrame(s, k + 1) <= t) k++;
            return k;
        }
        int n = (t - s.Char0) / Stride + 1;
        return Math.Min(Math.Min(n, 6), s.Body.Length);
    }

    // Weighted-typewriter paste frame (1-based) of the k-th VISIBLE char of s.Body:
    // Char0 + round(2.4*Wk) - round(2.4*W1) + NlPause * newlinesBefore(k). 2.4 frames per
    // weighted unit == 80ms per full-width char @30fps == the game speed the CharTime prior
    // models; half-width chars weigh 0.5 (game types them at half time). Valid for any k
    // (also beyond the 6 pasted chars -> used for the typing-truth separator).
    static int WeightedCharFrame(Spec s, int k)
    {
        double w = 0, w1 = -1;
        int newlines = 0, seen = 0;
        foreach (var c in s.Body)
        {
            if (c == '\n') { newlines++; continue; }
            if (c == '\r') continue;
            w += char.IsAscii(c) ? 0.5 : 1;
            seen++;
            if (w1 < 0) w1 = w;
            if (seen == k)
                return s.Char0 + (int)Math.Round(2.4 * w) - (int)Math.Round(2.4 * w1) + NlPause * newlines;
        }
        throw new ArgumentOutOfRangeException(nameof(k), k, "beyond visible body length");
    }

    // ---------- compositing ----------
    static void Composite(Mat frame, Mat bgra, Point at)
    {
        var dstRect = new Rectangle(at.X, at.Y, bgra.Width, bgra.Height);
        dstRect.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        if (dstRect.Width <= 0 || dstRect.Height <= 0) return;
        var srcRect = new Rectangle(dstRect.X - at.X, dstRect.Y - at.Y, dstRect.Width, dstRect.Height);

        using var src = new Mat(bgra, srcRect);
        using var dst = new Mat(frame, dstRect);
        using var bgr = new Mat(); CvInvoke.CvtColor(src, bgr, ColorConversion.Bgra2Bgr);
        using var alpha1 = new Mat(); CvInvoke.ExtractChannel(src, alpha1, 3);
        using var alpha3 = new Mat(); CvInvoke.CvtColor(alpha1, alpha3, ColorConversion.Gray2Bgr);

        using var bgrF = new Mat(); bgr.ConvertTo(bgrF, DepthType.Cv32F);
        using var dstF = new Mat(); dst.ConvertTo(dstF, DepthType.Cv32F);
        using var aF = new Mat(); alpha3.ConvertTo(aF, DepthType.Cv32F, 1.0 / 255.0);
        using var invA = new Mat(aF.Size, DepthType.Cv32F, 3); invA.SetTo(new MCvScalar(1, 1, 1));
        CvInvoke.Subtract(invA, aF, invA);
        using var fg = new Mat(); CvInvoke.Multiply(bgrF, aF, fg);
        using var bgc = new Mat(); CvInvoke.Multiply(dstF, invA, bgc);
        using var outF = new Mat(); CvInvoke.Add(fg, bgc, outF);
        using var out8 = new Mat(); outF.ConvertTo(out8, DepthType.Cv8U);
        out8.CopyTo(dst);
    }

    // Hard BGR overwrite (ignores alpha). Used only for the menu sign so the ContentMatcher
    // (CCOEFF + alpha mask, no <1 upper bound) sees an exact-match ~1.0 and latches Finished
    // on frame 1. Soft-alpha blending onto our flat bg only reaches ~0.77 for this small icon,
    // just under the 0.80 gate; content/name still use Composite (soft) so they stay <1.
    static void HardCopyBgr(Mat frame, Mat bgra, Point at)
    {
        var dstRect = new Rectangle(at.X, at.Y, bgra.Width, bgra.Height);
        dstRect.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        if (dstRect.Width <= 0 || dstRect.Height <= 0) return;
        var srcRect = new Rectangle(dstRect.X - at.X, dstRect.Y - at.Y, dstRect.Width, dstRect.Height);
        using var src = new Mat(bgra, srcRect);
        using var dst = new Mat(frame, dstRect);
        using var bgr = new Mat(); CvInvoke.CvtColor(src, bgr, ColorConversion.Bgra2Bgr);
        bgr.CopyTo(dst);
    }

    static Mat BuildBase()
    {
        var b = new Mat(H, W, DepthType.Cv8U, 3);
        b.SetTo(Bg);
        var menu = TmDraw.GetMenuSign();
        int ms = TemplateManager.GetMenuSignSize(Res);
        HardCopyBgr(b, menu, new Point(W - ms - 12, 12));
        return b;
    }

    static void DrawDialog(Mat frame, Spec s, int t)
    {
        // name shown whole visible range
        var nameTpl = TmDraw.GetTemplate(TemplateUsage.DialogNameTag, s.TrimName);
        Composite(frame, nameTpl, s.NamePos);
        int k = CharsTyped(s, t);
        if (k >= 1)
        {
            var content = TmDraw.GetTemplate(TemplateUsage.DialogContent, s.Body[..k]);
            Composite(frame, content, s.ContentPos);
        }
    }

    static void GenerateVideo()
    {
        Console.WriteLine("[gen] rendering frames...");
        if (Directory.Exists(FramesDir)) Directory.Delete(FramesDir, true);
        Directory.CreateDirectory(FramesDir);
        using var baseFrame = BuildBase();
        for (int t = 1; t <= N; t++)
        {
            using var frame = baseFrame.Clone();
            var active = Specs.FirstOrDefault(s => t >= s.Appear && t <= s.Clear - 1);
            if (active != null) DrawDialog(frame, active, t);
            var path = Path.Combine(FramesDir, $"f_{t:00000}.png");
            CvInvoke.Imwrite(path, frame);
        }
        Console.WriteLine($"[gen] {N} frames written. encoding mp4...");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]{"-y","-framerate","30","-i",Path.Combine(FramesDir,"f_%05d.png"),
                     "-c:v","libx264","-crf","0","-pix_fmt","yuv444p","-r","30",VideoPath})
            psi.ArgumentList.Add(a);
        var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Console.WriteLine($"[gen] ffmpeg exit={p.ExitCode}");
        // verify frame count read-back
        using var cap = new VideoCapture(VideoPath);
        int read = 0, first = -1, last = -1;
        var fr = new Mat();
        while (cap.Read(fr)) { var pf = (int)cap.Get(CapProp.PosFrames); if (read == 0) first = pf; last = pf; read++; }
        fr.Dispose();
        Console.WriteLine($"[gen] readback frames={read} first={first} last={last} fps={cap.Get(CapProp.Fps)}");
    }

    static void WriteScriptAndTrans()
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"Snippets\": [\n");
        for (int i = 0; i < Specs.Count; i++)
            sb.Append($"    {{\"Index\":{i},\"Action\":1,\"ProgressBehavior\":1,\"ReferenceIndex\":0,\"Delay\":0.0}}{(i < Specs.Count - 1 ? "," : "")}\n");
        sb.Append("  ],\n  \"TalkData\": [\n");
        for (int i = 0; i < Specs.Count; i++)
        {
            var body = Specs[i].Body.Replace("\\", "\\\\").Replace("\n", "\\n");
            sb.Append($"    {{\"WindowDisplayName\":\"{Specs[i].Speaker}\",\"Body\":\"{body}\",\"WhenFinishCloseWindow\":0,\"TalkCharacters\":[{{\"Character2dId\":297}}],\"Voices\":[]}}{(i < Specs.Count - 1 ? "," : "")}\n");
        }
        sb.Append("  ],\n  \"SpecialEffectData\": []\n}\n");
        File.WriteAllText(ScriptPath, sb.ToString());

        var tb = new StringBuilder();
        foreach (var s in Specs)
            tb.Append($"{s.Speaker}：{s.Trans.Replace("\n", "")}\n");
        File.WriteAllText(TransPath, tb.ToString());
        Console.WriteLine("[gen] script.json + translate.txt written");
    }

    // ---------- probe: measure match scores on a fully-typed frame ----------
    static int Probe()
    {
        Console.WriteLine("=== PROBE match scores ===");
        using var baseFrame = BuildBase();
        // menu score
        {
            var menuGa = new GaMat(TmMatch.GetMenuSign(), false);
            int width = menuGa.Size.Width * 3, height = menuGa.Size.Height * 2;
            var roi = new Rectangle(W - width, 0, width, height);
            roi.Intersect(new Rectangle(0, 0, W, H));
            using var crop = new Mat(baseFrame, roi);
            var r = TemplateMatcher.Match(crop, menuGa, TemplateMatchCachePool.MatchUsage.Misc);
            Console.WriteLine($"menu           MaxVal={r.MaxVal:F4}");
        }
        int offset = TemplateManager.GetFontSize(Res);
        foreach (var s in Specs)
        {
            using var frame = baseFrame.Clone();
            // fully typed (k=6)
            var nameTpl = TmDraw.GetTemplate(TemplateUsage.DialogNameTag, s.TrimName);
            Composite(frame, nameTpl, s.NamePos);
            int kk = Math.Min(6, s.Body.Length);
            var content = TmDraw.GetTemplate(TemplateUsage.DialogContent, s.Body[..kk]);
            Composite(frame, content, s.ContentPos);

            // name match
            var nameGa = new GaMat(TmMatch.GetTemplate(TemplateUsage.DialogNameTag, s.TrimName));
            using var nroi = new Mat(frame, s.NameRoi);
            var nr = TemplateMatcher.Match(nroi, nameGa, TemplateMatchCachePool.MatchUsage.Misc);
            var pt = new Point(nr.MaxLoc.X + s.NameRoi.X, nr.MaxLoc.Y + s.NameRoi.Y);

            var crect = new Rectangle(pt.X + (int)(0.1 * offset), pt.Y + (int)(1.1 * offset),
                (int)(7.5 * offset), (int)(2.0 * offset));
            crect.Intersect(new Rectangle(0, 0, W, H));
            double v1 = 0, v3 = 0, v6 = 0;
            using (var croi = new Mat(frame, crect))
            {
                var g1 = new GaMat(TmMatch.GetTemplate(TemplateUsage.DialogContent, s.Body[..1]));
                var g3 = new GaMat(TmMatch.GetTemplate(TemplateUsage.DialogContent, s.Body[..Math.Min(3, s.Body.Length)]));
                var g6 = new GaMat(TmMatch.GetTemplate(TemplateUsage.DialogContent, s.Body[..Math.Min(6, s.Body.Length)]));
                v1 = TemplateMatcher.Match(croi, g1, TemplateMatchCachePool.MatchUsage.Misc).MaxVal;
                v3 = TemplateMatcher.Match(croi, g3, TemplateMatchCachePool.MatchUsage.Misc).MaxVal;
                v6 = TemplateMatcher.Match(croi, g6, TemplateMatchCachePool.MatchUsage.Misc).MaxVal;
            }
            Console.WriteLine($"{s.Scenario,-18} name={nr.MaxVal:F4} dpt=({pt.X - s.NamePos.X},{pt.Y - s.NamePos.Y}) c1={v1:F4} c3={v3:F4} c6={v6:F4}");
        }
        return 0;
    }

    // ---------- A/B drive ----------
    sealed class DlgResult
    {
        public string Scenario = "";
        public int Start, End, Sep;
    }

    static List<DlgResult> RunOnce(bool oldBehavior)
    {
        Environment.SetEnvironmentVariable("DisableOnsetBackdate", oldBehavior ? "true" : null);
        Environment.SetEnvironmentVariable("DisableSeparatorEstimate", oldBehavior ? "true" : null);
        TemplateMatchCachePool.ResetAll();

        var config = new Config(VideoPath, ScriptPath, TransPath);
        var collected = new List<DialogBaseFrameSet>();
        var done = new ManualResetEventSlim(false);
        var callbacks = new VideoProcessCallbacks
        {
            OnNewDialog = d => { lock (collected) collected.Add(d); },
            OnTaskFinished = () => done.Set(),
        };
        using var proc = new VideoProcessor(config, callbacks);
        proc.StartProcess();
        if (!done.Wait(TimeSpan.FromSeconds(180)))
            throw new Exception("processing timeout");

        // export (populates SeparateFrame for separator dialogs)
        var subtitle = proc.GenerateSubtitle(new List<BannerBaseFrameSet>(), collected, new List<MarkerBaseFrameSet>());
        var ass = subtitle.ToString();
        File.WriteAllText(Path.Combine(ScratchDir, oldBehavior ? "old.ass" : "new.ass"), ass);

        var results = new List<DlgResult>();
        foreach (var set in collected)
        {
            var spec = Specs.FirstOrDefault(s => s.Body == set.Data.BodyOriginal);
            results.Add(new DlgResult
            {
                Scenario = spec?.Scenario ?? set.Data.BodyOriginal,
                Start = set.StartIndex(),
                End = set.EndIndex(),
                Sep = set.UseSeparator ? set.Separate.SeparateFrame : -1,
            });
        }
        Console.WriteLine($"[run] {(oldBehavior ? "OLD" : "NEW")} collected={collected.Count} stopReason={proc.StopReason}");
        return results;
    }

    static int RunAB()
    {
        var old = RunOnce(true);
        var neu = RunOnce(false);

        Console.WriteLine();
        Console.WriteLine("=== RESULTS (0-based frame indices; lag = start - groundTruth) ===");
        var jsonRows = new List<string>();
        for (int i = 0; i < Specs.Count; i++)
        {
            var s = Specs[i];
            int gt = s.Char0 - 1; // 0-based ground-truth start
            var o = old.FirstOrDefault(r => r.Scenario == s.Scenario);
            var nn = neu.FirstOrDefault(r => r.Scenario == s.Scenario);
            if (o == null || nn == null)
            {
                Console.WriteLine($"{s.Scenario,-18} MISSING old={(o != null)} new={(nn != null)}");
                jsonRows.Add($"{{\"name\":\"{s.Scenario}\",\"missing\":true}}");
                continue;
            }
            int oldLag = o.Start - gt, newLag = nn.Start - gt;
            Console.WriteLine($"{s.Scenario,-18} gt={gt,3} oldStart={o.Start,3}(lag {oldLag,2}) newStart={nn.Start,3}(lag {newLag,2}) endOld={o.End,3} endNew={nn.End,3} sepOld={o.Sep} sepNew={nn.Sep}");
            jsonRows.Add($"{{\"name\":\"{s.Scenario}\",\"groundTruthStartFrame\":{gt},\"oldStartFrame\":{o.Start},\"newStartFrame\":{nn.Start},\"oldLagFrames\":{oldLag},\"newLagFrames\":{newLag},\"endOldFrame\":{o.End},\"endNewFrame\":{nn.End},\"sepOld\":{o.Sep},\"sepNew\":{nn.Sep}}}");
        }
        // separator report for every UseSeparator spec (3-line original + >37 translation): D and F
        Console.WriteLine();
        var sepJson = new List<string>();
        foreach (var s in Specs.Where(s => s.Body.Split('\n').Length == 3 && s.Trans.Length > 37))
        {
            int sepExpected = ExpectedSeparator0Based(s);
            var os = old.First(r => r.Scenario == s.Scenario);
            var ns = neu.First(r => r.Scenario == s.Scenario);
            Console.WriteLine(
                $"SEPARATOR {s.Scenario}: expected={sepExpected} old={os.Sep} new={ns.Sep}  |old-exp|={Math.Abs(os.Sep - sepExpected)} |new-exp|={Math.Abs(ns.Sep - sepExpected)}");
            sepJson.Add(
                $"{{\"name\":\"{s.Scenario}\",\"expectedFrame\":{sepExpected},\"oldFrame\":{os.Sep},\"newFrame\":{ns.Sep}}}");
        }

        Console.WriteLine();
        Console.WriteLine("JSON_BEGIN");
        Console.WriteLine("{\"separators\":[" + string.Join(",", sepJson) + "],\"results\":[" + string.Join(",", jsonRows) + "]}");
        Console.WriteLine("JSON_END");
        return 0;
    }

    // ground-truth separator frame (0-based): frame where typewriter reaches weighted
    // position ratio*Weight(original), including newline pauses. Mirrors the intended
    // game typewriter that EstimateSeparator models.
    static int ExpectedSeparator0Based(Spec s)
    {
        double Weight(string str)
        {
            double w = 0;
            foreach (var c in str) { if (c is '\n' or '\r') continue; w += char.IsAscii(c) ? 0.5 : 1; }
            return w;
        }
        var translated = s.Trans.Replace("\n", "");
        int sepIdx = translated.Length / 2; // matches DialogBaseFrameSet ctor (no \n, no \R)
        double ratio = Weight(translated[..sepIdx]) / Weight(translated);
        double wt = ratio * Weight(s.Body);

        // walk visible chars of original, accumulating weight; find the char that reaches wt
        double acc = 0; int j = 0; int newlines = 0;
        foreach (var c in s.Body)
        {
            if (c == '\n') { newlines++; continue; }
            if (c == '\r') continue;
            acc += char.IsAscii(c) ? 0.5 : 1;
            if (acc >= wt) break;
            j++;
        }
        // frame (1-based) when visible char j (0-based -> (j+1)-th) is typed
        int frame1 = s.WeightedTyping
            ? WeightedCharFrame(s, j + 1)
            : s.Char0 + Stride * j + NlPause * newlines;
        return frame1 - 1; // 0-based
    }
}
