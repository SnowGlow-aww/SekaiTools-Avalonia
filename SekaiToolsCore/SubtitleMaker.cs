using System.Drawing;
using System.Text;
using SekaiToolsBase;
using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsBase.SubStationAlpha;
using SekaiToolsBase.SubStationAlpha.AssDraw;
using SekaiToolsBase.SubStationAlpha.Tag;
using SekaiToolsBase.SubStationAlpha.Tag.Modded;
using SekaiToolsBase.Utils;
using SekaiToolsCore.Match.TemplateMatcher;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;
using SekaiToolsCore.Utils;
using SubtitleEvent = SekaiToolsBase.SubStationAlpha.Event;

namespace SekaiToolsCore;

public class SubtitleMaker(VideoInfo videoInfo, TemplateManager templateManager, Config config)
{
    private readonly List<Style> _styles = [];

    private Point _nameTagPosition = new(0, 0);
    private StyleFontConfig StyleFontConfig { get; } = config.StyleFontConfig;
    private TypewriterSetting TypewriterSetting { get; } = config.TyperSetting;

    private ExportStyleConfig ExportStyleConfig { get; } = config.ExportStyleConfig;

    public Subtitle Make(
        List<DialogBaseFrameSet> dialogList,
        List<BannerBaseFrameSet> bannerList,
        List<MarkerBaseFrameSet> markerList)
    {
        var events = new List<SubtitleEvent>();

        if (dialogList.Count != 0)
        {
            _nameTagPosition = dialogList[0].Frames[0].Point;
            _styles.AddRange(MakeDialogStyles());
            events.AddRange(MakeDialogEvents(dialogList));
        }

        if (bannerList.Count != 0)
        {
            _styles.AddRange(MakeBannerStyles());
            events.AddRange(MakeBannerEvents(bannerList));
        }

        if (markerList.Count != 0)
        {
            _styles.AddRange(MakeMarkerStyles());
            events.AddRange(MakeMarkerEvents(markerList));
        }

        // if (!ExportStyleConfig) events.RemoveAll(e => e.Type == "Comment");
        if (!ExportStyleConfig.ExportLine1) events.RemoveAll(e => e.Style == "Line1");
        if (!ExportStyleConfig.ExportLine2) events.RemoveAll(e => e.Style == "Line2");
        if (!ExportStyleConfig.ExportLine3) events.RemoveAll(e => e.Style == "Line3");
        if (!ExportStyleConfig.ExportCharacter) events.RemoveAll(e => e.Style == "Character");
        if (!ExportStyleConfig.ExportBannerMask) events.RemoveAll(e => e.Style == "BannerMask");
        if (!ExportStyleConfig.ExportBannerText) events.RemoveAll(e => e.Style == "BannerText");
        if (!ExportStyleConfig.ExportMarkerMask) events.RemoveAll(e => e.Style == "MarkerMask");
        if (!ExportStyleConfig.ExportMarkerText) events.RemoveAll(e => e.Style == "MarkerText");
        if (!ExportStyleConfig.ExportScreenComment) events.RemoveAll(e => e.Style == "Screen");

        return new Subtitle(
            new ScriptInfo(videoInfo.Resolution.Width, videoInfo.Resolution.Height),
            new Garbage(Path.GetFileName(videoInfo.Path), Path.GetFileName(videoInfo.Path)),
            new Styles(_styles.ToArray()),
            new Events(events.ToArray())
        );
    }

    #region Dialog

    private GaMat GetNameTag(string name)
    {
        return new GaMat(templateManager.GetTemplate(TemplateUsage.DialogNameTag, name));
    }

    private static Queue<char> FormatDialogBodyArr(string body)
    {
        // 换行一律保留：三行文本以前会被整体去换行，导致成品只剩一行长条、
        // 后处理也无法按 \N 数命中「3行」样式（用户反馈）。
        var bodyCopy = body
            .Replace("…", "...")
            .Replace("... ...", "......")
            .Replace("\\N", "\n").Replace("\\n", "\n");
        var queue = new Queue<char>();
        foreach (var c in bodyCopy) queue.Enqueue(c);
        return queue;
    }

