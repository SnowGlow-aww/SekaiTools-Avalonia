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
        UseSeparator = NeedSetSeparator;

        #region InitSeparatorContentIndex

        int separatorContentIndex;

        if (Data.BodyTranslated.Contains("\\R"))
            separatorContentIndex = Data.BodyTranslated
                .Replace("\n", "").Replace("\\N", "")
                .IndexOf("\\R", StringComparison.Ordinal);
        else if (Data.BodyTranslated.Count(c => c == '\n') == 1)
            separatorContentIndex = Data.BodyTranslated
                .IndexOf("\\R", StringComparison.Ordinal);
        else
            separatorContentIndex = Data.BodyTranslated.TrimAll().Length / 2;

        Separate.SeparatorContentIndex = separatorContentIndex;

        #endregion
    }

    public DialogStoryEvent Data { get; }
    public FrameRate Fps { get; }
    public List<DialogFrameResult> Frames { get; } = [];


    public bool IsJitter => Data.Shake;

    public bool Finished { get; set; }

    public bool NeedSetSeparator => Data.BodyTranslated != string.Empty &&
                                    Data.BodyOriginal.LineCount() == 3 &&
                                    Data.BodyTranslated.TrimAll().Length > 37;

    public bool UseSeparator { get; set; }

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