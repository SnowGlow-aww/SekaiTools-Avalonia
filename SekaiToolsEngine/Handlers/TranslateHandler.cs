using System.Text.Json;
using SekaiToolsBase.Story;
using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsEngine.Ipc;

namespace SekaiToolsEngine.Handlers;

public sealed class TranslateHandler
{
    private readonly IpcTransport _transport;

    public TranslateHandler(IpcTransport transport)
    {
        _transport = transport;
    }

    public void Register(Dispatcher dispatcher)
    {
        dispatcher.Register("translate.submit", SubmitAsync);
    }

    private Task<object?> SubmitAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var filePath = p.GetProperty("filePath").GetString()!;

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var story = Story.FromFile(filePath);

        var lines = story.Events.Select(e => new
        {
            index = e.Index,
            type = e is DialogStoryEvent ? "dialog" : "effect",
            character = e is DialogStoryEvent d ? d.FinalCharacter : null,
            body = e.BodyOriginal,
            bodyTranslated = e.BodyTranslated,
        }).ToArray();

        return Task.FromResult<object?>(new { count = lines.Length, lines });
    }
}