    private string MakeDialogTypewriter(string body)
    {
        var queue = FormatDialogBodyArr(body);
        var fadeTime = TypewriterSetting.FadeTime;
        var charTime = TypewriterSetting.CharTime;
        if (fadeTime <= 0 && charTime <= 0)
            return string.Join("", queue);

        var sb = new StringBuilder();
        sb.Append(queue.Dequeue());

        var nextStart = 0;
        foreach (var s in queue)
        {
            var ft = fadeTime / (char.IsAscii(s) ? 2 : 1);
            var ct = charTime / (char.IsAscii(s) ? 2 : 1);

            var start = nextStart + (s == '\n' ? 300 : 0);
            var alphaTag = $@"{{\alphaFF\t({start},{start + ft},1,\alpha0)}}";
            sb.Append(alphaTag);
            sb.Append(s == '\n' ? "\\N" : s);
            nextStart = start + ct;
        }

        return sb.ToString();
    }

    private string MakeDialogTypewriter(string body, int frameCount)
    {
        var queue = FormatDialogBodyArr(body);
        var fadeTime = TypewriterSetting.FadeTime;
        var charTime = TypewriterSetting.CharTime;
        if (fadeTime <= 0 && charTime <= 0)
            return string.Join("", queue);

        var nowTime = (int)(1000 / videoInfo.Fps.Fps() * frameCount);
        var charTimeEnd = 0;
        var sb = new StringBuilder();
        sb.Append(queue.Dequeue());
        while (queue.Count != 0)
        {
            var s = queue.Dequeue();
            var ft = fadeTime / (char.IsAscii(s) ? 2 : 1);
            var ct = charTime / (char.IsAscii(s) ? 2 : 1);

            charTimeEnd += ct;
            charTimeEnd += s == '\n' ? 300 : 0;

            int alphaPercent;
            if (nowTime <= charTimeEnd - ft)
                alphaPercent = 100;
            else if (nowTime < charTimeEnd)
                alphaPercent = (charTimeEnd - nowTime) * 100 / ft;
            else
                alphaPercent = 0;

            var alphaTag = $@"{{\alpha{Convert.ToString((int)(255 * alphaPercent / 100.0), 16).ToUpper()}}}";
            if (alphaPercent != 0) sb.Append(alphaTag);
            sb.Append(s == '\n' ? "\\N" : s);
            if (alphaPercent == 100) break;
        }

        foreach (var s in queue) sb.Append(s == '\n' ? "\\N" : s);

        return sb.ToString();
    }

    private List<Style> MakeDialogStyles()
    {
        var fontsize = (int)((videoInfo.FrameRatio > 16.0 / 9
            ? videoInfo.Resolution.Height * 0.043
            : videoInfo.Resolution.Width * 0.024) * (70 / 61D));

        var outlineSize = (int)Math.Ceiling(fontsize / 15.0);
        var marginV = _nameTagPosition.Y + (int)(fontsize * 2.3);
        var marginH = _nameTagPosition.X + (int)(fontsize * 0.4);

        var charaFontsize = (int)(fontsize * 0.9);
        var charaOutlineSize = (int)Math.Ceiling(charaFontsize / 15.0);


        var blackColor = new AlphaColor(0, 255, 255, 255);
        var outlineColor = new AlphaColor(50, 73, 71, 102);
        var result = new List<Style>
        {
            new("Line1", StyleFontConfig.DialogFontFamily, fontsize,
                blackColor, outlineColour: outlineColor,
                outline: outlineSize, shadow: 0, alignment: 7, marginL: marginH, marginR: marginH, marginV: marginV),

            new("Line2", StyleFontConfig.DialogFontFamily, fontsize,
                blackColor, outlineColour: outlineColor,
                outline: outlineSize, shadow: 0, alignment: 7, marginL: marginH, marginR: marginH,
                marginV: marginV + (int)(fontsize * 1.01)),

            new("Line3", StyleFontConfig.DialogFontFamily, fontsize,
                blackColor, outlineColour: outlineColor,
                outline: outlineSize, shadow: 0, alignment: 7, marginL: marginH, marginR: marginH,
                marginV: marginV + (int)(fontsize * 1.01 * 2)),

            new("Character", StyleFontConfig.DialogFontFamily, charaFontsize,
                blackColor, outlineColour: outlineColor,
                outline: charaOutlineSize, shadow: 0, alignment: 7),

            new("Screen", StyleFontConfig.DialogFontFamily, charaFontsize,
                blackColor, outlineColour: outlineColor,
                outline: outlineSize, shadow: 0, alignment: 7)
        };

        return result;
    }

