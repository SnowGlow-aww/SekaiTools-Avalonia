using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SekaiToolsCore;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsEngine.Ipc;

namespace SekaiToolsEngine.Handlers;

public sealed class SubtitleHandler
{
    private readonly IpcTransport _transport;
    private VideoProcessor? _processor;
    private readonly object _lock = new();
    private readonly List<DialogBaseFrameSet> _dialogs = [];
    private readonly List<BannerBaseFrameSet> _banners = [];
    private readonly List<MarkerBaseFrameSet> _markers = [];

    public SubtitleHandler(IpcTransport transport)
    {
        _transport = transport;
    }

    public void Register(Dispatcher dispatcher)
    {
        dispatcher.Register("subtitle.start", StartAsync);
        dispatcher.Register("subtitle.stop", StopAsync);
        dispatcher.Register("subtitle.export", ExportAsync);
    }

    private async Task<object?> StartAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;

        var videoPath = p.GetProperty("videoPath").GetString()!;
        var scriptPath = p.GetProperty("scriptPath").GetString()!;
        var translatePath = p.TryGetProperty("translatePath", out var tp) ? tp.GetString() ?? "" : "";

        var threshold = new MatchingThreshold();
        if (p.TryGetProperty("threshold", out var th))
        {
            threshold = new MatchingThreshold
            {
                DialogNametagNormal = th.TryGetProperty("dialogNametagNormal", out var v1) ? v1.GetDouble() : 0.70,
                DialogNametagSpecial = th.TryGetProperty("dialogNametagSpecial", out var v2) ? v2.GetDouble() : 0.70,
                DialogContentNormal = th.TryGetProperty("dialogContentNormal", out var v3) ? v3.GetDouble() : 0.70,
                DialogContentSpecial = th.TryGetProperty("dialogContentSpecial", out var v4) ? v4.GetDouble() : 0.70,
                BannerNormal = th.TryGetProperty("bannerNormal", out var v5) ? v5.GetDouble() : 0.50,
                MarkerNormal = th.TryGetProperty("markerNormal", out var v6) ? v6.GetDouble() : 0.50,
                DialogDropGraceSeconds = th.TryGetProperty("dialogDropGraceSeconds", out var v7) ? v7.GetDouble() : 0.30,
            };
        }

        // 干净机器首跑：VideoProcessor 依赖 VideoProcess 模板/字体资源。尽力联网确保（Check + 按需下载），
        // 但联网失败不阻断——只要本地已有资源（老用户/离线）仍可继续；真正缺失则在构造时给清晰错误。
        try
        {
            await ResourceManager.Instance.EnsureResource(ResourceType.VideoProcess);
        }
        catch
        {
            // 拿文件清单/下载失败（多为离线）：忽略，改用本地已有资源继续。
            // 若资源其实缺失，下面 new VideoProcessor 会抛出并转成清晰错误。
        }

        var config = new Config(videoPath, scriptPath, translatePath, matchingThreshold: threshold);
        var callbacks = BuildCallbacks();

