using SekaiToolsBase.GameScript;
using SekaiToolsBase.Story;
using SekaiToolsBase.Story.Translation;

namespace SekaiToolsPlatform.Services;

/// <summary>
/// 本地翻译工作区：从磁盘加载剧本与译文文件，纯文件 IO 没有 UI 依赖。
/// 平台模式下不会用到，但本地模式的导入 / 参考依然走它。
/// </summary>
public sealed class LocalTranslationWorkspaceService
{
    public Story LoadStory(string path)
    {
        return Story.FromFile(path);
    }

    public Story LoadStoryWithTranslation(string scriptPath, string translationPath)
    {
        var translationData = new TranslationData(translationPath);
        foreach (var translation in translationData.Translations)
        {
            translation.Body = translation.Body.Replace("\\N", "\n");
        }

        var gameScript = new GameScript(scriptPath);
        return new Story(gameScript, translationData);
    }

    public Story LoadReferenceStory(string scriptPath, string translationPath)
    {
        return LoadStoryWithTranslation(scriptPath, translationPath);
    }

    public async Task SaveTranslationAsync(
        string path, string content, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }
}