    private List<SubtitleEvent> MakeDialogEvents(List<DialogBaseFrameSet> dialogList)
    {
        var result = new List<SubtitleEvent>();

        var dialogIndex = 0;
        foreach (var set in dialogList)
        {
            dialogIndex++;
            var dialogEvents = new List<SubtitleEvent>();
            var dialogMarker = $"-----  {dialogIndex:000}  -----";
            dialogEvents.Add(SubtitleEvent.Comment($"{dialogMarker}  Start",
                set.StartTime(), set.EndTime(), "Screen"));

            if (set.UseSeparator)
            {
                // 无头内核(SubtitleHandler)不像 Avalonia GUI 那样在卡片构造时调用 InitSeparator，
                // SeparateFrame 会停留在 0；于是 SeparateDialogSet 里 sepCount = 0 - StartIndex() 为负，
                // Frames[..sepCount] 取负长度抛 ArgumentOutOfRangeException，直接导致「无法导出 ass」。
                // 导出前确保分隔帧是合法的中间帧；GUI 已设的合法值(落在区间内)不会被覆盖。
                if (set.Separate.SeparateFrame <= set.StartIndex() ||
                    set.Separate.SeparateFrame >= set.EndIndex())
                    EstimateSeparator(set);
                var items = SeparateDialogSet(set);
                dialogEvents.Add(SubtitleEvent.Comment($"{dialogMarker}  Line 1 ↓",
                    set.StartTime(), set.EndTime(), "Screen"));

                dialogEvents.AddRange(GenerateDialogEvent(items[0]));

                dialogEvents.Add(SubtitleEvent.Comment($"{dialogMarker}  Line 2 ↓",
                    set.StartTime(), set.EndTime(), "Screen"));

                dialogEvents.AddRange(GenerateDialogEvent(items[1]));
            }
            else
            {
                if (set.Data.BodyTranslated.LineCount() == 3)
                    set.Data.SetTranslationContent(set.Data.BodyTranslated.TrimAll());
                dialogEvents.AddRange(GenerateDialogEvent(set));
            }

            if (dialogEvents.Count > 3)
            {
                dialogEvents.Add(SubtitleEvent.Comment($"{dialogMarker}  Debug ↓",
                    set.StartTime(), set.EndTime(), "Screen"));
                var t = GenerateNoneJitterDialogEvents(set)
                    .Select(item => item.ToComment()).ToList();
                dialogEvents.AddRange(t);
            }

            dialogEvents.Add(SubtitleEvent.Comment($"{dialogMarker}  End",
                set.StartTime(), set.EndTime(), "Screen"));

            result.AddRange(dialogEvents);
        }

        return result;


        List<DialogBaseFrameSet> SeparateDialogSet(DialogBaseFrameSet dialogBaseFrameSet)
        {
            var sepCount = dialogBaseFrameSet.Separate.SeparateFrame - dialogBaseFrameSet.StartIndex();

            var sepSet1 = new DialogBaseFrameSet((DialogStoryEvent)dialogBaseFrameSet.Data.Clone(), videoInfo.Fps);
            var sepSet2 = new DialogBaseFrameSet((DialogStoryEvent)dialogBaseFrameSet.Data.Clone(), videoInfo.Fps);

            sepSet1.Frames.AddRange(dialogBaseFrameSet.Frames[..sepCount]);
            sepSet2.Frames.AddRange(dialogBaseFrameSet.Frames[sepCount..]);

            var content = dialogBaseFrameSet.Data.FinalContent.TrimAll();
            sepSet1.Data.BodyTranslated = content[..dialogBaseFrameSet.Separate.SeparatorContentIndex];
            sepSet2.Data.BodyTranslated = content[dialogBaseFrameSet.Separate.SeparatorContentIndex..];

            return [sepSet1, sepSet2];
        }

        IEnumerable<SubtitleEvent> GenerateDialogEvent(DialogBaseFrameSet set)
        {
            var subtitleEventItems = new List<SubtitleEvent>();
            subtitleEventItems.AddRange(set.IsJitter
                ? GenerateJitterDialogEvents(set)
                : GenerateNoneJitterDialogEvents(set));
            return subtitleEventItems;
        }

        IEnumerable<SubtitleEvent> GenerateNoneJitterDialogEvents(DialogBaseFrameSet dialogBaseFrameSet)
        {
            var content = dialogBaseFrameSet.Data.FinalContent;
            var characterName = dialogBaseFrameSet.Data.FinalCharacter;
            var originLineCount = dialogBaseFrameSet.Data.BodyOriginal.Split("\n").Length;
            var styleName = "Line" + originLineCount;

            var startTime = dialogBaseFrameSet.StartTime();
            var endTime = dialogBaseFrameSet.EndTime();

            var body = MakeDialogTypewriter(content);

            var dialogItem = SubtitleEvent.Dialog(body, startTime, endTime, styleName);

            var characterItemPosition =
                dialogBaseFrameSet.Start().Point +
                new Size(GetNameTag(dialogBaseFrameSet.Data.CharacterOriginal).Size.Width + 10, 0);
            var characterItemPositionTag = $@"{{\pos({characterItemPosition.X},{characterItemPosition.Y})}}";
            var characterItem = SubtitleEvent.Dialog(
                characterItemPositionTag + characterName, startTime, endTime, "Character");
            // if (characterName == "") characterItem = characterItem.ToComment();

            return [characterItem, dialogItem];
        }

        IEnumerable<SubtitleEvent> GenerateJitterDialogEvents(DialogBaseFrameSet dialogBaseFrameSet)
        {
            var content = dialogBaseFrameSet.Data.FinalContent;
            var characterName = dialogBaseFrameSet.Data.FinalCharacter;
            var originLineCount = dialogBaseFrameSet.Data.BodyOriginal.Split("\n").Length;

            var styleName = "Line" + originLineCount;
            var styles = MakeDialogStyles();
            var style = styles.Find(s => s.Name == styleName)!;

            var constPosition = dialogBaseFrameSet.Start().Point;
            var lastPosition = new Point(0, 0);
            var dialogEvents = new List<SubtitleEvent>();
            var characterEvents = new List<SubtitleEvent>();
            foreach (var frame in dialogBaseFrameSet.Frames)
            {
                var x = style.MarginL;
                var y = style.MarginV;
                x += frame.Point.X - constPosition.X;
                y += frame.Point.Y - constPosition.Y;
                var body = @$"{{\pos({x},{y})}}"
                           + MakeDialogTypewriter(content, frame.Index - dialogBaseFrameSet.StartIndex());

                if (lastPosition.X == x && lastPosition.Y == y && body == dialogEvents[^1].Text)
                    dialogEvents[^1].End = frame.EndTime();
                else
                    dialogEvents.Add(SubtitleEvent.Dialog(body, frame.StartTime(), frame.EndTime(), styleName));

                if (lastPosition.X == x && lastPosition.Y == y && body == characterEvents[^1].Text)
                {
                    characterEvents[^1].End = frame.EndTime();
                }
                else
                {
                    var offset = GetNameTag(dialogBaseFrameSet.Data.CharacterOriginal).Size.Width;
                    var position = frame.Point + new Size(offset + 10, 0);
                    var tag = $@"{{\pos({position.X},{position.Y})}}";

                    var characterItem = SubtitleEvent.Dialog(
                        tag + characterName, frame.StartTime(), frame.EndTime(), "Character");
                    // if (characterName == "") characterItem = characterItem.ToComment();
                    characterEvents.Add(characterItem);
                }

                lastPosition = new Point(x, y);
            }

            var returnVal = new List<SubtitleEvent>();
            returnVal.AddRange(dialogEvents);
            returnVal.AddRange(characterEvents);
            return returnVal;
        }
    }

