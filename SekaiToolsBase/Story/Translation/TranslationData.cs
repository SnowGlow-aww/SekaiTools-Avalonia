namespace SekaiToolsBase.Story.Translation;

public class TranslationData
{
    public readonly List<Translation> Translations = [];

    public TranslationData(string? filePath)
    {
        if (filePath is null) return;
        if (!File.Exists(filePath)) throw new Exception("File not found");

        var fileStrings = File.ReadAllLines(filePath).ToList();

        fileStrings = fileStrings.Where(l => l.Trim().Length > 0).Where(l => !l.StartsWith('#')).Select(l => l.Trim())
            .ToList();
        fileStrings.ForEach(line =>
        {
            Translations.Add(line.Contains('：')
                ? new DialogTranslate(line.Split('：', 2)[0], line.Split('：', 2)[1].Replace("…", "..."))
                : new EffectTranslate(line));
        });
    }

    public bool IsEmpty()
    {
        return Translations.Count == 0;
    }

    // 翻译文本来自不同团队和工作流，角色名、是否带全角冒号、行类型标记都可能
    // 与日文 scenario 不一致。这些差异不能阻止载入；Story 会按现有行尽力套用，
    // 缺失行保留原文，多余行忽略。保留此方法只是为了兼容旧调用方。
    public bool IsApplicable(GameScript.GameScript gameScript)
    {
        _ = gameScript;
        return true;
    }
}
