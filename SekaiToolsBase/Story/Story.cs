using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsBase.Story.Translation;

namespace SekaiToolsBase.Story;

public class Story
{
    [Flags]
    public enum StoryEventType
    {
        Dialog = 0b001,
        Banner = 0b010,
        Marker = 0b100
    }

    public readonly BaseStoryEvent[] Events;

    public Story()
    {
        Events = [];
    }

    public Story(GameScript.GameScript gameScript, TranslationData translationData)
    {
        List<BaseStoryEvent> events = [];
        if (!gameScript.Empty())
        {
            int dialogCount = 0, effectCount = 0;
            int bannerCount = 0, markerCount = 0;
            foreach (var snippet in gameScript.Snippets)
                switch (snippet.Action)
                {
                    case 1:
                    {
                        var talkData = gameScript.TalkData[dialogCount];

                        if (dialogCount < gameScript.TalkData.Length)
                        {
                            var storyDialogEvent = new DialogStoryEvent(
                                dialogCount,
                                talkData.Body, talkData.GetCharacterId(),
                                talkData.WindowDisplayName,
                                talkData.WhenFinishCloseWindow == 1,
                                talkData.Shake
                            );
                            events.Add(storyDialogEvent);
                        }

                        dialogCount += 1;
                        break;
                    }
                    case 6:
                    {
                        var seData = gameScript.SpecialEffectData[effectCount];
                        switch (seData.EffectType)
                        {
                            case 8:
                                events.Add(new BannerStoryEvent(seData.StringVal, bannerCount, events.Count));
                                bannerCount++;
                                break;
                            case 18:
                                events.Add(new MarkerStoryEvent(seData.StringVal, markerCount));
                                markerCount++;
                                break;
                        }

                        effectCount += 1;
                        break;
                    }
                }
        }

        Events = events.ToArray();
        if (translationData.IsEmpty()) return;

        // 翻译文件不是 scenario 的强类型镜像：不同团队会使用不同角色命名，
        // 也可能省略姓名/横幅行或带有额外行。按两边实际存在的行尽力套用；
        // 类型不一致时只应用正文，绝不因命名或格式差异阻断打轴。
        var translationCount = Math.Min(Events.Length, translationData.Translations.Count);
        for (var i = 0; i < translationCount; i++)
        {
            var translation = translationData.Translations[i];
            if (Events[i] is DialogStoryEvent dialog)
            {
                if (translation is DialogTranslate dialogTranslation)
                    dialog.SetTranslation(dialogTranslation.Chara, dialogTranslation.Body);
                else
                    dialog.SetTranslationContent(translation.Body);
            }
            else
            {
                Events[i].BodyTranslated = translation.Body;
            }
        }
    }

    public static Story FromFile(string gameStoryDataPath, string translationDataPath = "")
    {
        if (!File.Exists(gameStoryDataPath)) throw new Exception("File not found");
        var jsonData = new GameScript.GameScript(gameStoryDataPath);
        var textData = File.Exists(translationDataPath)
            ? new TranslationData(translationDataPath)
            : new TranslationData(null);
        return new Story(jsonData, textData);
    }

    private int IndexInType(StoryEventType types, int index)
    {
        var i = 0;
        foreach (var e in Events)
        {
            if (types.HasFlag(StoryEventType.Dialog) && e.Type == "Dialog")
            {
                if (i == index) return i;
            }
            else if (types.HasFlag(StoryEventType.Banner) && e.Type == "Banner")
            {
                if (i == index) return i;
            }
            else if (types.HasFlag(StoryEventType.Marker) && e.Type == "Marker")
            {
                if (i == index) return i;
            }

            i += 1;
        }

        return -1;
    }

    public BaseStoryEvent[] GetTypes(StoryEventType types)
    {
        var result = new List<BaseStoryEvent>();
        foreach (var @event in Events)
            if (types.HasFlag(StoryEventType.Dialog) && @event.Type == "Dialog") result.Add(@event);
            else if (types.HasFlag(StoryEventType.Banner) && @event.Type == "Banner") result.Add(@event);
            else if (types.HasFlag(StoryEventType.Marker) && @event.Type == "Marker") result.Add(@event);

        return result.ToArray();
    }

    public DialogStoryEvent[] Dialogs()
    {
        var result = new List<DialogStoryEvent>();
        foreach (var v in Events)
            if (v is DialogStoryEvent @event)
                result.Add(@event);

        return result.ToArray();
    }

    public BannerStoryEvent[] Banners()
    {
        var result = new List<BannerStoryEvent>();
        foreach (var v in Events)
            if (v is BannerStoryEvent @event)
                result.Add(@event);

        return result.ToArray();
    }

    public MarkerStoryEvent[] Markers()
    {
        var result = new List<MarkerStoryEvent>();
        foreach (var v in Events)
            if (v is MarkerStoryEvent @event)
                result.Add(@event);

        return result.ToArray();
    }

    public BaseStoryEvent[] Effects()
    {
        var result = new List<BaseStoryEvent>();
        foreach (var v in Events)
            switch (v)
            {
                case BannerStoryEvent banner:
                    result.Add(banner);
                    break;
                case MarkerStoryEvent marker:
                    result.Add(marker);
                    break;
            }

        return result.ToArray();
    }

    public static Story Empty()
    {
        return new Story();
    }
}