    // 分隔帧估算：三行长台词的 Line1→Line2 切换时刻，应≈游戏打字机打到"译文分割点对应的原文位置"的时刻，
    // 而非旧的 InitSeparator 用的"显示时长中点"(配音长的行会让切换严重偏晚)。用逐帧记录的打字进度基线换算。
    // public：无头引擎(subtitle.lines/estimateSeparator)需要在导出之外复用同一估算，保证 UI 默认值与导出一致。
    public void EstimateSeparator(DialogBaseFrameSet set)
    {
        // A/B 与线上兜底：显式关闭时回退旧的"显示时长中点"分隔。
        if (Environment.GetEnvironmentVariable("DisableSeparatorEstimate") == "true")
        {
            set.InitSeparator();
            return;
        }

        // 加权字长：ASCII(半角)记 0.5、其余记 1，忽略换行——打字机对半角是半速；口径与状态机的指纹加权一致。
        static double Weight(string s)
        {
            var w = 0d;
            foreach (var c in s)
            {
                if (c is '\n' or '\r') continue;
                w += char.IsAscii(c) ? 0.5 : 1;
            }

            return w;
        }

        var translated = set.Data.FinalContent.TrimAll(); // 译文口径与 SeparateDialogSet 一致
        var original = set.Data.BodyOriginal;
        var sepIdx = set.Separate.SeparatorContentIndex;

        var wTransAll = Weight(translated);
        if (sepIdx <= 0 || sepIdx >= translated.Length || wTransAll <= 0)
        {
            set.InitSeparator();
            return;
        }

        var ratio = Weight(translated[..sepIdx]) / wTransAll;
        if (ratio is <= 0 or >= 1)
        {
            set.InitSeparator();
            return;
        }

        var wOrigAll = Weight(original);
        if (wOrigAll <= 0)
        {
            set.InitSeparator();
            return;
        }

        var fps = videoInfo.Fps.Fps();
        var startIndex = set.StartIndex();
        var wt = ratio * wOrigAll; // 目标：分隔点对应的原文加权位置
        var w1 = original.Length > 0 ? Weight(original[..1]) : 0; // 首字加权长(起点锚对应的加权位置)

        // 打字机跨过换行时停顿 300ms。统计原文加权位置区间 [wFrom, wTo) 内的换行停顿数(用于斜率区间扣除
        // 与锚点→分隔点的补偿)，避免同一停顿既被实测区间吃进斜率、又被显式加回而重复计入。
        double PausesBetween(double wFrom, double wTo)
        {
            if (wTo < wFrom) return -PausesBetween(wTo, wFrom);
            var count = 0;
            var walked = 0d;
            foreach (var c in original)
            {
                if (walked >= wTo) break;
                if (c == '\n')
                {
                    if (walked >= wFrom) count++;
                    continue;
                }

                if (c == '\r') continue;
                walked += char.IsAscii(c) ? 0.5 : 1;
            }

            return count;
        }

        // 打字速度先验(帧/加权单位)：CharTime 与游戏打字速度同源；CharTime 被关(≤0)时用游戏典型值 80ms。
        var ctMs = TypewriterSetting.CharTime > 0 ? TypewriterSetting.CharTime : 80;
        var ctSlope = ctMs / 1000.0 * fps;

        // 实测斜率只能用 P2→P3 两点(同为真实命中帧)：StartIndex 可能被起笔回溯改早、与 P2/P3 不同源，
        // 用它当锚会把斜率放大 backdate 帧数。另一头，起点检测偏晚(如快速连续对话)时状态机每帧只升一级
        // "追赶"，P3-P2 会缩成 1-2 帧、算出远小于真实的假斜率——所以实测值必须过先验合理性门控
        // (0.4x~2.5x CharTime)，不可信则回退先验；先验锚定在(回溯后≈真实打字起点的)StartIndex 上。
        var slope = ctSlope;
        var baseline = "CharTime";
        var anchorFrame = (double)startIndex;
        var anchorW = w1;
        if (set.FirstProgress2Frame >= 0 && set.FirstProgress3Frame > set.FirstProgress2Frame)
        {
            var w2 = Weight(original[..Math.Min(3, original.Length)]);
            var w3 = Weight(original[..Math.Min(6, original.Length)]);
            var span = w3 - w2;
            if (span >= 0.5)
            {
                var raw = (set.FirstProgress3Frame - set.FirstProgress2Frame - PausesBetween(w2, w3) * 0.3 * fps) /
                          span;
                if (raw >= 0.4 * ctSlope && raw <= 2.5 * ctSlope)
                {
                    slope = raw;
                    baseline = "P2P3";
                    anchorFrame = set.FirstProgress3Frame;
                    anchorW = w3;
                }
            }
        }

        var est = anchorFrame + (wt - anchorW) * slope + PausesBetween(anchorW, wt) * 0.3 * fps;
        var sepFrame = UtilFunc.Middle(startIndex + 1, (int)Math.Round(est), set.EndIndex() - 1);

        // 与旧的"显示时长中点"对照，便于 A/B 观察。
        var oldMid = UtilFunc.Middle(startIndex + 1, set.EndIndex() - 1, startIndex + set.Frames.Count / 2);
        Logger.Log(
            $"{nameof(SubtitleMaker)} EstimateSeparator start={startIndex} est={sepFrame} (raw={est:F1}) oldMid={oldMid} " +
            $"ratio={ratio:F3} baseline={baseline}");

        set.SetSeparator(sepFrame, sepIdx);
    }

