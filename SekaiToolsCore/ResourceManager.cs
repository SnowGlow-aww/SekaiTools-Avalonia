using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SekaiToolsBase;

namespace SekaiToolsCore;

public enum ResourceType
{
    VapourSynth,
    VideoProcess
}

public struct Resource
{
    // {
    //     "path": "vapourSynth/7z.dll",
    //     "size": 1892864,
    //     "md5": "1143c4905bba16d8cc02c6ba8f37f365"
    // }

    public string Path { get; set; }
    public string Md5 { get; set; }

    public long Size { get; set; }
}

public class ResourceManager
{
    private const string ResourceServerUrl = "https://resource.g.xbb.moe/";

    public static readonly string DataBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SekaiTools");

    private static readonly string BasePath = Path.Combine(DataBaseDir, "Resource");

    // 随引擎发布的内置资源根：csproj 把 videoProcess/** 与 videoProcess.json 作为 Content
    // 复制到可执行文件同级目录。取用顺序为 用户缓存(BasePath) → 内置 → 联网下载，
    // 让干净机器首跑打轴零联网、不再依赖 resource.g.xbb.moe（裸连不通、需代理）。
    private static readonly string BundledBasePath = AppContext.BaseDirectory;

    private static readonly Dictionary<ResourceType, string> ResourceTypePathMap = new()
    {
        { ResourceType.VapourSynth, "vapourSynth" },
        { ResourceType.VideoProcess, "videoProcess" }
    };

    private static readonly Dictionary<ResourceType, Resource[]> ResourceFileList = new();

    public static ResourceManager Instance { get; } = new();

    private Proxy UserProxy { get; set; } = Proxy.None;

    public void SetProxy(Proxy proxy)
    {
        UserProxy = proxy;
    }

    private HttpMessageHandler GetHttpHandler()
    {
        return UserProxy.ProxyType switch
        {
            Proxy.Type.None => new HttpClientHandler(),
            Proxy.Type.System => new HttpClientHandler(),
            Proxy.Type.Http => new HttpClientHandler
            {
                Proxy = new WebProxy(UserProxy.Host, UserProxy.Port), UseProxy = true
            },
            Proxy.Type.Socks5 => new SocketsHttpHandler
            {
                Proxy = new WebProxy(UserProxy.Host, UserProxy.Port), UseProxy = true
            },
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private async Task<HttpResponseMessage> Download(string url)
    {
        using var client = new HttpClient(GetHttpHandler());
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return response;
    }

    public string ResourcePath(ResourceType type, string fileName)
    {
        if (!ResourceTypePathMap.TryGetValue(type, out var typeDir))
            throw new ArgumentException($"ResourceType {type} not mapped");

        // 用户缓存优先（可能是联网下载的更新版），缺失则回退随包内置副本。
        var cached = Path.Combine(BasePath, typeDir, fileName);
        if (File.Exists(cached)) return cached;
        var bundled = Path.Combine(BundledBasePath, typeDir, fileName);
        if (File.Exists(bundled)) return bundled;
        throw new FileNotFoundException($"{fileName} not found in cache ({cached}) or bundle ({bundled})");
    }

    public async Task<bool> CheckResource(ResourceType type)
    {
        var fileList = await GetFileList(type);
        if (fileList.Length == 0) return false;
        return fileList.All(file => CheckResourceFile(type, file));
    }

    private static bool CheckResourceFile(ResourceType type, Resource file)
    {
        // 用户缓存或随包内置任一处存在且 size+md5 匹配即视为就绪。
        return ResourceFileValid(Path.Combine(BasePath, file.Path), file)
               || ResourceFileValid(Path.Combine(BundledBasePath, file.Path), file);
    }

    private static bool ResourceFileValid(string filename, Resource file)
    {
        filename = NormalizePath(filename);
        if (!File.Exists(filename)) return false;
        return file.Size == new FileInfo(filename).Length &&
               string.Equals(file.Md5, CalculateMd5(filename), StringComparison.CurrentCultureIgnoreCase);
    }

    private static string CalculateMd5(string filename)
    {
        using var md5 = MD5.Create();
        // 使用 FileStream 打开文件，并传入到 ComputeHash 方法中
        using var stream = File.OpenRead(filename);
        // 计算哈希值
        var hashBytes = md5.ComputeHash(stream);

        // 将字节数组转换为十六进制字符串
        var sb = new StringBuilder();
        foreach (var t in hashBytes) sb.Append(t.ToString("X2"));

        return sb.ToString();
    }

    public async Task EnsureResource(ResourceType type)
    {
        if (!ResourceTypePathMap.TryGetValue(type, out var typeDir))
            throw new ArgumentException($"ResourceType {type} not mapped");

        var fileList = await GetFileList(type);
        if (fileList.Length == 0)
            throw new InvalidOperationException($"Resource list for {type} is empty.");

        var tasks = fileList.Select<Resource, Task>(file => EnsureResourceFile(type, file)).ToArray();
        foreach (var task in tasks) await task;

        // delete files do not exist in the resource list
        // foreach (var file in Directory.GetFiles(Path.Combine(BasePath, typeDir)))
        // {
        //     if (fileList.Any(f =>
        //             NormalizePath(Path.Combine(BasePath, f.Path)) ==
        //             NormalizePath(Path.GetFileName(file)))) continue;
        //     File.Delete(file);
        // }
    }

    private async Task EnsureResourceFile(ResourceType type, Resource resource)
    {
        // 用户缓存或随包内置已就绪则无需联网——这是干净机器首跑的常态路径。
        if (CheckResourceFile(type, resource)) return;

        var filename = NormalizePath(Path.Combine(BasePath, resource.Path));
        var fileDir = Path.GetDirectoryName(filename);
        if (fileDir != null && !Directory.Exists(fileDir)) Directory.CreateDirectory(fileDir);

        if (File.Exists(filename)) File.Delete(filename);
        var fileUrl = ResourceServerUrl + resource.Path;

        Console.WriteLine($"Downloading {fileUrl}");
        var response = await Download(fileUrl);
        var fileBytes = await response.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(filename, fileBytes);
        Console.WriteLine($"Download completed: {filename}");
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private async Task<Resource[]> GetFileList(ResourceType type)
    {
        if (ResourceFileList.TryGetValue(type, out var resources)) return resources;

        if (!ResourceTypePathMap.TryGetValue(type, out var typeDir))
            throw new ArgumentException($"ResourceType {type} not mapped");

        // 优先读随包内置清单（离线首选，健康首跑不再联系 resource.g.xbb.moe）；
        // 仅当内置清单缺失/损坏时才回退联网拉取。
        var fileList = LoadBundledFileList(typeDir);
        if (fileList.Length == 0)
        {
            var fileListUrl = ResourceServerUrl + $"{typeDir}.json";
            Console.WriteLine($"Downloading {fileListUrl}");
            var response = await Download(fileListUrl);
            var fileListJson = await response.Content.ReadAsStringAsync();
            fileList = JsonSerializer.Deserialize<Resource[]>(fileListJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }

        if (fileList.Length > 0)
            ResourceFileList[type] = fileList;
        else
            ResourceFileList.Remove(type);
        return fileList;
    }

    // LoadBundledFileList 读取随引擎发布的 {typeDir}.json 清单；不存在或解析失败返回空数组。
    private static Resource[] LoadBundledFileList(string typeDir)
    {
        try
        {
            var path = Path.Combine(BundledBasePath, $"{typeDir}.json");
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<Resource[]>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch
        {
            return [];
        }
    }
}