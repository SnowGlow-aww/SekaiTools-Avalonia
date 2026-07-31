using System.Net;
using System.Security.Cryptography;
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

    private async Task<string> DownloadStringAsync(string url)
    {
        using var client = new HttpClient(GetHttpHandler());
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public string ResourcePath(ResourceType type, string fileName)
    {
        if (!ResourceTypePathMap.TryGetValue(type, out var typeDir))
            throw new ArgumentException($"ResourceType {type} not mapped");

        var manifestPath = $"{typeDir}/{fileName}";

        // 用户缓存优先（可能是联网下载的更新版），但已加载清单时不能让损坏缓存遮住有效内置副本。
        var cached = ResolveResourcePath(type, manifestPath, BasePath);
        Resource? metadata = null;
        lock (ResourceFileList)
        {
            if (ResourceFileList.TryGetValue(type, out var resources))
            {
                var match = resources.FirstOrDefault(resource =>
                    string.Equals(resource.Path.Replace('\\', '/'), manifestPath, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Path)) metadata = match;
            }
        }
        if (File.Exists(cached) && (metadata is null || ResourceFileValid(cached, metadata.Value))) return cached;

        var bundled = ResolveResourcePath(type, manifestPath, BundledBasePath);
        if (File.Exists(bundled) && (metadata is null || ResourceFileValid(bundled, metadata.Value))) return bundled;
        throw new FileNotFoundException($"{fileName} not found in cache ({cached}) or bundle ({bundled})");
    }

    public async Task<bool> CheckResource(ResourceType type)
    {
        var fileList = await GetFileList(type);
        if (fileList.Length == 0) return false;
        return fileList.All(file => CheckResourceFile(type, file));
    }

    /// <summary>
    /// 只使用随程序内置的清单校验本地缓存/内置资源，不进行任何网络请求。
    /// VideoProcess 资源随 SekaiCoreEngine 发布，启动打轴时必须走此路径；缺失说明
    /// 引擎包不完整或文件损坏，应重新安装，而不是要求用户联网补下载。
    /// </summary>
    public bool CheckLocalResource(ResourceType type)
    {
        if (!ResourceTypePathMap.TryGetValue(type, out var typeDir))
            throw new ArgumentException($"ResourceType {type} not mapped");

        var fileList = LoadBundledFileList(typeDir);
        if (fileList.Length == 0) return false;
        try
        {
            fileList = ValidateResourceList(type, fileList);
        }
        catch
        {
            return false;
        }

        lock (ResourceFileList)
            ResourceFileList[type] = fileList;
        return fileList.All(file => CheckResourceFile(type, file));
    }

    private static bool CheckResourceFile(ResourceType type, Resource file)
    {
        // 用户缓存或随包内置任一处存在且 size+md5 匹配即视为就绪。
        return ResourceFileValid(ResolveResourcePath(type, file.Path, BasePath), file)
               || ResourceFileValid(ResolveResourcePath(type, file.Path, BundledBasePath), file);
    }

    private static bool ResourceFileValid(string filename, Resource file)
    {
        filename = NormalizePath(filename);
        if (!File.Exists(filename)) return false;
        return file.Size == new FileInfo(filename).Length &&
               string.Equals(file.Md5, CalculateMd5(filename), StringComparison.OrdinalIgnoreCase);
    }

    private static string CalculateMd5(string filename)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filename);
        return Convert.ToHexString(md5.ComputeHash(stream));
    }

    public async Task EnsureResource(ResourceType type)
    {
        if (!ResourceTypePathMap.ContainsKey(type))
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

        var filename = ResolveResourcePath(type, resource.Path, BasePath);
        var fileDir = Path.GetDirectoryName(filename)
                      ?? throw new InvalidDataException($"Resource path has no parent directory: {resource.Path}");
        Directory.CreateDirectory(fileDir);

        var fileUrl = BuildResourceUrl(resource.Path);
        Console.WriteLine($"Downloading {fileUrl}");

        using var client = new HttpClient(GetHttpHandler());
        using var response = await client.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != resource.Size)
            throw new InvalidDataException(
                $"Resource size mismatch for {resource.Path}: expected {resource.Size}, server reported {contentLength}.");

        await using var source = await response.Content.ReadAsStreamAsync();
        await InstallVerifiedResourceAsync(
            source,
            filename,
            resource,
            containmentRoot: Path.Combine(BasePath, ResourceTypePathMap[type]));
        Console.WriteLine($"Download completed: {filename}");
    }

    internal static async Task InstallVerifiedResourceAsync(
        Stream source,
        string destination,
        Resource resource,
        CancellationToken cancellationToken = default,
        string? containmentRoot = null)
    {
        ValidateSizeAndHash(resource);

        var normalizedDestination = NormalizePath(destination);
        var directory = Path.GetDirectoryName(normalizedDestination)
                        ?? throw new InvalidDataException($"Resource destination has no parent directory: {destination}");
        if (!string.IsNullOrWhiteSpace(containmentRoot))
            EnsureNoReparsePoints(containmentRoot, normalizedDestination);
        Directory.CreateDirectory(directory);
        if (!string.IsNullOrWhiteSpace(containmentRoot))
            EnsureNoReparsePoints(containmentRoot, normalizedDestination);

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(normalizedDestination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long written = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0) break;
                    if (written > resource.Size - read)
                        throw new InvalidDataException(
                            $"Resource size mismatch for {resource.Path}: expected {resource.Size}, download is larger.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                }

                if (written != resource.Size)
                    throw new InvalidDataException(
                        $"Resource size mismatch for {resource.Path}: expected {resource.Size}, downloaded {written}.");
            }

            if (!ResourceFileValid(temporary, resource))
                throw new InvalidDataException($"Resource hash verification failed for {resource.Path}.");

            // Re-check immediately before commit: a cache subdirectory must not
            // have been replaced with a symlink/junction while bytes were downloading.
            if (!string.IsNullOrWhiteSpace(containmentRoot))
                EnsureNoReparsePoints(containmentRoot, normalizedDestination);

            // 临时文件与目标位于同一目录；Move(overwrite) 在同卷上以一次替换提交，失败时保留旧缓存。
            File.Move(temporary, normalizedDestination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static Resource[] ValidateResourceList(ResourceType type, IEnumerable<Resource> resources)
    {
        var validated = resources.ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in validated)
        {
            ValidateSizeAndHash(resource);
            var canonical = ResolveResourcePath(type, resource.Path, BasePath);
            if (!seen.Add(canonical))
                throw new InvalidDataException($"Resource manifest contains duplicate path: {resource.Path}");
        }

        return validated;
    }

    internal static string ResolveResourcePath(ResourceType type, string manifestPath, string root)
    {
        if (!ResourceTypePathMap.TryGetValue(type, out var typeDir))
            throw new ArgumentException($"ResourceType {type} not mapped");
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidDataException("Resource path is empty.");

        var portablePath = manifestPath.Trim().Replace('\\', '/');
        if (portablePath.StartsWith('/') || Path.IsPathRooted(portablePath))
            throw new InvalidDataException($"Resource path must be relative: {manifestPath}");

        var segments = portablePath.Split('/', StringSplitOptions.None);
        if (segments.Length < 2 || !string.Equals(segments[0], typeDir, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Resource path must be inside the {typeDir} directory: {manifestPath}");
        if (segments.Any(segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Contains(':') || segment.Any(char.IsControl)))
            throw new InvalidDataException($"Resource path contains an invalid segment: {manifestPath}");

        var normalizedRoot = NormalizePath(root);
        var typeRoot = NormalizePath(Path.Combine(normalizedRoot, typeDir));
        var candidate = NormalizePath(Path.Combine(normalizedRoot, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = typeRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, comparison))
            throw new InvalidDataException($"Resource path escapes the {typeDir} directory: {manifestPath}");

        EnsureNoReparsePoints(typeRoot, candidate);
        return candidate;
    }

    internal static void EnsureNoReparsePoints(string containmentRoot, string candidate)
    {
        var root = NormalizePath(containmentRoot);
        var target = NormalizePath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (target != root && !target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException($"Resource path escapes its containment root: {candidate}");

        // Lexical containment is insufficient when an existing child directory
        // is a Unix symlink or Windows junction. Inspect each existing component
        // below (and including) the trusted type root; missing tail components are
        // safe to create only after their nearest existing parent has passed.
        var current = root;
        RejectReparsePoint(current);
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".") return;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Resource path traverses a symlink or junction: {path}");
        }
        catch (FileNotFoundException)
        {
            // The remaining path will be created beneath the verified parent.
        }
        catch (DirectoryNotFoundException)
        {
            // The remaining path will be created beneath the verified parent.
        }
    }

    private static void ValidateSizeAndHash(Resource resource)
    {
        if (resource.Size < 0)
            throw new InvalidDataException($"Resource size cannot be negative: {resource.Path}");
        if (string.IsNullOrWhiteSpace(resource.Md5) ||
            resource.Md5.Length != 32 ||
            resource.Md5.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Resource MD5 must contain exactly 32 hexadecimal characters: {resource.Path}");
    }

    private static string BuildResourceUrl(string manifestPath)
    {
        var escapedPath = string.Join('/', manifestPath.Trim().Replace('\\', '/').Split('/')
            .Select(Uri.EscapeDataString));
        return ResourceServerUrl + escapedPath;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private async Task<Resource[]> GetFileList(ResourceType type)
    {
        lock (ResourceFileList)
        {
            if (ResourceFileList.TryGetValue(type, out var cachedResources)) return cachedResources;
        }

        if (!ResourceTypePathMap.TryGetValue(type, out var typeDir))
            throw new ArgumentException($"ResourceType {type} not mapped");

        // 优先读随包内置清单（离线首选，健康首跑不再联系 resource.g.xbb.moe）；
        // 仅当内置清单缺失/损坏时才回退联网拉取。
        var fileList = LoadBundledFileList(typeDir);
        if (fileList.Length == 0)
        {
            var fileListUrl = ResourceServerUrl + $"{typeDir}.json";
            Console.WriteLine($"Downloading {fileListUrl}");
            var fileListJson = await DownloadStringAsync(fileListUrl);
            fileList = JsonSerializer.Deserialize<Resource[]>(fileListJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }

        fileList = ValidateResourceList(type, fileList);
        lock (ResourceFileList)
        {
            if (fileList.Length > 0)
                ResourceFileList[type] = fileList;
            else
                ResourceFileList.Remove(type);
        }

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
