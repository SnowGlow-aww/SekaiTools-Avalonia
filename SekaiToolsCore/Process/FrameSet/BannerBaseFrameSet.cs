using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsCore.Process.Model;

namespace SekaiToolsCore.Process.FrameSet;

public class BannerBaseFrameSet(BannerStoryEvent data, FrameRate fps) : BaseFrameSet
{
    private int _start = int.MaxValue, _end = int.MinValue;
    public BannerStoryEvent Data { get; } = data;
    private FrameRate Fps { get; } = fps;

    public bool Finished { get; set; }

    // 淡入起笔 / 淡出收尾（低阈值边界，见 BannerTemplateMatcher）。-1 = 未标定，
    // SubtitleMaker 回退到 [_start,_end]。匹配区间 [_start,_end] 保持正常阈值语义
    // 不变（预览 UI 仍用它）。
    public int OnsetFrame { get; set; } = -1;
    public int FadeTailFrame { get; set; } = -1;

    public void Add(int index)
    {
        if (_start > index) _start = index;
        if (_end < index) _end = index;
    }

    public override bool IsEmpty()
    {
        return _start == int.MaxValue && _end == int.MinValue;
    }

    public override IProcessFrame Start()
    {
        return new ProcessFrame(_start, Fps);
    }

    public override IProcessFrame End()
    {
        return new ProcessFrame(_end, Fps);
    }
}