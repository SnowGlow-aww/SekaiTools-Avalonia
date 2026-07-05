using System.Drawing;
using Emgu.CV;
using SekaiToolsBase;
using SekaiToolsBase.Story;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;
using SekaiToolsCore.Utils;

namespace SekaiToolsCore.Match.TemplateMatcher;

public class DialogTemplateMatcher(
    VideoInfo videoInfo,
    Story storyData,
    TemplateManager templateManager,
    Config config
)
{
    public readonly List<DialogBaseFrameSet> Set =
        storyData.Dialogs().Select(d => new DialogBaseFrameSet(d, videoInfo.Fps)).ToList();

    private Point _nameTagPosition;

    private MatchStatus _status = 0;

    private int _consecutiveFailures;
    private int _lastFailedIndex = -1;
    private bool _useFallbackThreshold;
    private const double FallbackRatio = 0.7;
    // 兜底匹配的绝对下限：从 0.40 提到 0.50，避免低置信度的单帧把对话起点锚错。
    private const double AbsMinThreshold = 0.50;
    private int FallbackTriggerFrames => (int)Math.Ceiling(videoInfo.Fps.Fps() * 0.5);

    // 卡住跳过(look-ahead)状态：当前对话从没匹配上时已卡了多少帧 + 命中目标与连续命中计数。
    private int _emptyStuckFrames;
    private int _lookaheadHits;
    private int _lookaheadTarget = -1;
    private const int LookaheadConfirmHits = 2;
    // 向前探测的窗口大小：不止探下一条，而是探后面若干条，覆盖"连续多条剧本行都没在视频里出现"
    // (如演出/闪回里被剪掉/合并的行)的情况，跳到第一条真正出现在画面里的后续对话。
    private const int LookaheadWindow = 12;
    private int SkipProbeFrames => config.MatchingThreshold.DialogStuckSkipSeconds <= 0
        ? int.MaxValue
        : (int)Math.Ceiling(videoInfo.Fps.Fps() * config.MatchingThreshold.DialogStuckSkipSeconds);

    // Grace window for a dialog that is *already on screen* (has matched frames) but
    // momentarily fails to match — flicker, dialog-box shake, a brief overlay. Without
    // this, a single sub-threshold frame ends the line early and the subtitle stops
    // before the dialog actually disappears ("covers不到全行"). During the window we
    // retry with the fallback threshold and buffer the frames; if matching recovers we
    // commit them so the timeline spans the gap, otherwise we discard them and finalize
    // at the last real match (no spurious tail on genuinely-ended dialogs).
    private int _droppedGrace;
    private MatchStatus _lastMatchedStatus;
    private readonly List<(int Index, Point Point)> _pendingFrames = [];
    private int DroppedGraceFrames =>
        (int)Math.Ceiling(videoInfo.Fps.Fps() * Math.Max(0, config.MatchingThreshold.DialogDropGraceSeconds));

    // 起笔回溯(onset backdate)：消除"下一条对话真实起点已到、但要等宽限耗尽/6字指纹打全才被记录"
    // 造成的系统性轴前滞后。逐帧用"名牌 + 内容首字(降阈)"探测某条对话最早出现的帧，真实命中时把该条
    // 的起点回填到这个最早帧。所有 onset 帧都是原始 frameIndex 单位(未做 FrameIndexOffset)。
    private int _onsetDialogIndex = -1; // 起笔候选属于哪条对话
    private int _onsetFrame = -1; // 原始 frameIndex 单位(与 Process 参数一致, 未做 FrameIndexOffset)
    private Point _onsetPoint = Point.Empty; // 起笔时名牌位置
    private bool _onsetDuringHold; // 是否在 Matched1/2 hold(anti-lag)期间录得(此期间当前条命中可疑, 不因其继续命中而作废)
    private bool _charOneOnsetHit; // DialogMatchContent 的 DialogNotMatched 分支写：首字模板分数是否过了 onset 降阈
    private static bool OnsetBackdateDisabled =>
        Environment.GetEnvironmentVariable("DisableOnsetBackdate") == "true"; // A/B 与线上兜底开关
    private int OnsetMaxBackdateFrames => (int)Math.Ceiling(videoInfo.Fps.Fps() * 0.35);
    private const double OnsetThresholdDelta = 0.15;
    private const double OnsetMinThreshold = 0.50;

    public bool Finished => Set.All(d => d.Finished) || Set.Count == 0;

    private void ResetOnset()
    {
        _onsetDialogIndex = -1;
        _onsetFrame = -1;
        _onsetPoint = Point.Empty;
        _onsetDuringHold = false;
    }

    // 记录起笔候选：仅当候选还不属于该条时才写(保持最早记录不被后续帧覆盖)。
    private void RecordOnset(int dialogIdx, int frameIndex, Point point, bool duringHold)
    {
        if (_onsetDialogIndex == dialogIdx) return;
        _onsetDialogIndex = dialogIdx;
        _onsetFrame = frameIndex;
        _onsetPoint = point;
        _onsetDuringHold = duringHold;
    }

    /// <summary>
    /// 起笔探测：用"名牌(正常阈值,无 fallback) + 内容首字(降阈)"判断某条对话此刻是否已经开始出现在画面里，
    /// 命中则记录其最早帧供真实命中时回溯起点。与 ProbeDialog(需 6 字指纹)不同——这里只要首字微微出现即算数，
    /// 目的正是抓到"打字机刚落笔"的那一刻。命中/失配都按连续性维护候选(见下)。
    /// </summary>
    private void TryProbeOnset(Mat img, int dialogIdx, int frameIndex, bool duringHold)
    {
        var dialogBase = Set[dialogIdx];
        var body = dialogBase.Data.BodyOriginal;
        if (string.IsNullOrEmpty(body))
        {
            // 正文为空 → 无法用内容首字确认，视为未命中；若陈旧候选属于本条则按失配清掉。
            if (_onsetDialogIndex == dialogIdx) ResetOnset();
            return;
        }

        var hit = false;
        var point = Point.Empty;

        // 1) 名牌(正常阈值, 不用 fallback，同 ProbeDialog 第一步)
        var nameTpl = GetNameTag(TrimTemplateContent(dialogBase.Data.CharacterOriginal));
        var nameThr = dialogBase.Data.Shake
            ? config.MatchingThreshold.DialogNametagSpecial
            : config.MatchingThreshold.DialogNametagNormal;
        var nameRoi = NameTagCropArea(nameTpl.Size, dialogBase.Data.Shake);
        if (!(nameRoi.IsEmpty || nameRoi.Width < nameTpl.Size.Width || nameRoi.Height < nameTpl.Size.Height))
        {
            using var nameCrop = new Mat(img, nameRoi);
            var nameRes = TemplateMatcher.Match(nameCrop, nameTpl, TemplateMatchCachePool.MatchUsage.Misc);
            if (nameRes.MaxVal > nameThr && nameRes.MaxVal < 1)
            {
                point = new Point(nameRes.MaxLoc.X + nameRoi.X, nameRes.MaxLoc.Y + nameRoi.Y);

                // 2) 内容首字(前 1 字)——降阈 max(内容阈值-Δ, 下限)，抓打字机刚落笔的首帧。ROI 同 ProbeDialog 第二步。
                var contentTpl = new GaMat(templateManager.GetTemplate(TemplateUsage.DialogContent, body[..1]));
                var contentThr = Math.Max(
                    (dialogBase.Data.Shake
                        ? config.MatchingThreshold.DialogContentSpecial
                        : config.MatchingThreshold.DialogContentNormal) - OnsetThresholdDelta,
                    OnsetMinThreshold);
                var offset = TemplateManager.GetFontSize(img.Size);
                var crect = new Rectangle(point.X + (int)(0.1 * offset), point.Y + (int)(1.1 * offset),
                    (int)(7.5 * offset), (int)(2.0 * offset));
                if (dialogBase.Data.Shake) crect.Extend(0.6);
                crect.Limit(new Rectangle(Point.Empty, videoInfo.Resolution));
                if (!(crect.IsEmpty || crect.Width < contentTpl.Size.Width || crect.Height < contentTpl.Size.Height))
                {
                    using var contentCrop = new Mat(img, crect);
                    var contentRes = TemplateMatcher.Match(contentCrop, contentTpl,
                        TemplateMatchCachePool.MatchUsage.Misc);
                    hit = contentRes.MaxVal > contentThr && contentRes.MaxVal < 1;
                }
            }
        }

        if (hit)
            RecordOnset(dialogIdx, frameIndex, point, duringHold);
        else if (_onsetDialogIndex == dialogIdx)
            ResetOnset(); // 连续性要求：本条一旦失配就清掉陈旧候选，防止用过时的帧回溯
    }

    private GaMat GetNameTag(string name)
    {
        return new GaMat(templateManager.GetTemplate(TemplateUsage.DialogNameTag, name));
    }

    private Point DialogMatchNameTag(Mat img, DialogBaseFrameSet dialogBase, int frameIndex = -1)
    {
        var template = GetNameTag(TrimTemplateContent(dialogBase.Data.CharacterOriginal));
        var threshold = dialogBase.Data.Shake
            ? config.MatchingThreshold.DialogNametagSpecial
            : config.MatchingThreshold.DialogNametagNormal;

        var roi = NameTagCropArea(template.Size, dialogBase.Data.Shake);
        if (roi.IsEmpty || roi.Width < template.Size.Width || roi.Height < template.Size.Height) return Point.Empty;
        var imgCropped = new Mat(img, roi);
        var result = TemplateMatcher.Match(imgCropped, template, TemplateMatchCachePool.MatchUsage.DialogNameTag);

        if (frameIndex != -1)
            Logger.Log(
                $"{nameof(DialogTemplateMatcher)} Frame {frameIndex} Match Name Tag {LastNotProcessedIndex()} Result: {result.MaxVal}");

        var effectiveThreshold = _useFallbackThreshold
            ? Math.Max(threshold * FallbackRatio, AbsMinThreshold)
            : threshold;
        if (effectiveThreshold < result.MaxVal && result.MaxVal < 1)
        {
            var res = new Point(result.MaxLoc.X + roi.X, result.MaxLoc.Y + roi.Y);
            if (_nameTagPosition.IsEmpty) _nameTagPosition = res;
            return res;
        }

        return Point.Empty;
    }

    /// <summary>
    /// look-ahead 探测：用正常阈值、无 fallback、不写任何状态地判断某条对话此刻是否在画面里。
    /// **名牌 + 内容前缀双重判定**：名牌区分说话人，内容前 3 个字区分"同一说话人的不同台词"
    /// (如连续多条大神使台词 『…… / 『—— / 『そ……，名牌都一样，必须靠内容前缀才能认准是哪一条)。
    /// 仅用于"卡住跳过"——当前对话卡死时在窗口里找"真正出现在画面上的那一条"。
    /// </summary>
    private bool ProbeDialog(Mat img, DialogBaseFrameSet dialogBase)
    {
        // 1) 名牌
        var nameTpl = GetNameTag(TrimTemplateContent(dialogBase.Data.CharacterOriginal));
        var nameThr = dialogBase.Data.Shake
            ? config.MatchingThreshold.DialogNametagSpecial
            : config.MatchingThreshold.DialogNametagNormal;
        var nameRoi = NameTagCropArea(nameTpl.Size, dialogBase.Data.Shake);
        if (nameRoi.IsEmpty || nameRoi.Width < nameTpl.Size.Width || nameRoi.Height < nameTpl.Size.Height)
            return false;
        using var nameCrop = new Mat(img, nameRoi);
        var nameRes = TemplateMatcher.Match(nameCrop, nameTpl, TemplateMatchCachePool.MatchUsage.Misc);
        if (!(nameRes.MaxVal > nameThr && nameRes.MaxVal < 1)) return false;
        var point = new Point(nameRes.MaxLoc.X + nameRoi.X, nameRes.MaxLoc.Y + nameRoi.Y);

        // 2) 内容前缀(前 ≤6 个字)——区分同名说话人的不同台词。与主匹配一致用 6 字指纹，
        //    避免 3 字在共享前缀(『……/『私 等)的相邻台词间串扰，导致跳过探测落到错的那条。
        var body = dialogBase.Data.BodyOriginal;
        if (string.IsNullOrEmpty(body)) return true; // 无正文则只认名牌
        var prefix = body[..Math.Min(6, body.Length)];
        var contentTpl = new GaMat(templateManager.GetTemplate(TemplateUsage.DialogContent, prefix));
        var contentThr = dialogBase.Data.Shake
            ? config.MatchingThreshold.DialogContentSpecial
            : config.MatchingThreshold.DialogContentNormal;
        var offset = TemplateManager.GetFontSize(img.Size);
        var crect = new Rectangle(point.X + (int)(0.1 * offset), point.Y + (int)(1.1 * offset),
            (int)(7.5 * offset), (int)(2.0 * offset));
        if (dialogBase.Data.Shake) crect.Extend(0.6);
        crect.Limit(new Rectangle(Point.Empty, videoInfo.Resolution));
        if (crect.IsEmpty || crect.Width < contentTpl.Size.Width || crect.Height < contentTpl.Size.Height)
            return false;
        using var contentCrop = new Mat(img, crect);
        var contentRes = TemplateMatcher.Match(contentCrop, contentTpl, TemplateMatchCachePool.MatchUsage.Misc);
        return contentRes.MaxVal > contentThr && contentRes.MaxVal < 1;
    }

    private Size GetDialogAreaSize()
    {
        return videoInfo.FrameRatio > 16.0 / 9
            ? new Size
            {
                Height = (int)(0.237 * videoInfo.Resolution.Height),
                Width = (int)(1.389 * videoInfo.Resolution.Height)
            }
            : new Size
            {
                Height = (int)(0.133 * videoInfo.Resolution.Width),
                Width = (int)(0.781 * videoInfo.Resolution.Width)
            };
    }

    private Rectangle NameTagCropArea(Size ntt, bool shake)
    {
        var dialogAreaSize = GetDialogAreaSize();

        // 16:10 / 16:9 兼容：游戏内容是 16:9。当画面比 16:9 更"高"(宽高比 ≤ 16:9，如 16:10、4:3)时，
        // 16:9 内容按宽度铺满，上下可能各有一条黑边(letterbox)；此时对话框可能落在"内容底部"而非
        // "画面底部"。把名牌搜索带向上扩展一条黑边的高度，使"铺满整屏"与"上下留黑边居中"两种录制都能命中。
        // 16:9 时黑边为 0，行为与旧版完全一致。
        var letterbox = videoInfo.FrameRatio <= 16.0 / 9
            ? Math.Max(0, (int)((videoInfo.Resolution.Height - videoInfo.Resolution.Width * 9.0 / 16.0) / 2))
            : 0;

        var rect = new Rectangle
        {
            X = (videoInfo.Resolution.Width - dialogAreaSize.Width) / 2,
            Y = videoInfo.Resolution.Height - dialogAreaSize.Height - (int)(ntt.Height * 1.1) - letterbox,
            Height = (int)(ntt.Height * 1.8) + letterbox,
            Width = (int)(ntt.Width + ntt.Height * 1.8)
        };
        if (shake)
            rect.Extend(0.6);

        rect.Limit(new Rectangle(Point.Empty, videoInfo.Resolution));
        return rect;
    }

    private static string TrimTemplateContent(string origin, int maxLen = 3)
    {
        var trimmed = "";
        var len = 0D;
        foreach (var c in origin)
        {
            trimmed += c;
            len += char.IsAscii(c) ? 0.5 : 1;
            if (len >= maxLen) break;
        }

        if (trimmed.Contains('・'))
            trimmed = trimmed[..trimmed.IndexOf('・')];

        return trimmed;
    }

    private MatchStatus DialogMatchContent(Mat img, DialogBaseFrameSet dialogBase, Point point,
        MatchStatus lastStatus = 0,
        int frameIndex = -1)
    {
        var content = dialogBase.Data.BodyOriginal;
        // 每次进入都先清 onset 首字命中标志：只有下面 DialogNotMatched 分支真正跑了首字匹配才会把它置真，
        // 避免 NameTagNotMatched/Dropped→DialogNotMatched 这类"没跑内容匹配就返回 DialogNotMatched"的路径读到陈旧值。
        _charOneOnsetHit = false;
        if (point.X == 0) return 0;
        var charTemplates = GetDialogInd();
        var template1 = charTemplates[0];
        var template2 = charTemplates[1];
        var template3 = charTemplates[2];

        bool matchRes;

        var matchingThreshold = dialogBase.Data.Shake
            ? config.MatchingThreshold.DialogContentSpecial
            : config.MatchingThreshold.DialogContentNormal;

        switch (lastStatus)
        {
            case MatchStatus.DialogNotMatched:
            {
                // 主动未匹配阶段(名牌已在、内容还没到正常阈值)：把首字模板的原始分数拿出来，
                // 过 onset 降阈即视作"起笔已现"，供 Process 记录回溯候选(此时正常阈值还没命中，不算真起点)。
                matchRes = LocalMatch(img, template1, matchingThreshold,
                    TemplateMatchCachePool.MatchUsage.DialogContent1, out var scoreOne);
                var onsetThr = Math.Max(matchingThreshold - OnsetThresholdDelta, OnsetMinThreshold);
                _charOneOnsetHit = scoreOne > onsetThr && scoreOne < 1;
                return matchRes ? MatchStatus.DialogMatched1 : MatchStatus.DialogNotMatched;
            }
            case MatchStatus.DialogMatched1:
            {
                matchRes = LocalMatch(img, template2, matchingThreshold,
                    TemplateMatchCachePool.MatchUsage.DialogContent2, out _);
                if (matchRes) return MatchStatus.DialogMatched2;
                matchRes = LocalMatch(img, template1, matchingThreshold,
                    TemplateMatchCachePool.MatchUsage.DialogContent1, out _);
                return matchRes ? MatchStatus.DialogMatched1 : MatchStatus.DialogDropped;
            }
            case MatchStatus.DialogMatched2:
            {
                matchRes = LocalMatch(img, template3, matchingThreshold,
                    TemplateMatchCachePool.MatchUsage.DialogContent3, out _);
                if (matchRes) return MatchStatus.DialogMatched3;
                matchRes = LocalMatch(img, template2, matchingThreshold,
                    TemplateMatchCachePool.MatchUsage.DialogContent2, out _);
                return matchRes ? MatchStatus.DialogMatched2 : MatchStatus.DialogDropped;
            }
            case MatchStatus.DialogMatched3:
            {
                matchRes = LocalMatch(img, template3, matchingThreshold,
                    TemplateMatchCachePool.MatchUsage.DialogContent3, out _);
                return matchRes ? MatchStatus.DialogMatched3 : MatchStatus.DialogDropped;
            }
            case MatchStatus.NameTagNotMatched:
            case MatchStatus.DialogDropped:
            default:
                return MatchStatus.DialogNotMatched;
        }


        bool LocalMatch(Mat src, GaMat tmp, double threshold, TemplateMatchCachePool.MatchUsage usage,
            out double score)
        {
            score = 0;
            var offset = TemplateManager.GetFontSize(src.Size);
            Rectangle dialogStartPosition = new(
                point.X + (int)(0.1 * offset),
                point.Y + (int)(1.1 * offset),
                // 加宽到 7.5*offset：要容纳 6 字内容指纹模板（约 6*offset 宽），原 4.0*offset 只够 ~3 字。
                (int)(7.5 * offset),
                (int)(2.0 * offset)
            );
            if (dialogBase.Data.Shake)
                dialogStartPosition.Extend(0.6);
            dialogStartPosition.Limit(new Rectangle(Point.Empty, videoInfo.Resolution));
            if (dialogStartPosition.IsEmpty ||
                dialogStartPosition.Width < tmp.Size.Width ||
                dialogStartPosition.Height < tmp.Size.Height)
                return false;

            var imgCropped = new Mat(src, dialogStartPosition);
            var result = TemplateMatcher.Match(imgCropped, tmp, usage);
            score = result.MaxVal;

            if (frameIndex != -1)
                Logger.Log(
                    $"{nameof(DialogTemplateMatcher)} Frame {frameIndex} Content[{usage}] idx={LastNotProcessedIndex()} val={result.MaxVal:F4}");

            // 内容匹配**不做** fallback 降阈。降阈(0.56)会让"共享前缀的下一条台框"以部分相关度
            // (『……/『 等前缀)把已结束的当前行"复活"——走 default(命中)分支、绕过掉帧宽限里的
            // 前进探测，导致匹配状态机整段滞后错位、后续整段被跳(实测 36/37 行滞后 7~14 秒)。
            // 内容的短暂失配(闪烁/淡入淡出)由掉帧宽限(缓冲帧 + 前进探测)兜住，无需降阈；名牌的
            // 重新捕获仍可用 fallback。
            return result.MaxVal > threshold && result.MaxVal < 1;
        }

        List<GaMat> GetDialogInd()
        {
            // 内容匹配的"指纹"长度：从 1→3→6 个字（原为 1→2→3）。3 字前缀（如『……/『私/『——）在
            // **同一说话人连续台词**之间不具区分度——已匹配的行会"粘"在后续共享前缀的台词框上，导致
            // 匹配状态机与画面实际显示的行错位、整段台词被吞/被跳（抽帧逐帧证实：3 字时 99『私、 会以
            // 0.84 误中 103『私の；6 字时仅 103 自身以 0.92 命中，串扰消失）。6 字前缀配合下方加宽的
            // 内容 ROI，可在同说话人快速对话里干净地区分相邻台词。打字机逐字动画下，1/3/6 字模板分别在
            // 打出第 1/3/6 个字时依次命中，与原三段式进度自然对应。
            var dialogBody1 = content[..1];
            var dialogBody2 = content[..Math.Min(3, content.Length)];
            var dialogBody3 = content[..Math.Min(6, content.Length)];
            var mat1 = templateManager.GetTemplate(TemplateUsage.DialogContent, dialogBody1);
            var mat2 = templateManager.GetTemplate(TemplateUsage.DialogContent, dialogBody2);
            var mat3 = templateManager.GetTemplate(TemplateUsage.DialogContent, dialogBody3);
            return [new GaMat(mat1), new GaMat(mat2), new GaMat(mat3)];
        }
    }

    private static int LastNotProcessedIndex(IReadOnlyList<DialogBaseFrameSet> set)
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

    public int DebugSetFinishedUntilContains(string targetString, string? speaker = null)
    {
        return DebugSetFinishedUntilContains(Set, targetString, speaker);
    }

    private static int DebugSetFinishedUntilContains(IList<DialogBaseFrameSet> set, string targetString,
        string? speaker)
    {
        for (var i = 0; i < set.Count; i++)
        {
            if (set[i].Data.BodyOriginal.Contains(targetString) &&
                (speaker == null || set[i].Data.CharacterOriginal.Contains(speaker)))
                return i;
            set[i].Finished = true;
        }

        return -1;
    }

    public void DebugSetFinishedAfter(int index)
    {
        DebugSetFinishedAfter(Set, index);
    }

    private static void DebugSetFinishedAfter(IList<DialogBaseFrameSet> set, int index)
    {
        for (var i = index; i < set.Count; i++) set[i].Finished = true;
    }

    public bool Process(Mat frame, int frameIndex)
    {
        MatchStatus? firstStatus = null;

        while (!Finished)
        {
            var dIndex = LastNotProcessedIndex(Set);
            if (dIndex < 0) break;

            if (dIndex != _lastFailedIndex)
            {
                _lastFailedIndex = dIndex;
                _consecutiveFailures = 0;
                _useFallbackThreshold = false;
                _droppedGrace = 0;
                _pendingFrames.Clear();
                _emptyStuckFrames = 0;
                _lookaheadHits = 0;
                _lookaheadTarget = -1;
                // 起笔候选若落后于新的活动对话(属于已翻篇的更早条)则清掉；等于当前活动对话的要留着——
                // 那正是宽限期间为"下一条"录下、此刻要转交给它去回溯起点的候选。
                if (_onsetDialogIndex >= 0 && _onsetDialogIndex < dIndex) ResetOnset();
            }

            var dialogRefers = Set[dIndex];
            var matchResult = MatchForDialog(frame, dialogRefers, frameIndex);
            _status = matchResult.Status;
            firstStatus ??= matchResult.Status;

            switch (_status)
            {
                case MatchStatus.DialogDropped:
                    // Finalize immediately only if the dialog never started, or the
                    // grace window is exhausted. While a tracked dialog is still inside
                    // the window, keep it alive: buffer this frame at the last known
                    // position, drop the threshold, and restore the matched status so
                    // the next frame retries the same template. Transient noise no
                    // longer truncates the line before it is fully shown.
                    if (!Set[dIndex].IsEmpty() && _droppedGrace < DroppedGraceFrames)
                    {
                        // 宽限期是为闪烁/抖动保命的（当前条短暂失配但其实还在画面上）。但在**快速连续对话**里，
                        // 当前条结束的同一刻下一条的台词框就已出现，若死守宽限会滞留在已结束的当前条上、错过
                        // 下一条的短暂台框（实测漏轴的 38-40/113-116/119-125 都是这种）。因此每帧探测"下一条
                        // 是否已经出现在画面里"（名牌+6字内容指纹）：只有下一条**尚未**出现时才继续宽限；
                        // 一旦下一条出现，立刻结束宽限、定版当前条并在本帧前进去匹配下一条。
                        var probeIdx = dIndex + 1;
                        while (probeIdx < Set.Count && Set[probeIdx].Finished) probeIdx++;
                        var nextAppeared = probeIdx < Set.Count && ProbeDialog(frame, Set[probeIdx]);
                        if (!nextAppeared)
                        {
                            // 下一条 6 字指纹还没打全(所以 nextAppeared 假)，但它的首字可能已经落笔——
                            // 每帧探一下起笔，记下最早出现帧，稍后下一条真正命中时把起点回溯到这里(消滞后)。
                            if (probeIdx < Set.Count) TryProbeOnset(frame, probeIdx, frameIndex, false);
                            _droppedGrace++;
                            _pendingFrames.Add((frameIndex, Set[dIndex].End().Point));
                            _useFallbackThreshold = true;
                            _status = _lastMatchedStatus;
                            return IsStatusMatched(_lastMatchedStatus);
                        }
                        // 下一条已出现：丢弃宽限缓冲，立即定版当前条并前进。
                        _pendingFrames.Clear();
                    }

                    Set[dIndex].Finished = true;
                    _droppedGrace = 0;
                    _pendingFrames.Clear();
                    _consecutiveFailures = 0;
                    _useFallbackThreshold = false;
                    _emptyStuckFrames = 0;
                    _lookaheadHits = 0;
                    _lookaheadTarget = -1;
                    TemplateMatchCachePool.NextDialog();
                    continue;
                case MatchStatus.DialogNotMatched or MatchStatus.NameTagNotMatched:
                    _consecutiveFailures++;
                    _emptyStuckFrames++;

                    // 起笔回溯：主动扫描阶段的候选维护。DialogNotMatched=名牌已在、首字尚未到正常阈值：
                    // 若首字过了 onset 降阈(_charOneOnsetHit)就记下起笔候选，否则本条的陈旧候选按连续性清掉。
                    // NameTagNotMatched=名牌都不在，本条不可能在起笔，属于本条的陈旧候选一律清掉。
                    if (_status == MatchStatus.DialogNotMatched)
                    {
                        if (_charOneOnsetHit) RecordOnset(dIndex, frameIndex, matchResult.Point, false);
                        else if (_onsetDialogIndex == dIndex) ResetOnset();
                    }
                    else if (_onsetDialogIndex == dIndex)
                    {
                        ResetOnset();
                    }

                    // 卡住跳过(look-ahead)：仅当"当前这条的名牌此刻根本不在画面里(NameTagNotMatched)"、
                    // 且从没匹配上(IsEmpty)、且已卡够久时，才探测"下一条"是否已经出现；连续确认
                    // LookaheadConfirmHits 帧后判定当前这条被漏掉(如演出/MV 里某条难匹配的行)，跳过并前进。
                    // 关键：用 NameTagNotMatched 门控——若当前说话人正在画面上(只是内容还在逐字匹配，状态为
                    // DialogNotMatched)，绝不跳，避免连续同名说话人(如えむ→えむ)把正在匹配的当前条误跳。
                    // 且只有下一条真出现才跳，所以长 MV 间隔不会误跳后面的对话。
                    if (_status == MatchStatus.NameTagNotMatched && Set[dIndex].IsEmpty() &&
                        _emptyStuckFrames >= SkipProbeFrames)
                    {
                        // 在 [dIndex+1, dIndex+LookaheadWindow] 窗口里找"第一条此刻出现在画面里"的后续对话。
                        var found = -1;
                        var windowEnd = Math.Min(dIndex + LookaheadWindow, Set.Count - 1);
                        for (var k = dIndex + 1; k <= windowEnd; k++)
                        {
                            if (Set[k].Finished) continue;
                            if (ProbeDialog(frame, Set[k]))
                            {
                                found = k;
                                break;
                            }
                        }

                        if (found >= 0)
                        {
                            // 需连续 LookaheadConfirmHits 帧命中同一个目标才跳，避免单帧误判
                            if (found == _lookaheadTarget) _lookaheadHits++;
                            else { _lookaheadTarget = found; _lookaheadHits = 1; }

                            if (_lookaheadHits >= LookaheadConfirmHits)
                            {
                                Logger.Log(
                                    $"{nameof(DialogTemplateMatcher)} Frame {frameIndex} skip stuck dialogs idx={dIndex}..{found - 1} (idx={found} appeared)");
                                // 跳过 dIndex..found-1：都标记完成、留空集(下游忽略)
                                for (var s = dIndex; s < found; s++) Set[s].Finished = true;
                                // 起笔候选若落后于新的活动对话 found(属于被跳掉的那些条)则清掉；等于 found 的保留。
                                if (_onsetDialogIndex >= 0 && _onsetDialogIndex < found) ResetOnset();
                                _emptyStuckFrames = 0;
                                _consecutiveFailures = 0;
                                _useFallbackThreshold = false;
                                _lookaheadHits = 0;
                                _lookaheadTarget = -1;
                                TemplateMatchCachePool.NextDialog();
                                continue; // 本帧直接切到 found 去匹配
                            }
                        }
                        else
                        {
                            _lookaheadHits = 0;
                            _lookaheadTarget = -1;
                        }
                    }

                    if (_consecutiveFailures >= FallbackTriggerFrames && !_useFallbackThreshold)
                    {
                        _useFallbackThreshold = true;
                        _consecutiveFailures = 0;
                        continue;
                    }
                    _useFallbackThreshold = false;
                    return IsStatusMatched(firstStatus.Value);
                default:
                    // 防滞后(关键)：当前行只到 Matched1/Matched2 时，它是在用**短前缀**(content[..1]=『 或
                    // content[..3]=『……)硬撑——这类前缀在共享开头的相邻台词间不具区分度，会把"已经结束的
                    // 当前行"粘在**下一条**的台框上，使匹配整段滞后(实测 36/37 行滞后 7~14 秒，导致 38-40 被跳)。
                    // 某些行因首 6 字含特殊排版(！/空格/——)始终到不了 Matched3(6字指纹)，更会长期卡在 Matched2。
                    // 因此：只要当前行还没到 Matched3，且**下一条**已带名牌+6字内容指纹清晰出现在画面里，
                    // 就立刻定版当前行并前进，不被短前缀的"假命中"拖住。到了 Matched3 的行本身有区分度，
                    // 其结束由掉帧宽限的前进探测处理，不在此列。
                    if (_status is MatchStatus.DialogMatched1 or MatchStatus.DialogMatched2 &&
                        !Set[dIndex].IsEmpty())
                    {
                        var nextI = dIndex + 1;
                        while (nextI < Set.Count && Set[nextI].Finished) nextI++;
                        // 当前行卡在短前缀 hold 期间(其命中本身可疑)，每帧探下一条起笔并标记 duringHold——
                        // 这类候选不因"当前行继续命中"作废，由 0.35s 上限兜底，专治此路径下下一条起点晚约 6 字打字时长。
                        if (nextI < Set.Count) TryProbeOnset(frame, nextI, frameIndex, true);
                        if (nextI < Set.Count && ProbeDialog(frame, Set[nextI]))
                        {
                            if (_pendingFrames.Count > 0)
                            {
                                foreach (var (idx, pt) in _pendingFrames) Set[dIndex].Add(idx, pt);
                                _pendingFrames.Clear();
                            }

                            Set[dIndex].Finished = true;
                            _droppedGrace = 0;
                            _consecutiveFailures = 0;
                            _useFallbackThreshold = false;
                            _emptyStuckFrames = 0;
                            _lookaheadHits = 0;
                            _lookaheadTarget = -1;
                            TemplateMatchCachePool.NextDialog();
                            continue;
                        }
                    }

                    // 起笔回溯消费：仅在本条**第一帧**真实命中时(IsEmpty)把起点回填到最早探到起笔的帧，
                    // 抹平"下一条起点等宽限耗尽/指纹打全才被记录"的系统性轴前滞后。
                    if (Set[dIndex].IsEmpty() && !OnsetBackdateDisabled &&
                        _onsetDialogIndex == dIndex && _onsetFrame < frameIndex)
                    {
                        var from = Math.Max(_onsetFrame, frameIndex - OnsetMaxBackdateFrames);
                        // 不早于"上一条非空已定版对话最后真实帧的下一帧"，防导出事件重叠。
                        // 单位换算：Frames[].Index = 原始 frameIndex + FrameIndexOffset，故上一条最后真实帧的
                        // 原始帧号 = End().Index - FrameIndexOffset，回溯起点取其 +1。
                        for (var j = dIndex - 1; j >= 0; j--)
                        {
                            if (!Set[j].Finished || Set[j].IsEmpty()) continue;
                            from = Math.Max(from, Set[j].End().Index - DialogBaseFrameSet.FrameIndexOffset + 1);
                            break;
                        }

                        // 回填 [from, frameIndex) 的每一帧，保持 Frames 密集(SeparateDialogSet 按帧数切片依赖密集性)。
                        for (var f = from; f < frameIndex; f++) Set[dIndex].Add(f, matchResult.Point);
                        if (from < frameIndex)
                            Logger.Log(
                                $"{nameof(DialogTemplateMatcher)} Frame {frameIndex} onset backdate idx={dIndex} frames={frameIndex - from}");
                    }

                    // Real match (possibly a recovery after a dropped streak): commit
                    // any frames buffered during the grace window first, so the dialog
                    // spans the transient gap, then add this frame.
                    if (_pendingFrames.Count > 0)
                    {
                        foreach (var (idx, pt) in _pendingFrames) Set[dIndex].Add(idx, pt);
                        _pendingFrames.Clear();
                    }

                    Set[dIndex].Add(frameIndex, matchResult.Point);
                    // 记录匹配进度(0-based, 取刚加入帧的 Index=frameIndex+FrameIndexOffset)：首次达到 3/6 字指纹的帧，供分隔帧估算。
                    if (_status == MatchStatus.DialogMatched2 && Set[dIndex].FirstProgress2Frame < 0)
                        Set[dIndex].FirstProgress2Frame = Set[dIndex].End().Index;
                    else if (_status == MatchStatus.DialogMatched3 && Set[dIndex].FirstProgress3Frame < 0)
                        Set[dIndex].FirstProgress3Frame = Set[dIndex].End().Index;
                    _lastMatchedStatus = _status;
                    _droppedGrace = 0;
                    _consecutiveFailures = 0;
                    _useFallbackThreshold = false;
                    _emptyStuckFrames = 0;
                    _lookaheadHits = 0;
                    _lookaheadTarget = -1;
                    // 起笔候选作废：本条真实命中已消费/取代它的候选则清；否则(候选属别的条)只有非 hold 期录的才清——
                    // 宽限恢复证明画面还是本条的，那种候选无效；hold 期录的候选本就可疑但要留给下一条，由 0.35s 上限兜底。
                    if (_onsetDialogIndex == dIndex || !_onsetDuringHold) ResetOnset();
                    return IsStatusMatched(firstStatus.Value);
            }
        }

        return IsStatusMatched(firstStatus ?? MatchStatus.DialogNotMatched);
    }

    private static bool IsStatusMatched(MatchStatus status)
    {
        return status is MatchStatus.DialogMatched1
            or MatchStatus.DialogMatched2
            or MatchStatus.DialogMatched3;
    }

    private MatchResult MatchForDialog(Mat frame, DialogBaseFrameSet dialogBase, int frameIndex)
    {
        var lastStatus = _status;
        if (lastStatus is MatchStatus.DialogDropped && dialogBase.IsEmpty())
            lastStatus = MatchStatus.DialogNotMatched;
        Point point;
        if (dialogBase.Data.Shake || dialogBase.IsEmpty())
            point = DialogMatchNameTag(frame, dialogBase, frameIndex);
        else
            point = dialogBase.Start().Point;

        if (point.IsEmpty)
            return new MatchResult(Point.Empty, IsStatusMatched(lastStatus)
                ? MatchStatus.DialogDropped
                : MatchStatus.NameTagNotMatched);

        return new MatchResult(point, DialogMatchContent(frame, dialogBase, point, lastStatus, frameIndex));
    }

    private enum MatchStatus
    {
        NameTagNotMatched = -2,
        DialogNotMatched = 0,
        DialogMatched1 = 1,
        DialogMatched2 = 2,
        DialogMatched3 = 3,
        DialogDropped = -1
    }


    private struct MatchResult(Point point, MatchStatus status)
    {
        public readonly Point Point = point;
        public readonly MatchStatus Status = status;
    }
}