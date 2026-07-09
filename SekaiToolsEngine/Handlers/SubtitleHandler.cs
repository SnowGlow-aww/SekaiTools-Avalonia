using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SekaiToolsBase.Utils;
using SekaiToolsCore;
using SekaiToolsCore.Match.TemplateMatcher;
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

    // subtitle.frame 用独立 VideoCapture（不与识别线程共用句柄），按需懒开、start 换视频时重开。
    private string _videoPath = "";
    private VideoCapture? _frameCapture;
    private string _frameCapturePath = "";
    private readonly object _frameLock = new();

    public void Register(Dispatcher dispatcher)
    {
        dispatcher.Register("subtitle.start", StartAsync);
        dispatcher.Register("subtitle.stop", StopAsync);
        dispatcher.Register("subtitle.export", ExportAsync);
        dispatcher.Register("subtitle.lines", LinesAsync);
        dispatcher.Register("subtitle.setSeparator", SetSeparatorAsync);
        dispatcher.Register("subtitle.setTranslation", SetTranslationAsync);
        dispatcher.Register("subtitle.estimateSeparator", EstimateSeparatorAsync);
        dispatcher.Register("subtitle.frame", FrameAsync);
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

        lock (_frameLock)
        {
            _frameCapture?.Dispose();
            _frameCapture = null;
            _frameCapturePath = "";
            _videoPath = videoPath;
        }

        lock (_lock)
        {
            _processor?.StopProcess();
            _processor?.Dispose(); // 释放上一段视频的 VideoCapture/Token，避免连打多个视频时句柄堆积
            _dialogs.Clear();
            _banners.Clear();
            _markers.Clear();
            // 清掉跨运行的静态匹配缓存，杜绝上一段视频的缓存残留影响本次匹配。
            TemplateMatchCachePool.ResetAll();
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
                int idx;
                lock (_lock)
                {
                    _dialogs.Add(set);
                    idx = _dialogs.Count - 1;
                }

                _transport.SendNotification("subtitle.dialog", SerializeDialog(set, idx));
            },
            OnNewBanner = set =>
            {
                int idx;
                lock (_lock)
                {
                    _banners.Add(set);
                    idx = _banners.Count - 1;
                }

                _transport.SendNotification("subtitle.banner", SerializeFrameSet(set, idx));
            },
            OnNewMarker = set =>
            {
                int idx;
                lock (_lock)
                {
                    _markers.Add(set);
                    idx = _markers.Count - 1;
                }

                _transport.SendNotification("subtitle.marker", SerializeFrameSet(set, idx));
            },
            OnException = ex =>
            {
                _transport.SendNotification("subtitle.error", new { message = ex.Message });
            },
        };
    }

    private static object SerializeDialog(DialogBaseFrameSet set, int index) => new
    {
        type = "dialog",
        index,
        character = set.Data.FinalCharacter,
        body = set.Data.BodyOriginal,
        bodyTranslated = set.Data.BodyTranslated,
        startIndex = set.StartIndex(),
        endIndex = set.EndIndex(),
        shake = set.IsJitter,
        needSetSeparator = set.NeedSetSeparator,
        useSeparator = set.UseSeparator,
        separateFrame = set.Separate.SeparateFrame,
        separatorContentIndex = set.Separate.SeparatorContentIndex,
        contentLength = set.Data.BodyTranslated.TrimAll().Length,
    };

    private static object SerializeFrameSet(BannerBaseFrameSet set, int index) => new
    {
        type = "banner",
        index,
        body = set.Data.BodyOriginal,
        bodyTranslated = set.Data.BodyTranslated,
        startIndex = set.StartIndex(),
        endIndex = set.EndIndex(),
    };

    private static object SerializeFrameSet(MarkerBaseFrameSet set, int index) => new
    {
        type = "marker",
        index,
        body = set.Data.BodyOriginal,
        bodyTranslated = set.Data.BodyTranslated,
        startIndex = set.StartIndex(),
        endIndex = set.EndIndex(),
    };

    // ---- 行列表 / 分句编辑（服务插件端结果面板）----

    private Task<object?> LinesAsync(JsonElement? @params)
    {
        VideoProcessor? proc;
        List<DialogBaseFrameSet> dialogs;
        List<BannerBaseFrameSet> banners;
        List<MarkerBaseFrameSet> markers;
        lock (_lock)
        {
            proc = _processor;
            dialogs = [.._dialogs];
            banners = [.._banners];
            markers = [.._markers];
        }

        if (proc == null) throw new InvalidOperationException("No active processor");
        var running = proc.StopReason == ProcessStopReason.None;

        var lines = new List<object>();
        for (var i = 0; i < dialogs.Count; i++)
        {
            var set = dialogs[i];
            if (set.IsEmpty()) continue;
            if (running && !set.Finished) continue;
            // 行列表展示的默认分隔帧与导出同源（同一 EstimateSeparator），只在没有合法值时填充，
            // 用户已设的合法值绝不覆盖。识别进行中不估算（打字进度基线可能还没写完）。
            if (!running && set.UseSeparator &&
                (set.Separate.SeparateFrame <= set.StartIndex() || set.Separate.SeparateFrame >= set.EndIndex()))
            {
                try
                {
                    proc.EstimateSeparator(set);
                }
                catch
                {
                    set.InitSeparator();
                }
            }

            lines.Add(SerializeDialog(set, i));
        }

        for (var i = 0; i < banners.Count; i++)
        {
            if (banners[i].IsEmpty()) continue;
            lines.Add(SerializeFrameSet(banners[i], i));
        }

        for (var i = 0; i < markers.Count; i++)
        {
            if (markers[i].IsEmpty()) continue;
            lines.Add(SerializeFrameSet(markers[i], i));
        }

        var vi = proc.VideoInfo;
        return Task.FromResult<object?>(new
        {
            fps = vi.Fps.Fps(),
            width = vi.Resolution.Width,
            height = vi.Resolution.Height,
            frameCount = vi.FrameCount,
            finished = !running,
            lines,
        });
    }

    private Task<object?> SetSeparatorAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var index = RequireIndex(p);
        lock (_lock)
        {
            var set = DialogAt(index);
            if (p.TryGetProperty("useSeparator", out var us) &&
                us.ValueKind is JsonValueKind.True or JsonValueKind.False)
                set.UseSeparator = us.GetBoolean();

            var frame = set.Separate.SeparateFrame;
            var ci = set.Separate.SeparatorContentIndex;
            if (p.TryGetProperty("separateFrame", out var sf) && sf.ValueKind == JsonValueKind.Number)
                frame = Math.Clamp(sf.GetInt32(), set.StartIndex() + 1,
                    Math.Max(set.StartIndex() + 1, set.EndIndex() - 1));
            if (p.TryGetProperty("separatorContentIndex", out var ciEl) && ciEl.ValueKind == JsonValueKind.Number)
            {
                var len = set.Data.BodyTranslated.TrimAll().Length;
                ci = Math.Clamp(ciEl.GetInt32(), 1, Math.Max(1, len - 1));
            }

            set.SetSeparator(frame, ci);
            return Task.FromResult<object?>(SerializeDialog(set, index));
        }
    }

    private Task<object?> SetTranslationAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var index = RequireIndex(p);
        if (!p.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            throw new ArgumentException("text required");
        var text = (t.GetString() ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        lock (_lock)
        {
            var set = DialogAt(index);
            set.Data.BodyTranslated = text;
            var len = text.TrimAll().Length;
            var nl = text.IndexOf('\n');
            var ci = nl > 0
                ? text[..nl].TrimAll().Length // 显式换行：分割点=第一行长（与 GUI QuickEdit 一致）
                : set.Separate.SeparatorContentIndex;
            ci = Math.Clamp(ci, 1, Math.Max(1, len - 1));

            if (p.TryGetProperty("useSeparator", out var us) &&
                us.ValueKind is JsonValueKind.True or JsonValueKind.False)
                set.UseSeparator = us.GetBoolean();
            else
                set.UseSeparator = set.NeedSetSeparator;

            set.SetSeparator(set.Separate.SeparateFrame, ci);
            return Task.FromResult<object?>(SerializeDialog(set, index));
        }
    }

    private Task<object?> EstimateSeparatorAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        var index = RequireIndex(p);
        lock (_lock)
        {
            var proc = _processor ?? throw new InvalidOperationException("No active processor");
            var set = DialogAt(index);
            var old = set.Separate;
            try
            {
                if (p.TryGetProperty("separatorContentIndex", out var ciEl) && ciEl.ValueKind == JsonValueKind.Number)
                {
                    var len = set.Data.BodyTranslated.TrimAll().Length;
                    set.Separate.SeparatorContentIndex = Math.Clamp(ciEl.GetInt32(), 1, Math.Max(1, len - 1));
                }

                proc.EstimateSeparator(set);
                return Task.FromResult<object?>(new { separateFrame = set.Separate.SeparateFrame });
            }
            finally
            {
                set.Separate = old; // 只算建议值，不落地
            }
        }
    }

    private Task<object?> FrameAsync(JsonElement? @params)
    {
        if (@params == null) throw new ArgumentException("params required");
        var p = @params.Value;
        if (!p.TryGetProperty("frame", out var fEl) || fEl.ValueKind != JsonValueKind.Number)
            throw new ArgumentException("frame required");
        var frame = fEl.GetInt32();
        var maxWidth = p.TryGetProperty("maxWidth", out var mw) && mw.ValueKind == JsonValueKind.Number
            ? Math.Clamp(mw.GetInt32(), 64, 1280)
            : 480;

        lock (_frameLock)
        {
            if (_videoPath == "") throw new InvalidOperationException("尚未开始打轴，没有可用的视频");
            if (_frameCapture == null || _frameCapturePath != _videoPath)
            {
                _frameCapture?.Dispose();
                _frameCapture = new VideoCapture(_videoPath);
                _frameCapturePath = _videoPath;
            }

            var cap = _frameCapture;
            var total = (int)cap.Get(CapProp.FrameCount);
            if (total > 0) frame = Math.Clamp(frame, 0, total - 1);
            cap.Set(CapProp.PosFrames, frame);
            using var mat = new Mat();
            if (!cap.Read(mat) || mat.IsEmpty)
                throw new InvalidOperationException($"读取第 {frame} 帧失败");

            using var outMat = new Mat();
            if (mat.Width > maxWidth)
            {
                var scale = (double)maxWidth / mat.Width;
                CvInvoke.Resize(mat, outMat, new System.Drawing.Size(maxWidth, (int)(mat.Height * scale)));
            }
            else
            {
                mat.CopyTo(outMat);
            }

            using var buf = new Emgu.CV.Util.VectorOfByte();
            CvInvoke.Imencode(".jpg", outMat, buf,
                new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 70));
            return Task.FromResult<object?>(new { frame, base64 = Convert.ToBase64String(buf.ToArray()) });
        }
    }

    // 仅在持有 _lock 时调用。
    private DialogBaseFrameSet DialogAt(int index)
    {
        if (index < 0 || index >= _dialogs.Count)
            throw new ArgumentException($"index 越界: {index} (共 {_dialogs.Count} 条)");
        return _dialogs[index];
    }

    private static int RequireIndex(JsonElement p)
    {
        if (!p.TryGetProperty("index", out var el) || el.ValueKind != JsonValueKind.Number)
            throw new ArgumentException("index required");
        return el.GetInt32();
    }
}
