using System.Drawing;
using Emgu.CV;
using SekaiToolsBase;
using SekaiToolsCore.Process;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;
using SekaiToolsCore.Utils;
using SekaiStory = SekaiToolsBase.Story.Story;

namespace SekaiToolsCore.Match.TemplateMatcher;

public class BannerTemplateMatcher(
    VideoInfo videoInfo,
    SekaiStory storyData,
    TemplateManager templateManager,
    Config config)
{
    public readonly List<BannerBaseFrameSet> Set = storyData.Banners()
        .Select(d => new BannerBaseFrameSet(d, videoInfo.Fps))
        .ToList();

    private MatchStatus _status;

    // 低阈值边界标定（修横幅起始偏晚/结束偏早）：
    //  - _recent 滚动记录待匹配横幅最近 ~1.5s 的逐帧匹配值；首次过正常阈值时向前
    //    回溯连续 ≥BannerFadeLow 的帧，得到淡入起笔 OnsetFrame。
    //  - 掉出正常阈值后进入 Tail 阶段：继续用低阈值延展 FadeTailFrame，连续
    //    TailMissLimit 帧低于低阈值才真正收尾。
    // 匹配区间 [_start,_end]（正常阈值语义）不变，预览 UI 不受影响；导出时
    // SubtitleMaker 采用 Onset/FadeTail 精修三条横幅行的起止。
    private readonly List<(int Frame, double Val)> _recent = [];
    private int _tailMiss;
    private const int TailMissLimit = 3;

    public bool Finished => Set.All(d => d.Finished) || Set.Count == 0;

    private GaMat GetTemplate(string content)
    {
        return new GaMat(templateManager.GetTemplate(TemplateUsage.BannerContent, content));
    }


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

    /// 当前帧对指定横幅文本的原始匹配值（裁剪/模板处理与 1.3.3 一致）。
    private double BannerMatchValue(Mat src, string text, int frameIndex = -1)
    {
        var sText = TrimContent(text);
        var tmp = GetTemplate(sText);

        var cropArea = UtilFunc.FromCenter(src.Size.Center(),
            new Size((int)(tmp.Size.Height * text.Length * 1.5), (int)(tmp.Size.Height * 1.5)));
        cropArea.Limit(new Rectangle(Point.Empty, src.Size));
        if (cropArea.IsEmpty || cropArea.Width < tmp.Size.Width || cropArea.Height < tmp.Size.Height) return 0;
        var imgCropped = new Mat(src, cropArea);
        // 旧版无任何预滤波；原 LaplaceSharpen(beta=0) 实为空操作(只附带一次灰度转换)，
        // 而 TemplateMatcher.Match 已会按需做 Bgr2Gray，故移除以保持与 1.3.3 一致。
        var result = TemplateMatcher.Match(imgCropped, tmp, TemplateMatchCachePool.MatchUsage.Banner);

        if (frameIndex != -1)
            Logger.Log(
                $"{nameof(BannerTemplateMatcher)} Frame {frameIndex} Match Banner {LastNotProcessedIndex()} Result: {result.MaxVal}");

        return result.MaxVal;
    }

    private static int LastNotProcessedIndex(List<BannerBaseFrameSet> set)
    {
        for (var i = 0; i < set.Count; i++)
            if (!set[i].Finished)
                return i;
        return -1;
    }

    public int LastNotProcessedIndex()
    {
        return LastNotProcessedIndex(Set);
    }

    private void Record(int frameIndex, double val)
    {
        _recent.Add((frameIndex, val));
        var cap = Math.Max(16, (int)(videoInfo.Fps.Fps() * 1.5));
        if (_recent.Count > cap) _recent.RemoveRange(0, _recent.Count - cap);
    }

    /// 从首个过正常阈值的帧向前回溯：连续（帧号相邻）且 ≥低阈值的最早帧即淡入起笔。
    private int BacktrackOnset(int matchedFrame, double lowThr)
    {
        var onset = matchedFrame;
        var maxBack = (int)(videoInfo.Fps.Fps() * 0.5);
        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var (frame, val) = _recent[i];
            if (frame >= onset) continue;
            if (frame != onset - 1) break; // 帧号断档（跳帧/上一条横幅收尾同帧接管）
            if (val < lowThr) break;
            onset = frame;
            if (matchedFrame - onset >= maxBack) break;
        }

        return onset;
    }

    public void Process(Mat frame, int frameIndex)
    {
        while (!Finished)
        {
            var index = LastNotProcessedIndex();
            if (index < 0) return;

            var set = Set[index];
            var val = BannerMatchValue(frame, set.Data.BodyOriginal, frameIndex);
            var matched = val >= config.MatchingThreshold.BannerNormal && val <= 1;
            var lowThr = config.MatchingThreshold.BannerFadeLow;
            Record(frameIndex, val);

            if (_status == MatchStatus.Matched)
            {
                if (matched)
                {
                    set.Add(frameIndex);
                    return;
                }

                // 掉出正常阈值 → 淡出尾巴阶段（本帧继续按低阈值处理）
                _status = MatchStatus.Tail;
                _tailMiss = 0;
                if (set.FadeTailFrame < set.EndIndex()) set.FadeTailFrame = set.EndIndex();
            }

            if (_status == MatchStatus.Tail)
            {
                if (val >= lowThr)
                {
                    set.FadeTailFrame = frameIndex;
                    _tailMiss = 0;
                    return;
                }

                if (++_tailMiss < TailMissLimit) return;

                set.Finished = true;
                _status = MatchStatus.NotMatched;
                _tailMiss = 0;
                _recent.Clear();
                continue; // 同帧内继续尝试下一条横幅
            }

            // NotMatched：等待首次过正常阈值
            if (matched)
            {
                _status = MatchStatus.Matched;
                set.Add(frameIndex);
                set.OnsetFrame = BacktrackOnset(frameIndex, lowThr);
            }

            return;
        }
    }

    private enum MatchStatus
    {
        NotMatched,
        Matched,
        Tail
    }
}
