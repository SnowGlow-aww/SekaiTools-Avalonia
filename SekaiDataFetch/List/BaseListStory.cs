using System.Reflection;
using SekaiDataFetch.Source;
using SekaiToolsBase;

namespace SekaiDataFetch.List;

[AttributeUsage(AttributeTargets.Property)]
public class CachePathAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

[AttributeUsage(AttributeTargets.Property)]
public class SourcePathAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

public abstract class BaseListStory
{
    protected static readonly Fetcher Fetcher = Fetcher.Instance;

    public static readonly string DataBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SekaiTools");

    private string[] CachePaths
    {
        get
        {
            var fields = GetType().GetFields(BindingFlags.NonPublic |
                                             BindingFlags.Static |
                                             BindingFlags.Instance);
            return fields
                .Where(f => f.GetCustomAttributes(typeof(CachePathAttribute), false).Length != 0)
                .Select(f => f.GetValue(this) as string)
                .Where(s => s != null)
                .ToArray()!;
        }
    }

    public void SetSource(SourceData sourceData)
    {
        Fetcher.SetSource(sourceData);
    }

    public void SetProxy(Proxy proxy)
    {
        Fetcher.SetProxy(proxy);
    }

    public void ClearCache()
    {
        foreach (var path in CachePaths)
            if (File.Exists(path))
                File.Delete(path);

        Logger.Log($"{GetType().Name} cache cleared");
    }

    protected abstract void Load();

    public async Task Refresh()
    {
        var type = GetType();

        var sourceProps = type.GetProperties(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(p => new
            {
                Prop = p,
                Attr = p.GetCustomAttributes(typeof(SourcePathAttribute), false).FirstOrDefault() as SourcePathAttribute
            })
            .Where(x => x.Attr is { Key.Length: > 0 })
            .ToDictionary(x => x.Attr?.Key!, x => x.Prop.GetValue(null) as string);

        var cacheFields = type.GetProperties(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(p => new
            {
                Prop = p,
                Attr = p.GetCustomAttributes(typeof(CachePathAttribute), false).FirstOrDefault() as CachePathAttribute
            })
            .Where(x => x.Attr is { Key.Length: > 0 })
            .ToDictionary(x => x.Attr?.Key!, x => x.Prop.GetValue(null) as string);

        var tasks = sourceProps.Keys.Intersect(cacheFields.Keys)
            .Select(async key =>
            {
                var sourceValue = sourceProps[key];
                var cachePath = cacheFields[key];
                if (sourceValue != null && cachePath != null)
                {
                    var content = await Fetcher.Fetch(sourceValue);
                    // Fetcher.Fetch returns the "{}" sentinel (not an exception) after exhausting
                    // retries on HTTP/network failure. Caching it would write a 2-byte file that
                    // Load() parses as 0 entries while Refresh() still reports success. Fail loudly
                    // so the caller (engine download.refresh) learns the refresh actually failed.
                    if (string.IsNullOrWhiteSpace(content) || content.Trim() == "{}")
                        throw new InvalidOperationException(
                            $"刷新失败：源 {key} 返回空数据（网络/源不可用，Fetcher 返回了 \"{{}}\" 兜底值）");
                    await File.WriteAllTextAsync(cachePath, content);
                }
            }).ToArray();

        await Task.WhenAll(tasks);

        Logger.Log($"{type.Name} data refreshed from sources: {string.Join(", ", sourceProps.Keys)}");

        Load();
    }
}