    #endregion

    #region Banner

    private List<SubtitleEvent> MakeBannerEvents(List<BannerBaseFrameSet> bannerList)
    {
        var result = new List<SubtitleEvent>();
        var count = 0;
        foreach (var set in bannerList)
        {
            count++;

            var events = new List<SubtitleEvent>();
            var markerString = $"-----  {count:000}  -----";
            events.Add(SubtitleEvent.Comment($"{markerString}  Start", set.StartTime(), set.EndTime(), "Screen"));
            events.AddRange(GenerateBannerEvent(set));
            events.Add(SubtitleEvent.Comment($"{markerString}  End", set.StartTime(), set.EndTime(), "Screen"));
            result.AddRange(events);
        }

        return result;

        IEnumerable<SubtitleEvent> GenerateBannerEvent(BannerBaseFrameSet set)
        {
            var offset = TemplateManager.GetFontSize(videoInfo.Resolution);
            var center = videoInfo.Resolution.Center();
            center.Y += (int)(offset * 2.5);
            center.Y = center.Y / 20 * 20;
            var content = set.Data.FinalContent;
            var startTime = set.StartTime();
            var endTime = set.EndTime();

            var maskFade = Tags.Fade(set.Data.TotalIndex == 0 ? 300 : 100, 200);
            var maskBlur = maskFade + Tags.Blur(30) + Tags.Anchor(7) + Tags.Paint(1);

            var body = maskFade + Tags.Anchor(5) + Tags.FontSize(offset) +
                       Tags.Move(center.X - offset / 3, center.Y, center.X, center.Y, 0, 200) + content;

            var contentItem = SubtitleEvent.Dialog(body, startTime, endTime, "BannerText");

            var cRec = UtilFunc.FromCenter(center,
                new Size(offset * 12 / 20 * 20, (int)(offset * 1.4) / 20 * 20));
            var mRec = UtilFunc.FromCenter(center,
                new Size(offset * 12 / 20 * 20, (int)(offset * 2.0) / 20 * 20));
            var mask = AssDraw.Rectangle(mRec).ToString();
            var clipLeft = (
                    Tags.Clip(0, cRec.Y, cRec.X, cRec.Y + cRec.Height) +
                    Tags.Transformation(
                        0, 200, Tags.Clip(0, cRec.Y, cRec.X + cRec.Width, cRec.Y + cRec.Height)))
                .ToString();

            var clipRight = (
                    Tags.Clip(cRec.X, cRec.Y, videoInfo.Resolution.Width, cRec.Y + cRec.Height) +
                    Tags.Transformation(0, 200,
                        Tags.Clip(cRec.X + cRec.Width, cRec.Y,
                            videoInfo.Resolution.Width, cRec.Y + cRec.Height)))
                .ToString();


            var shift = ModdedTags.LeadingHorizontal(offset * 5) +
                        Tags.Transformation(0, 200, ModdedTags.LeadingHorizontal(0));


            var maskItem1 =
                SubtitleEvent.Dialog(maskBlur + clipLeft + mask, startTime, endTime, "BannerMask");
            var maskItem2 =
                SubtitleEvent.Dialog(maskBlur + clipRight + shift + mask, startTime, endTime, "BannerMask");

            return [maskItem1, maskItem2, contentItem];
        }
    }

