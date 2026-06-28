using System.Text.Json;
using SekaiDataFetch;
using SekaiDataFetch.Item;
using SekaiDataFetch.List;
using SekaiDataFetch.Source;
using SekaiToolsBase;
using SekaiToolsBase.DataList;
using SekaiToolsEngine.Ipc;

namespace SekaiToolsEngine.Handlers;

public sealed class DownloadHandler
{
    private const string SourceListUrl = "https://config.g.xbb.moe/source.json";
    private readonly IpcTransport _transport;
    private SourceData[] _allSources;
    private SourceData _currentSource; // tracked locally because SourceList.SourceData is write-only

    private static readonly SourceData MoesekaiJp = new()
    {
        SourceName = "Moesekai JP",
        SourceTemplate = "https://sekaimaster.exmeaning.com/master/{type}.json",
        StorageBaseUrl = "https://storage.exmeaning.com/sekai-jp-assets/",
        ActionSetTemplate = "scenario/actionset/{abName}/{scenarioId}.json",
        MemberStoryTemplate = "character/member/{abName}/{scenarioId}.json",
        EventStoryTemplate = "event_story/{abName}/scenario/{scenarioId}.json",
        SpecialStoryTemplate = "scenario/special/{abName}/{scenarioId}.json",
        UnitStoryTemplate = "scenario/unitstory/{abName}/{scenarioId}.json",
    };

    private static readonly SourceData MoesekaiCn = new()
    {
        SourceName = "Moesekai CN",
        SourceTemplate = "https://sekaimaster-cn.exmeaning.com/master/{type}.json",
        StorageBaseUrl = "https://storage.exmeaning.com/sekai-cn-assets/",
        ActionSetTemplate = "scenario/actionset/{abName}/{scenarioId}.json",
        MemberStoryTemplate = "character/member/{abName}/{scenarioId}.json",
        EventStoryTemplate = "event_story/{abName}/scenario/{scenarioId}.json",
        SpecialStoryTemplate = "scenario/special/{abName}/{scenarioId}.json",
        UnitStoryTemplate = "scenario/unitstory/{abName}/{scenarioId}.json",
    };

    public DownloadHandler(IpcTransport transport)
    {
        _transport = transport;
        _allSources = BuildDefaultSources();
        _currentSource = _allSources[0];
    }

    private static SourceData[] BuildDefaultSources()
    {
        return new[] { MoesekaiJp }
            .Concat(SourceData.Default)
            .Append(MoesekaiCn)
            .ToArray();
    }

    public void Register(Dispatcher dispatcher)
    {
        dispatcher.Register("download.sources", GetSourcesAsync);
        dispatcher.Register("download.setSource", SetSourceAsync);
        dispatcher.Register("download.refresh", RefreshAsync);
        dispatcher.Register("download.candidates", CandidatesAsync);
        dispatcher.Register("download.filters", FiltersAsync);
        dispatcher.Register("download.fetchCandidates", FetchCandidatesAsync);
    }

