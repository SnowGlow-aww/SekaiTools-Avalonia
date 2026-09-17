using CommunityToolkit.Mvvm.ComponentModel;
using SekaiToolsBase.Utils;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;

namespace SekaiToolsApp.ViewModels.LineCards;

/// <summary>
/// 对话行卡片 VM。
/// 长行会启用换行分隔符面板（<see cref="UseSeparator"/>），允许调整：
/// - <see cref="SeparatorContentIndex"/>：把译文按字符切成 part1/part2；
/// - <see cref="SeparateFrame"/>：决定换行发生在哪一帧。
/// 任何一项更改后会重新计算 <see cref="PromptWarning"/>，并同步到底层
/// <see cref="DialogBaseFrameSet"/>，导出阶段直接使用。
/// </summary>
public partial class DialogLineCardViewModel : LineCardViewModelBase
{
    /// <summary>
    /// 单字时间预估（毫秒）。导出时会读用户设置里的打字机参数。
    /// </summary>
    private const int CharTimeMs = 80;

    private readonly FrameRate _fps;

    public DialogBaseFrameSet Set { get; }
    public override BaseFrameSet FrameSet => Set;

    public DialogLineCardViewModel(DialogBaseFrameSet set)
    {
        set.InitSeparator();

        Set = set;
        _fps = set.Fps;

        _rawContent = set.Data.BodyOriginal;
        _translatedContent = set.Data.BodyTranslated.EscapedReturn();
        _useSeparator = set.UseSeparator;
        _separateFrame = set.Separate.SeparateFrame;
        _separatorContentIndex = set.Separate.SeparatorContentIndex;

        RecomputeContentParts();
        RecomputeSeparateTime();
        RecomputePromptWarning();
    }

    #region 显示字段

    public string CharacterOriginal => Set.Data.CharacterOriginal;
    public string CharacterTranslated => Set.Data.CharacterTranslated;

    /// <summary>角色名优先用译名，没有再回落到原名。</summary>
    public string CharacterDisplay => string.IsNullOrEmpty(CharacterTranslated)
        ? CharacterOriginal
        : CharacterTranslated;

    [ObservableProperty] private string _rawContent = string.Empty;

    [ObservableProperty] private string _translatedContent = string.Empty;

    partial void OnTranslatedContentChanged(string value)
    {
        Set.Data.BodyTranslated = value;
        OnPropertyChanged(nameof(SeparatorContentIndexLimit));
        RecomputeContentParts();
        RecomputePromptWarning();
    }

    public void ApplyQuickEdit(string translatedContent, bool useReturn)
    {
        TranslatedContent = translatedContent;
        UseSeparator = useReturn;
        if (translatedContent.Contains('\n'))
            SeparatorContentIndex = translatedContent.Split('\n')[0].Length;
    }

    public bool IsShaking => Set.Data.Shake;

    /// <summary>过长行（需要分行的对话）。<see cref="DialogBaseFrameSet.NeedSetSeparator"/>。</summary>
    public bool IsTooLongLine => Set.NeedSetSeparator;

    public int StartFrame => Set.StartIndex();
    public int EndFrame => Set.EndIndex();
    public string StartTime => _fps.TimeAtFrame(StartFrame).GetAssFormatted();
    public string EndTime => _fps.TimeAtFrame(EndFrame).GetAssFormatted();

    #endregion

    #region 分隔符

    [ObservableProperty] private bool _useSeparator;

    partial void OnUseSeparatorChanged(bool value)
    {
        Set.UseSeparator = value;
    }

    public int SeparatorContentIndexLimit
    {
        get
        {
            var len = Set.Data.BodyTranslated.TrimAll().Length - 1;
            return len < 1 ? 1 : len;
        }
    }

    [ObservableProperty] private int _separatorContentIndex;

    partial void OnSeparatorContentIndexChanged(int value)
    {
        Set.SetSeparator(SeparateFrame, value);
        RecomputeContentParts();
        RecomputePromptWarning();
    }

    [ObservableProperty] private int _separateFrame;

    partial void OnSeparateFrameChanged(int value)
    {
        Set.SetSeparator(value, SeparatorContentIndex);
        RecomputeSeparateTime();
        RecomputePromptWarning();
    }

    [ObservableProperty] private string _separateTime = string.Empty;

    [ObservableProperty] private string _contentPart1 = string.Empty;
    [ObservableProperty] private string _contentPart2 = string.Empty;

    [ObservableProperty] private string _promptWarning = string.Empty;

    public bool HasPromptWarning => !string.IsNullOrEmpty(PromptWarning);

    partial void OnPromptWarningChanged(string value)
    {
        OnPropertyChanged(nameof(HasPromptWarning));
    }

    private void RecomputeContentParts()
    {
        var trimmed = Set.Data.BodyTranslated.TrimAll();
        if (trimmed.Length == 0)
        {
            ContentPart1 = string.Empty;
            ContentPart2 = string.Empty;
            return;
        }

        var idx = SeparatorContentIndex;
        if (idx < 0) idx = 0;
        if (idx > trimmed.Length) idx = trimmed.Length;

        ContentPart1 = trimmed[..idx];
        ContentPart2 = trimmed[idx..];
    }

    private void RecomputeSeparateTime()
    {
        SeparateTime = new ProcessFrame(SeparateFrame, _fps).StartTime();
    }

    private void RecomputePromptWarning()
    {
        if (_fps.Fps() <= 0)
        {
            PromptWarning = string.Empty;
            return;
        }

        var frameDurationMs = 1000.0 / _fps.Fps();
        var part1FrameDurationMs = (SeparateFrame - Set.StartIndex()) * frameDurationMs;
        if (ContentPart1.VisualWeight() * CharTimeMs > part1FrameDurationMs)
        {
            PromptWarning = "第一行文字将无法显示完全";
            return;
        }

        var part2FrameDurationMs = (Set.EndIndex() - SeparateFrame) * frameDurationMs;
        if (ContentPart2.VisualWeight() * CharTimeMs > part2FrameDurationMs)
        {
            PromptWarning = "第二行文字将无法显示完全";
            return;
        }

        PromptWarning = string.Empty;
    }

    #endregion
}
