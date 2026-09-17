using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsBase.Utils;

namespace SekaiToolsPlatform.ViewModels;

/// <summary>
/// 对话型行：既有发言人 (Original/Translated) 又有正文。
/// UI 端用 <c>TooLong</c> 布尔做颜色 converter。
/// </summary>
public class LineDialogModel : LineModel
{
    private readonly DialogStoryEvent _storyEvent;

    public LineDialogModel(DialogStoryEvent dialogStoryEvent)
    {
        _storyEvent = dialogStoryEvent;
        EndLine = dialogStoryEvent.CloseWindow;
        OriginalCharacter = dialogStoryEvent.CharacterOriginal;
        TranslatedCharacter = string.IsNullOrWhiteSpace(dialogStoryEvent.CharacterTranslated)
            ? dialogStoryEvent.CharacterOriginal
            : dialogStoryEvent.CharacterTranslated;
        OriginalContent = dialogStoryEvent.BodyOriginal;
        TranslatedContent = dialogStoryEvent.BodyTranslated;
        RefreshContentDiff();
    }

    /// <summary>
    /// 角色头像资源相对路径，例如 "chr_3.png"。
    /// 宿主 (Avalonia) 自行拼接 avares:// 前缀。
    /// </summary>
    public string Icon => _storyEvent.CharacterId is > 0 and <= 31
        ? $"chr_{_storyEvent.CharacterId}.png"
        : string.Empty;

    public bool HasIcon => !string.IsNullOrEmpty(Icon);
    public bool HasNoIcon => string.IsNullOrEmpty(Icon);

    public bool EndLine
    {
        get => GetProperty(false);
        set => SetProperty(value);
    }

    public string OriginalCharacter
    {
        get => GetProperty(string.Empty);
        set => SetProperty(value);
    }

    public string TranslatedCharacter
    {
        get => GetProperty(string.Empty);
        set
        {
            SetProperty(value);
            if (CharacterTranslateChangedEnabled)
                CharacterTranslateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string CharacterReference
    {
        get => GetProperty(string.Empty);
        set
        {
            SetProperty(value);
            OnPropertyChanged(nameof(HasCharacterReference));
        }
    }

    public bool HasCharacterReference => !string.IsNullOrEmpty(CharacterReference);

    public string OriginalContent
    {
        get => GetProperty(string.Empty);
        set => SetProperty(value);
    }

    public string TranslatedContent
    {
        get => GetProperty(string.Empty);
        set
        {
            var v = FormatContent(value);
            Check = CheckContent(v);
            SetProperty(v);
            LineCount = (v + "\n").LineCount();
            MaxLineLength = (int)Math.Ceiling((v + "\n").MaxLineVisualWeight());
            RefreshContentDiff();
            OnPropertyChanged(nameof(HasCheck));
            OnPropertyChanged(nameof(HasTranslatedContent));
            if (ContentTranslateChangedEnabled)
                ContentTranslateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ContentReference
    {
        get => GetProperty(string.Empty);
        set
        {
            SetProperty(value);
            OnPropertyChanged(nameof(HasContentReference));
            OnPropertyChanged(nameof(HasReferenceDiffView));
            RefreshContentDiff();
        }
    }

    public bool HasContentReference => !string.IsNullOrEmpty(ContentReference);
    public bool HasTranslatedContent => !string.IsNullOrEmpty(TranslatedContent);
    public bool HasCheck => !string.IsNullOrEmpty(Check);
    public bool HasReferenceDiffView => ShowReferenceDiff && HasContentReference;
    public DiffPartModel[] ContentReferenceDiffParts
    {
        get => GetProperty(Array.Empty<DiffPartModel>());
        private set => SetProperty(value);
    }

    public DiffPartModel[] TranslatedContentDiffParts
    {
        get => GetProperty(Array.Empty<DiffPartModel>());
        private set => SetProperty(value);
    }

    public int LineCount
    {
        get => GetProperty(0);
        set => SetProperty(value);
    }

    public int MaxLineLength
    {
        get => GetProperty(0);
        set
        {
            SetProperty(value);
            // 三行文案上限 45 字 / 段，否则 37。原作经验值，未来想动一定要看 SekaiText
            // 端的同款规则保持一致。
            TooLong = OriginalContent.LineCount() == 3 ? value > 45 : value > 37;
        }
    }

    public bool TooLong
    {
        get => GetProperty(false);
        set => SetProperty(value);
    }

    public string Check
    {
        get => GetProperty("");
        set => SetProperty(value);
    }

    public bool CharacterTranslateChangedEnabled { get; set; } = true;
    public bool ContentTranslateChangedEnabled { get; set; } = true;

    public event EventHandler? CharacterTranslateChanged;
    public event EventHandler? ContentTranslateChanged;

    public override string Result
    {
        get
        {
            var charResult = string.IsNullOrWhiteSpace(TranslatedCharacter) ? OriginalCharacter : TranslatedCharacter;
            var contentResult = string.IsNullOrWhiteSpace(TranslatedContent) ? OriginalContent : TranslatedContent;
            return $"{charResult}：{contentResult.Replace("\n", "\\N")}" + (EndLine ? "\n" : "");
        }
    }

    public DialogStoryEvent Export()
    {
        var dialog = new DialogStoryEvent(
            _storyEvent.Index, OriginalContent,
            _storyEvent.CharacterId, OriginalCharacter,
            _storyEvent.CloseWindow, _storyEvent.Shake);
        dialog.SetTranslation(TranslatedCharacter, TranslatedContent);
        return dialog;
    }

    private void RefreshContentDiff()
    {
        var (referParts, translatedParts) = TextDiffBuilder.Build(ContentReference, TranslatedContent);
        ContentReferenceDiffParts = referParts;
        TranslatedContentDiffParts = translatedParts;
    }

    protected override void OnShowReferenceDiffChanged()
    {
        OnPropertyChanged(nameof(HasReferenceDiffView));
    }

    private static string FormatContent(string content)
    {
        return content.Replace("…", "...")
            .Replace('(', '（')
            .Replace(')', '）')
            .Replace(',', '，')
            .Replace('?', '？')
            .Replace('!', '！')
            .Replace('欸', '诶');
    }

    private static string CheckContent(string content)
    {
        var result = "";
        var normalEnds = new[] { '、', '，', '。', '？', '！', '~', '♪', '☆', '.', '—' };
        var abnormalEnds = new[] { '）', '」', '』', '”' };
        var contentArray = content.Split("\n").Where(s => s.Length > 0).ToList();
        for (var i = 0; i < contentArray.Count; i++)
        {
            var line = contentArray[i];
            var lineRes = "";
            var last = line.Last();
            if (normalEnds.Contains(last) || abnormalEnds.Contains(last))
            {
                if (normalEnds.Contains(last) && (line.EndsWith(".，") || line.EndsWith(".。")))
                    lineRes += "【「……。」和「……，」只保留省略号】";
                else if (line.Length > 1 && abnormalEnds.Contains(line[^2]))
                    lineRes += "【句尾缺少逗号句号】";
            }
            else
            {
                lineRes += "【句尾缺少逗号句号】";
            }

            if (line.Contains('—') && line.Contains("——") &&
                line.Split("—").Length != line.Split("——").Length * 2 - 1)
                lineRes += "【破折号使用错误】";

            if (lineRes != "") result += $"行{i + 1}:{lineRes}\n";
        }

        return result.Trim();
    }
}