    private List<Style> MakeBannerStyles()
    {
        var result = new List<Style>();
        var fontsize = (int)((videoInfo.FrameRatio > 16.0 / 9
            ? videoInfo.Resolution.Height * 0.043
            : videoInfo.Resolution.Width * 0.024) * (70 / 61D));


        var whiteColor = new AlphaColor(0, 255, 255, 255);
        var outlineColor = new AlphaColor(30, 95, 92, 123);
        result.Add(new Style("BannerMask", StyleFontConfig.BannerFontFamily, fontsize, outlineColor,
            outlineColour: outlineColor,
            outline: 0, shadow: 0, alignment: 7));
        result.Add(new Style("BannerText", StyleFontConfig.BannerFontFamily, fontsize, whiteColor,
            outlineColour: outlineColor,
            outline: 0, shadow: 0, alignment: 7));
        return result;
    }

    #endregion

    #region Marker

    private List<SubtitleEvent> MakeMarkerEvents(List<MarkerBaseFrameSet> markerList)
    {
        List<SubtitleEvent> result = [];
        var count = 0;
        foreach (var set in markerList)
        {
            count++;

            var events = new List<SubtitleEvent>();
            var markerString = $"-----  {count:000}  -----";
            events.Add(SubtitleEvent.Comment($"{markerString}  Start", set.StartTime(), set.EndTime(), "Screen"));
            events.AddRange(GenerateMarkerEvent(set));
            events.Add(SubtitleEvent.Comment($"{markerString}  End", set.StartTime(), set.EndTime(), "Screen"));
            result.AddRange(events);
        }

        return result;

        List<SubtitleEvent> GenerateMarkerEvent(MarkerBaseFrameSet baseFrameSet)
        {
            List<SubtitleEvent> markerEventText = [];
            List<SubtitleEvent> markerEventMask = [];
            var content = baseFrameSet.Data.FinalContent;
            var contentLength = (content.Length + content.Count(c => c > 127)) / 2;

            foreach (var frame in baseFrameSet.Frames)
            {
                var startTime = frame.StartTime();
                var endTime = frame.EndTime();
                var position = frame.Point;
                var fs = _styles.First(style => style.Name == "MarkerText").Fontsize;
                var tagText = new Tags(Tags.Position(position.X, position.Y + (int)(fs * 1.6)));

                var tagMask = new Tags(
                    Tags.Bord(0), Tags.Blur(50), Tags.Clip(
                        new Point(0, position.Y + (int)(fs * 1.6)),
                        new Point((int)(fs * contentLength * 1.5), position.Y + (int)(fs * 2.65))),
                    Tags.Paint(1)
                );
                var mask = AssDraw.Rectangle(
                    new Rectangle(new Point(-50, 0), new Size(100 + fs * contentLength + position.X, fs * 4))
                ).ToString();


                var maskText = tagMask + mask;
                var bodyText = tagText + content;
                if (markerEventMask.Count > 0 && markerEventText.Count > 0)
                    if (markerEventMask[^1].Text == maskText && markerEventText[^1].Text == bodyText)
                    {
                        markerEventMask[^1].End = endTime;
                        markerEventText[^1].End = endTime;
                        continue;
                    }

                markerEventMask.Add(
                    SubtitleEvent.Dialog(maskText, startTime, endTime, "MarkerMask"));
                markerEventText.Add(
                    SubtitleEvent.Dialog(bodyText, startTime, endTime, "MarkerText"));
            }

            return [.. markerEventMask, .. markerEventText];
        }
    }

    private List<Style> MakeMarkerStyles()
    {
        var result = new List<Style>();
        var fontsize = (int)((videoInfo.FrameRatio > 16.0 / 9
            ? videoInfo.Resolution.Height * 0.043
            : videoInfo.Resolution.Width * 0.024) * (70 / 61D));

        var whiteColor = new AlphaColor(0, 255, 255, 255);
        var outlineColor = new AlphaColor(30, 95, 92, 123);
        result.Add(new Style("MarkerMask", StyleFontConfig.MarkerFontFamily, fontsize, outlineColor,
            outlineColour: outlineColor,
            outline: 0, shadow: 0, alignment: 7));
        result.Add(new Style("MarkerText", StyleFontConfig.MarkerFontFamily, fontsize, whiteColor,
            outlineColour: outlineColor,
            outline: 0, shadow: 0, alignment: 7));
        return result;
    }

    #endregion
}
