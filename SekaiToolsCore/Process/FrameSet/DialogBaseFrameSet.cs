using System.Drawing;
using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsBase.Utils;
using SekaiToolsCore.Process.Model;
using SekaiToolsCore.Utils;

namespace SekaiToolsCore.Process.FrameSet;

public class DialogFrameResult(int index, FrameRate fps, Point point) : ProcessFrame(index, fps)
{
    public Point Point => point;
}

public struct Separator
{
    public int SeparateFrame { get; set; }
    public int SeparatorContentIndex { get; set; }
}

public partial class DialogBaseFrameSet : BaseFrameSet
{
    // 单位换算锚点：Frames[].Index = 传入的原始 frameIndex + FrameIndexOffset(1-based PosFrames → 0-based 真帧号)。
    // 起笔回溯需在 DialogTemplateMatcher 里做同一换算，故设为 public 供其复用同一常量。
    public const int FrameIndexOffset = -1;

    public Separator Separate;

    // 逐帧匹配进度记录(0-based, 与 Frames[].Index 同单位)：首次达到 3字/6字 指纹的帧号，
    // 供分隔帧按实测打字速度估算(打字机打到某加权字长所用的帧数基线)。未达到时保持 -1。
    public int FirstProgress2Frame { get; set; } = -1; // 首次达到 Matched2(3字指纹) 的帧
    public int FirstProgress3Frame { get; set; } = -1; // 首次达到 Matched3(6字指纹) 的帧

    public DialogBaseFrameSet(DialogStoryEvent data, FrameRate fps)
    {
        Data = data;
        Fps = fps;
        ApplyTranslation(Data.BodyTranslated);
    }

    public DialogStoryEvent Data { get; }
    public FrameRate Fps { get; }
    public List<DialogFrameResult> Frames { get; } = [];


    public bool IsJitter => Data.Shake;

    public bool Finished { get; set; }

    public bool NeedSetSeparator => Data.BodyTranslated != string.Empty &&
                                    (Data.BodyOriginal.LineCount() == 3 ||
                                     Data.BodyTranslated.Split(new[] { "\\N", "\\n", "\n" }, StringSplitOptions.None).Length >= 3 ||
                                     Data.BodyTranslated.TrimAll().Length > 37);

    public bool UseSeparator { get; set; }

    /// <summary>
    /// 应用译文并同步分轴状态。真实换行、字面 \N/\n 与专用 \R 都会刷新文本分割点；
    /// useSeparator 非空时尊重 UI 的明确选择，否则保持旧版按过长阈值自动判断的语义。
    /// </summary>
    public void ApplyTranslation(string text, bool? useSeparator = null)
    {
        Data.SetTranslationContent(text);
        var contentLength = text.TrimAll().Length;
        var explicitSeparatorContentIndex = text.ExplicitSeparatorContentIndex();
        var validExplicitSeparatorContentIndex = explicitSeparatorContentIndex is > 0 &&
                                                  explicitSeparatorContentIndex < contentLength
            ? explicitSeparatorContentIndex
            : null;
        var existingSeparatorContentIndex = Separate.SeparatorContentIndex > 0 &&
                                            Separate.SeparatorContentIndex < contentLength
            ? Separate.SeparatorContentIndex
            : (int?)null;
        var separatorContentIndex = validExplicitSeparatorContentIndex
                                    ?? existingSeparatorContentIndex
                                    ?? contentLength / 2;
        separatorContentIndex = contentLength > 1
            ? Math.Clamp(separatorContentIndex, 1, contentLength - 1)
            : Math.Max(0, separatorContentIndex);

        // \N 也可能只是排版换行。构造 FrameSet/旧调用方未明确选择时，不能仅凭标记
        // 把短三行译文变成两条时间轴；Web/QuickEdit 的显式选择由 useSeparator 传入。
        UseSeparator = useSeparator ?? NeedSetSeparator;
        SetSeparator(Separate.SeparateFrame, separatorContentIndex);
    }

    public void InitSeparator()
    {
        Separate.SeparateFrame = UtilFunc.Middle(StartIndex() + 1, EndIndex() - 1,
            StartIndex() + Frames.Count / 2);
    }

    public void SetSeparator(int separateFrame, int separatorContentIndex)
    {
        Separate.SeparateFrame = separateFrame;
        Separate.SeparatorContentIndex = separatorContentIndex;
    }
}

public partial class DialogBaseFrameSet
{
    public override bool IsEmpty()
    {
        return Frames.Count == 0;
    }

    public override DialogFrameResult Start()
    {
        return Frames[0];
    }

    public override DialogFrameResult End()
    {
        return Frames[^1];
    }

    public void Add(int index, Point point)
    {
        Frames.Add(new DialogFrameResult(index + FrameIndexOffset, Fps, point));
    }
}
