namespace SekaiToolsCore.Process.Config;

public class Config
{
    public Config(
        string videoFilePath,
        string scriptFilePath,
        string translateFilePath,
        StyleFontConfig? styleFontConfig = null,
        ExportStyleConfig? exportStyleConfig = null,
        TypewriterSetting? typerSetting = null,
        MatchingThreshold? matchingThreshold = null
    )
    {
        if (!Path.Exists(videoFilePath))
            throw new FileNotFoundException("Video file not found.", videoFilePath);
        if (!Path.Exists(scriptFilePath))
            throw new FileNotFoundException("Script file not found.", scriptFilePath);
        if (translateFilePath != "" && !Path.Exists(translateFilePath))
            throw new FileNotFoundException("Translation file not found.", translateFilePath);

        VideoFilePath = videoFilePath;
        ScriptFilePath = scriptFilePath;
        TranslateFilePath = translateFilePath;

        // Construct real defaults when a caller omits these. `default(struct)` zero-inits
        // and SKIPS the `= true`/value init defaults, so a `= default` param silently gave an
        // all-FALSE ExportStyleConfig (every subtitle line filtered out by Make() -> empty .ass),
        // empty font families, and zero typewriter timings. The IPC engine (SubtitleHandler)
        // omits all three — that is exactly what produced the styled-but-event-less .ass.
        StyleFontConfig = styleFontConfig ?? new StyleFontConfig();
        ExportStyleConfig = exportStyleConfig ?? new ExportStyleConfig();

        TyperSetting = typerSetting ?? new TypewriterSetting();
        MatchingThreshold = matchingThreshold ?? new MatchingThreshold();
    }

    public string VideoFilePath { get; }
    public string ScriptFilePath { get; }
    public string TranslateFilePath { get; }

    public TypewriterSetting TyperSetting { get; }

    public MatchingThreshold MatchingThreshold { get; }

    public StyleFontConfig StyleFontConfig { get; }

    public ExportStyleConfig ExportStyleConfig { get; }
}