    private async Task<object?> GetSourcesAsync(JsonElement? @params)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await http.GetStringAsync(SourceListUrl);
            var remote = System.Text.Json.JsonSerializer.Deserialize<SourceData[]>(json);
            if (remote is { Length: > 0 })
            {
                var moesekaiNames = new HashSet<string> { MoesekaiJp.SourceName, MoesekaiCn.SourceName };
                var others = remote.Where(s => !moesekaiNames.Contains(s.SourceName)).ToArray();
                _allSources = new[] { MoesekaiJp }
                    .Concat(others)
                    .Append(MoesekaiCn)
                    .ToArray();
            }
        }
        catch { /* use defaults */ }

        return _allSources.Select((s, i) => new
        {
            index = i,
            name = s.SourceName,
        }).ToArray();
    }

    private Task<object?> SetSourceAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var index = p.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
        // Validate instead of silently no-op'ing on a bad index (which previously still
        // returned "ok", leaving the caller running against the wrong/old source).
        if (index < 0 || index >= _allSources.Length)
            throw new ArgumentException($"Source index out of range: {index} (have {_allSources.Length})");

        var source = _allSources[index];
        _currentSource = source;
        Fetcher.Instance.SetSource(source);
        SourceList.Instance.SourceData = source;
        ListUnitStory.Instance.SetSource(source);
        ListEventStory.Instance.SetSource(source);
        ListSpecialStory.Instance.SetSource(source);
        ListCardStory.Instance.SetSource(source);
        ListActionStory.Instance.SetSource(source);
        ListGreetStory.Instance.SetSource(source);

        // Optional proxy {type:0=None|1=Http|2=Socks5, host, port}. Apply to the fetcher and every
        // list singleton so headless downloads honor the user's proxy (previously never applied).
        if (p.TryGetProperty("proxy", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            var ptype = pr.TryGetProperty("type", out var pt) ? pt.GetInt32() : 0;
            var phost = pr.TryGetProperty("host", out var ph) ? ph.GetString() ?? "" : "";
            var pport = pr.TryGetProperty("port", out var pp) ? pp.GetInt32() : 0;
            var proxy = new Proxy(phost, pport, ptype switch
            {
                1 => Proxy.Type.Http,
                2 => Proxy.Type.Socks5,
                _ => Proxy.Type.None,
            });
            Fetcher.Instance.SetProxy(proxy);
            ListUnitStory.Instance.SetProxy(proxy);
            ListEventStory.Instance.SetProxy(proxy);
            ListSpecialStory.Instance.SetProxy(proxy);
            ListCardStory.Instance.SetProxy(proxy);
            ListActionStory.Instance.SetProxy(proxy);
            ListGreetStory.Instance.SetProxy(proxy);
        }
        return Task.FromResult<object?>("ok");
    }

    private async Task<object?> RefreshAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var storyTypeIndex = p.GetProperty("storyTypeIndex").GetInt32();

        BaseListStory list = storyTypeIndex switch
        {
            0 => ListUnitStory.Instance,
            1 => ListEventStory.Instance,
            2 => ListSpecialStory.Instance,
            >= 3 and <= 6 => ListCardStory.Instance,
            >= 7 and <= 9 => ListActionStory.Instance,
            10 => ListGreetStory.Instance,
            _ => throw new ArgumentException($"Unknown storyTypeIndex: {storyTypeIndex}")
        };

        await list.Refresh();
        return "ok";
    }

    private Task<object?> CandidatesAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var storyTypeIndex = p.GetProperty("storyTypeIndex").GetInt32();
        var filter = p.TryGetProperty("filter", out var f) ? f.GetString() : null;

        var candidates = BuildCandidates(storyTypeIndex, filter);
        return Task.FromResult<object?>(candidates);
    }

    private Task<object?> FiltersAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var storyTypeIndex = p.GetProperty("storyTypeIndex").GetInt32();

        object? result = storyTypeIndex switch
        {
            0 => new
            {
                type = "unit",
                units = ListUnitStory.Instance.Data.Keys.ToArray(),
            },
            2 => new
            {
                type = "special",
                titles = ListSpecialStory.Instance.Data.Keys.ToArray(),
            },
            >= 7 and <= 9 => new
            {
                type = "area",
                areas = ListActionStory.Instance.Areas
                    .OrderBy(a => a.Id)
                    .Select(a => new { id = a.Id, name = a.AreaName })
                    .ToArray(),
            },
            _ => new { type = "none" },
        };

        return Task.FromResult<object?>(result);
    }

    private async Task<object?> FetchCandidatesAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var items = p.GetProperty("items");
        var saveDirBase = p.TryGetProperty("saveDir", out var sd)
            ? sd.GetString()!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SekaiTools/Downloads");

        var total = items.GetArrayLength();
        var done = 0;
        var failed = 0;

        foreach (var item in items.EnumerateArray())
        {
            var url = item.GetProperty("url").GetString() ?? "";
            var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;

            // A candidate with no resolvable URL is a failure, not silent success.
            if (string.IsNullOrWhiteSpace(url))
            {
                failed++;
                _transport.SendNotification("download.error", new { title = title ?? "", error = "下载地址为空" });
                continue;
            }

            var fileName = Path.GetFileName(url);
            var savePath = Path.Combine(saveDirBase, fileName);
            Directory.CreateDirectory(saveDirBase);

            try
            {
                var content = await Fetcher.Instance.Fetch(url);
                // Fetcher.Fetch returns the "{}" sentinel (NOT an exception) after exhausting retries
                // on HTTP/network failure; writing it leaves a 2-byte file that downstream parses as
                // 0 entries (masquerading as a recognition failure). Treat the sentinel / empty body
                // as a real failure instead of reporting success.
                if (string.IsNullOrWhiteSpace(content) || content.Trim() == "{}")
                {
                    failed++;
                    _transport.SendNotification("download.error", new { title = title ?? fileName, error = "下载失败（空响应 / HTTP 错误）" });
                    continue;
                }
                await File.WriteAllTextAsync(savePath, content);
                done++;
                _transport.SendNotification("download.progress", new
                {
                    done, total, failed,
                    title = title ?? fileName,
                    path = savePath,
                });
            }
            catch (Exception ex)
            {
                failed++;
                _transport.SendNotification("download.error", new
                {
                    title = title ?? fileName,
                    error = ex.Message,
                });
            }
        }

        _transport.SendNotification("download.finished", new { done, total, failed });
        return new { done, total, failed };
    }

    private object[] BuildCandidates(int storyTypeIndex, string? filter)
    {
        return storyTypeIndex switch
        {
            0 => BuildUnitCandidates(filter),
            1 => BuildEventCandidates(),
            2 => BuildSpecialCandidates(filter),
            >= 3 and <= 6 => BuildCardCandidates(storyTypeIndex, filter),
            >= 7 and <= 9 => BuildActionCandidates(storyTypeIndex),
            10 => BuildGreetCandidates(filter),
            _ => [],
        };
    }

    private object[] BuildUnitCandidates(string? unitKey)
    {
        var data = ListUnitStory.Instance.Data;
        var key = unitKey ?? data.Keys.FirstOrDefault() ?? "";
        if (!data.TryGetValue(key, out var unitSet)) return [];

        var results = new List<object>();
        foreach (var chapter in unitSet.Chapters)
        {
            var ab = chapter.AssetBundleName;
            foreach (var ep in chapter.Episodes)
            {
                results.Add(new
                {
                    title = chapter.Name + " - " + ep.Key,
                    url = SourceList.Instance.UnitStory(ep.ScenarioId, ab),
                });
            }
        }
        return results.ToArray();
    }

    private object[] BuildEventCandidates()
    {
        var sets = ListEventStory.Instance.Data;
        if (sets.Count == 0) return [];

        var results = new List<object>();
        foreach (var set in sets.OrderByDescending(s => s.EventStory.EventId))
        {
            var ab = set.EventStory.AssetBundleName;
            var eventName = set.GameEvent.Name;
            var eventId = set.EventStory.EventId;
            foreach (var ep in set.EventStory.EventStoryEpisodes)
            {
                results.Add(new
                {
                    title = $"No.{eventId} {eventName} - {ep.EpisodeNo} {ep.Title}",
                    url = SourceList.Instance.EventStory(ep.ScenarioId, ab),
                });
            }
        }
        return results.ToArray();
    }

    private object[] BuildSpecialCandidates(string? selectedTitle)
    {
        var data = ListSpecialStory.Instance.Data;
        if (data.Count == 0) return [];
        var key = selectedTitle ?? data.Keys.FirstOrDefault() ?? "";
        if (!data.TryGetValue(key, out var set)) return [];

        return set.Episodes.Select(ep => (object)new
        {
            title = ep.Title,
            url = SourceList.Instance.SpecialStory(ep),
        }).ToArray();
    }

    private object[] BuildCardCandidates(int storyTypeIndex, string? characterIdStr)
    {
        var data = ListCardStory.Instance.Data;
        if (data.Count == 0) return [];
        var charId = int.TryParse(characterIdStr, out var cid) ? cid : 1;

        string[] rarityTypes = storyTypeIndex switch
        {
            3 => ["rarity_4"],
            4 => ["rarity_birthday"],
            5 => ["rarity_1", "rarity_2"],
            6 => ["rarity_3"],
            _ => [],
        };

        var matched = data
            .Where(d => d.Card.CharacterId == charId && rarityTypes.Contains(d.Card.CardRarityType))
            .OrderByDescending(d => d.Card.Id)
            .ToList();

        var results = new List<object>();
        foreach (var s in matched)
        {
            var prefix = s.Card.Prefix;
            results.Add(new
            {
                title = $"No.{s.Card.Id} {prefix} 前篇",
                url = SourceList.Instance.MemberStory(s.FirstPart),
            });
            results.Add(new
            {
                title = $"No.{s.Card.Id} {prefix} 後篇",
                url = SourceList.Instance.MemberStory(s.SecondPart),
            });
        }
        return results.ToArray();
    }

    private object[] BuildActionCandidates(int storyTypeIndex)
    {
        var data = ListActionStory.Instance.Data;
        if (data.Count == 0) return [];

        var results = new List<object>();
        foreach (var set in data.OrderByDescending(d => d.ActionSet.Id))
        {
            results.Add(new
            {
                title = $"对话 {set.ActionSet.Id} ({set.ActionSet.ScenarioId})",
                url = SourceList.Instance.ActionSet(set),
            });
        }
        return results.ToArray();
    }

    private object[] BuildGreetCandidates(string? characterIdStr)
    {
        var data = ListGreetStory.Instance.Data;
        if (data.Count == 0) return [];
        var charId = int.TryParse(characterIdStr, out var cid) ? cid : 1;

        var baseUrl = _currentSource.StorageBaseUrl ?? "";
        return data
            .Where(d => d.CharacterId == charId)
            .OrderByDescending(d => d.PublishedAt)
            .Select(item =>
            {
                var serif = item.Serif.Replace("\n", " ");
                if (serif.Length > 40) serif = serif[..40] + "...";
                // Port the GUI's BuildGreetVoiceUrl so greet voices have a real URL (was "" ->
                // every greet item silently "failed"). Empty base falls through to FetchCandidates'
                // empty-url guard, which now reports a failure instead of faking success.
                var url = string.IsNullOrEmpty(baseUrl)
                    ? ""
                    : baseUrl.Contains("sekai.best", StringComparison.OrdinalIgnoreCase)
                        ? baseUrl + $"sound/systemvoice/{item.AssetbundleName}/{item.Voice}.mp3"
                        : baseUrl + $"startapp/sound/systemvoice/{item.AssetbundleName}/{item.Voice}.mp3";
                return (object)new
                {
                    title = $"[{item.Voice}] {serif}",
                    url,
                };
            }).ToArray();
    }
}