        lock (_lock)
        {
            _processor?.StopProcess();
            _dialogs.Clear();
            _banners.Clear();
            _markers.Clear();
            try
            {
                _processor = new VideoProcessor(config, callbacks);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"打轴模板/字体资源缺失，且无法联网下载；请联网首次运行以下载 VideoProcess 资源：{ex.Message}", ex);
            }
        }

        // 刻意 fire-and-forget：start 语义是"立即回 ok 再后台流式跑"，不等长任务完成。
        _ = Task.Run(() =>
        {
            try
            {
                _processor.StartProcess();
            }
            catch (Exception ex)
            {
                _transport.SendNotification("subtitle.error", new { message = ex.Message });
            }
        });

        return "ok";
    }

    private Task<object?> StopAsync(JsonElement? @params)
    {
        lock (_lock)
        {
            _processor?.StopProcess();
        }
        return Task.FromResult<object?>("ok");
    }

    private Task<object?> ExportAsync(JsonElement? @params)
    {
        VideoProcessor? proc;
        List<BannerBaseFrameSet> banners;
        List<DialogBaseFrameSet> dialogs;
        List<MarkerBaseFrameSet> markers;
        lock (_lock)
        {
            proc = _processor;
            banners = [.._banners];
            dialogs = [.._dialogs];
            markers = [.._markers];
        }
        if (proc == null) throw new InvalidOperationException("No active processor");

        foreach (var dialog in dialogs)
            dialog.Data.BodyTranslated = dialog.Data.BodyTranslated.Replace("…", "...");

        var subtitle = proc.GenerateSubtitle(banners, dialogs, markers);
        return Task.FromResult<object?>(new { content = subtitle.ToString() });
    }

    private VideoProcessCallbacks BuildCallbacks()
    {
        return new VideoProcessCallbacks
        {
            OnTaskStarted = () =>
            {
                var cl = _processor?.ContentLength;
                _transport.SendNotification("subtitle.started", new
                {
                    dialogTotal = cl?.Dialog ?? 0,
                    bannerTotal = cl?.Banner ?? 0,
                    markerTotal = cl?.Marker ?? 0,
                });
            },
            OnTaskFinished = () =>
            {
                var reason = _processor?.StopReason.ToString() ?? "unknown";
                _transport.SendNotification("subtitle.finished", new { reason });
            },
            OnProgress = progress =>
            {
                _transport.SendNotification("subtitle.progress", new { percent = progress });
            },
            OnFps = (fps, eta) =>
            {
                _transport.SendNotification("subtitle.fps", new
                {
                    fps,
                    eta = eta.ToString(@"hh\:mm\:ss"),
                });
            },
            OnFramePreviewImage = mat =>
            {
                if (mat is null || mat.IsEmpty) return;
                try
                {
                    using var resized = new Mat();
                    var scale = 640.0 / mat.Width;
                    CvInvoke.Resize(mat, resized, new System.Drawing.Size(640, (int)(mat.Height * scale)));
                    using var buf = new Emgu.CV.Util.VectorOfByte();
                    CvInvoke.Imencode(".jpg", resized, buf,
                        new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 50));
                    _transport.SendNotification("subtitle.preview", new { base64 = Convert.ToBase64String(buf.ToArray()) });
                }
                catch { }
            },
            OnNewDialog = set =>
            {
                lock (_lock) _dialogs.Add(set);
                _transport.SendNotification("subtitle.dialog", SerializeFrameSet(set));
            },
            OnNewBanner = set =>
            {
                lock (_lock) _banners.Add(set);
                _transport.SendNotification("subtitle.banner", SerializeFrameSet(set));
            },
            OnNewMarker = set =>
            {
                lock (_lock) _markers.Add(set);
                _transport.SendNotification("subtitle.marker", SerializeFrameSet(set));
            },
            OnException = ex =>
            {
                _transport.SendNotification("subtitle.error", new { message = ex.Message });
            },
        };
    }

    private static object SerializeFrameSet(DialogBaseFrameSet set) => new
    {
        type = "dialog",
        character = set.Data.FinalCharacter,
        body = set.Data.BodyOriginal,
        bodyTranslated = set.Data.BodyTranslated,
        startIndex = set.StartIndex(),
        endIndex = set.EndIndex(),
    };

    private static object SerializeFrameSet(BannerBaseFrameSet set) => new
    {
        type = "banner",
        body = set.Data.BodyOriginal,
        bodyTranslated = set.Data.BodyTranslated,
        startIndex = set.StartIndex(),
        endIndex = set.EndIndex(),
    };

    private static object SerializeFrameSet(MarkerBaseFrameSet set) => new
    {
        type = "marker",
        body = set.Data.BodyOriginal,
        bodyTranslated = set.Data.BodyTranslated,
        startIndex = set.StartIndex(),
        endIndex = set.EndIndex(),
    };
}
