using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Emgu.CV;
using SekaiToolsApp.Imaging;
using SekaiToolsApp.Services;
using SekaiToolsApp.ViewModels;
using SekaiToolsApp.ViewModels.LineCards;
using SekaiToolsCore;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;

namespace SekaiToolsApp.Views.Pages;

/// <summary>
/// 字幕生成主页 code-behind。
/// </summary>
public partial class SubtitlePageView : UserControl, IAsyncDisposable
{
    private static readonly string[] VideoExtensions = [".mp4", ".avi", ".mkv", ".webm", ".wmv"];
    private static readonly string[] ScriptExtensions = [".json", ".asset"];
    private static readonly string[] TranslateExtensions = [".txt"];

    private readonly SubtitlePageViewModel _viewModel = new();

    private VideoProcessor? _videoProcessor;
    private VideoProcessor? _retiringVideoProcessor;
    private Task? _processorStartupTask;
    private WriteableBitmap? _previewBitmap;
    private DateTime _lastFpsRender = DateTime.MinValue;
    private bool _engineExceptionShown;
    private bool _stopRequested;
    private int _processSessionId;
    private string? _lastEngineExceptionMessage;
    private volatile bool _disposed;

    public SubtitlePageView()
    {
        InitializeComponent();
        DataContext = _viewModel;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Loaded += (_, _) => SetupColumnSplitter();

        _ = EnsureResourcesAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SubtitlePageViewModel ViewModel => _viewModel;

    #region 文件选择 / 拖放

    private async void OnBrowseVideoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync("视频文件", VideoExtensions);
        if (path != null) await ApplyFileSelectionAsync(path);
    }

    private async void OnBrowseScriptClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync("剧本文件", ScriptExtensions);
        if (path != null) await ApplyFileSelectionAsync(path);
    }

    private async void OnBrowseTranslateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync("翻译文件", TranslateExtensions);
        if (path != null) await ApplyFileSelectionAsync(path);
    }

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return null;

        var fileTypes = new List<FilePickerFileType>
        {
            new(title)
            {
                Patterns = extensions.Select(e => "*" + e).ToArray(),
            },
            new("所有文件") { Patterns = ["*.*"] },
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"选择{title}",
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
        });
        var first = files.FirstOrDefault();
        return first?.TryGetLocalPath();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Link : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        var first = files?.FirstOrDefault();
        var path = first?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        await ApplyFileSelectionAsync(path);
    }

    /// <summary>
    /// 把指定文件按扩展名归类到对应字段，并尝试从同目录拉取同名兄弟文件
    /// （video↔script↔translate 三者相互联动）。
    /// </summary>
    private async Task ApplyFileSelectionAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (VideoExtensions.Contains(ext))
        {
            _viewModel.VideoFilePath = path;
            await TryAttachSiblingsAsync(path, includeVideo: false);
        }
        else if (ScriptExtensions.Contains(ext))
        {
            _viewModel.ScriptFilePath = path;
            await TryAttachSiblingsAsync(path, includeScript: false);
        }
        else if (TranslateExtensions.Contains(ext))
        {
            _viewModel.TranslateFilePath = path;
            await TryAttachSiblingsAsync(path, includeTranslate: false);
        }
    }

    private async Task TryAttachSiblingsAsync(string path,
        bool includeVideo = true, bool includeScript = true, bool includeTranslate = true)
    {
        string? siblingVideo = null;
        string? siblingScript = null;
        string? siblingTranslate = null;

        if (includeVideo)
            siblingVideo = VideoExtensions
                .Select(e => Path.ChangeExtension(path, e))
                .FirstOrDefault(File.Exists);

        if (includeScript)
            siblingScript = ScriptExtensions
                .Select(e => Path.ChangeExtension(path, e))
                .FirstOrDefault(File.Exists);

        if (includeTranslate)
            siblingTranslate = TranslateExtensions
                .Select(e => Path.ChangeExtension(path, e))
                .FirstOrDefault(File.Exists);

        if (siblingVideo == null && siblingScript == null && siblingTranslate == null)
            return;

        var confirmed = await ConfirmAttachSiblingsAsync();
        if (!confirmed) return;

        if (siblingVideo != null) _viewModel.VideoFilePath = siblingVideo;
        if (siblingScript != null) _viewModel.ScriptFilePath = siblingScript;
        if (siblingTranslate != null) _viewModel.TranslateFilePath = siblingTranslate;
    }

    private async Task<bool> ConfirmAttachSiblingsAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window owner) return true;

        var dialog = new Window
        {
            Title = "提示",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var result = false;
        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = "在该文件处发现了同名的文件，是否自动引入作为处理文件？",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        var yes = new Button { Content = "是", Width = 80, Classes = { "accent" } };
        yes.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        var no = new Button { Content = "否", Width = 80 };
        no.Click += (_, _) => dialog.Close();
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        stack.Children.Add(buttons);
        dialog.Content = stack;
        await dialog.ShowDialog(owner);
        return result;
    }

    #endregion

    #region 启动 / 停止 / 重置

    private void OnResetClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _stopRequested = true;
        _processSessionId++;
        StopVideoProcessor(clearProcessor: true);
        _viewModel.ResetAll();
    }

    private void OnStopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _stopRequested = true;
        _viewModel.RunState = SubtitleRunState.Stopped;
        _viewModel.EtaText = string.Empty;
        StopVideoProcessor(clearProcessor: false);
    }

    private async void OnExportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_viewModel.CanExport) return;

        if (_videoProcessor == null)
        {
            await ShowErrorAsync("无法输出", "当前没有可用的视频处理上下文，请重新运行识别后再输出。");
            return;
        }

        var path = await PickAssSavePathAsync();
        if (path == null) return;

        try
        {
            var subtitle = GenerateSubtitle();
            await File.WriteAllTextAsync(path, subtitle.ToString(), Encoding.UTF8);
            await ShowInfoAsync("输出完成", "字幕文件已保存：\n" + path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("输出失败", ex.Message);
        }
    }

    private async void OnStartClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_disposed || _viewModel.IsRunning) return;
        if (!_viewModel.IsResourceReady)
        {
            await ShowErrorAsync("资源未就绪", "模板资源还没有准备完成，请等待资源检查/下载结束后再开始。");
            return;
        }
        if (!await ValidateInputsAsync()) return;

        // 准备阶段（轻量）：在 UI 线程做基础校验。
        _viewModel.ResetRunData();
        _engineExceptionShown = false;
        _stopRequested = false;
        _lastEngineExceptionMessage = null;
        try
        {
            EnsureRequiredVideoProcessResources();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("资源缺失", ex.Message);
            _viewModel.RunState = SubtitleRunState.Failed;
            return;
        }

        var settings = SettingsService.Instance.Current;
        Config config;
        try
        {
            config = _viewModel.BuildEngineConfig(settings);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("配置错误", ex.Message);
            _viewModel.RunState = SubtitleRunState.Failed;
            return;
        }

        var sessionId = ++_processSessionId;
        _viewModel.RunState = SubtitleRunState.Running;

        var previous = _videoProcessor ?? _retiringVideoProcessor;
        _videoProcessor = null;
        _retiringVideoProcessor = null;
        if (previous != null)
        {
            await Task.Run(previous.Dispose);
            if (previous.IsProcessing)
            {
                _retiringVideoProcessor = previous;
                if (_disposed || sessionId != _processSessionId) return;
                _viewModel.RunState = SubtitleRunState.Failed;
                await ShowErrorAsync("上一任务仍在停止", "视频解码仍卡在原生读取中，请稍后重试；为避免并发释放导致崩溃，本次未启动新任务。");
                return;
            }
        }
        if (_disposed || sessionId != _processSessionId) return;

        // 重 IO / native 部分（VideoCapture、模板加载）放后台线程，避免：
        // 1) UI 线程被 native 调用阻塞；
        // 2) EmguCV / 视频解码异常未被托管 catch 时直接闪退。
        VideoProcessor? processor = null;
        Exception? constructError = null;
        var startupTask = Task.Run(() =>
        {
            try
            {
                processor = new VideoProcessor(config, BuildCallbacks(sessionId));
                if (_disposed || sessionId != Volatile.Read(ref _processSessionId))
                {
                    processor.Dispose();
                    processor = null;
                }
            }
            catch (Exception ex)
            {
                constructError = ex;
            }
        });
        _processorStartupTask = startupTask;
        try
        {
            await startupTask;
        }
        finally
        {
            if (ReferenceEquals(_processorStartupTask, startupTask))
                _processorStartupTask = null;
        }

        if (_disposed || sessionId != _processSessionId)
        {
            processor?.Dispose();
            return;
        }

        if (constructError != null || processor == null)
        {
            _viewModel.RunState = SubtitleRunState.Failed;
            var detail = constructError?.InnerException is { } inner
                ? $"{constructError.Message}\n\n内层错误：{inner.GetType().Name}: {inner.Message}"
                : (constructError?.Message ?? "VideoProcessor 构造返回空。");
            await ShowErrorAsync("无法启动",
                "视频处理器初始化失败：\n" + detail +
                "\n\n常见原因：视频文件无法被解码（缺少 codec/损坏）、模板资源缺失、剧本 JSON 解析失败。");
            return;
        }

        _videoProcessor = processor;

        try
        {
            _videoProcessor.StartProcess();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_videoProcessor, processor))
                _videoProcessor = null;
            processor.Dispose();
            await ShowErrorAsync("无法启动", ex.Message);
            _viewModel.RunState = SubtitleRunState.Failed;
        }
    }

    private async Task<bool> ValidateInputsAsync()
    {
        var v = _viewModel.VideoFilePath;
        var s = _viewModel.ScriptFilePath;
        var t = _viewModel.TranslateFilePath;

        if (string.IsNullOrEmpty(v) || string.IsNullOrEmpty(s))
        {
            await ShowErrorAsync("配置不完整", "请至少选择视频与剧本文件。");
            return false;
        }

        if (!File.Exists(v))
        {
            await ShowErrorAsync("文件不存在", "视频文件不存在：\n" + v);
            return false;
        }

        if (!File.Exists(s))
        {
            await ShowErrorAsync("文件不存在", "剧本文件不存在：\n" + s);
            return false;
        }

        if (!string.IsNullOrEmpty(t) && !File.Exists(t))
        {
            await ShowErrorAsync("文件不存在", "翻译文件不存在：\n" + t);
            return false;
        }

        return true;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window owner) return;

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var ok = new Button
        {
            Content = "确定",
            Width = 100,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Classes = { "accent" },
        };
        ok.Click += (_, _) => dialog.Close();
        stack.Children.Add(ok);
        dialog.Content = stack;
        await dialog.ShowDialog(owner);
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        await ShowErrorAsync(title, message);
    }

    private async Task<string?> PickAssSavePathAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return null;

        var suggested = string.IsNullOrEmpty(_viewModel.VideoFilePath)
            ? "subtitle.ass"
            : Path.ChangeExtension(Path.GetFileName(_viewModel.VideoFilePath), ".ass");

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存 ASS 字幕",
            SuggestedFileName = suggested,
            DefaultExtension = "ass",
            FileTypeChoices =
            [
                new FilePickerFileType("ASS 字幕") { Patterns = ["*.ass"] },
                new FilePickerFileType("所有文件") { Patterns = ["*.*"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    private SekaiToolsBase.SubStationAlpha.Subtitle GenerateSubtitle()
    {
        if (_videoProcessor == null) throw new InvalidOperationException("VideoProcessor is not available.");

        var bannerFrameSets = new List<BannerBaseFrameSet>();
        var dialogFrameSets = new List<DialogBaseFrameSet>();
        var markerFrameSets = new List<MarkerBaseFrameSet>();

        foreach (var card in _viewModel.LineCards)
        {
            switch (card)
            {
                case DialogLineCardViewModel dialog:
                    // SubtitleMaker clones and normalizes export data. Keep the live
                    // editor model byte-for-byte unchanged by an export operation.
                    dialogFrameSets.Add(dialog.Set);
                    break;
                case BannerLineCardViewModel banner:
                    bannerFrameSets.Add(banner.Set);
                    break;
                case MarkerLineCardViewModel marker:
                    markerFrameSets.Add(marker.Set);
                    break;
            }
        }

        return _videoProcessor.GenerateSubtitle(bannerFrameSets, dialogFrameSets, markerFrameSets);
    }

    private void StopVideoProcessor(bool clearProcessor)
    {
        var current = _videoProcessor;
        if (clearProcessor) _videoProcessor = null;
        try { current?.StopProcess(); }
        catch { /* 静默：StopProcess 只取消 token，不阻塞 UI。 */ }
        if (clearProcessor && current != null)
        {
            _retiringVideoProcessor = current;
            _ = Task.Run(() =>
            {
                current.Dispose();
                if (!current.IsProcessing)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_retiringVideoProcessor, current))
                            _retiringVideoProcessor = null;
                    });
                }
            });
        }
    }

    #endregion

    #region 引擎回调

    private VideoProcessCallbacks BuildCallbacks(int sessionId)
    {
        return new VideoProcessCallbacks
        {
            OnTaskStarted = () => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _processSessionId) return;
                if (_videoProcessor == null) return;
                var contentLength = _videoProcessor.ContentLength;
                _viewModel.DialogTotal = contentLength.Dialog;
                _viewModel.DialogCurrent = 0;
                _viewModel.BannerTotal = contentLength.Banner;
                _viewModel.BannerCurrent = 0;
                _viewModel.MarkerTotal = contentLength.Marker;
                _viewModel.MarkerCurrent = 0;
            }),
            OnTaskFinished = () => Dispatcher.UIThread.Post(async () =>
            {
                if (sessionId != _processSessionId) return;
                if (_videoProcessor == null) return;

                // 读取上游引擎的停止原因，决定 UI 最终状态。
                var reason = _videoProcessor.StopReason;
                var (state, reasonText) = reason switch
                {
                    ProcessStopReason.Completed => (SubtitleRunState.Finished, "处理完成"),
                    ProcessStopReason.ReadFailed => (SubtitleRunState.Finished, "视频读取结束"),
                    ProcessStopReason.Canceled => (SubtitleRunState.Stopped, "已停止"),
                    ProcessStopReason.ExceptionThreshold => (SubtitleRunState.Failed,
                        "连续异常过多，已中止处理"),
                    ProcessStopReason.CaptureError => (SubtitleRunState.Failed, "无法读取视频或资源"),
                    _ => (_stopRequested ? SubtitleRunState.Stopped : SubtitleRunState.Finished, string.Empty),
                };

                _viewModel.RunState = state;
                _viewModel.StopReasonText = reasonText;
                _viewModel.EtaText = string.Empty;

                // 失败才弹错误对话框；需避免重复弹出。
                if (state == SubtitleRunState.Failed && !_engineExceptionShown)
                {
                    _engineExceptionShown = true;
                    var head = string.IsNullOrEmpty(reasonText) ? "视频处理出错" : reasonText;
                    var detail = _lastEngineExceptionMessage;
                    var message = string.IsNullOrEmpty(detail)
                        ? head + "。\n\n请确认视频是 16:9 或接近 16:9 的完整游戏剧情录屏，且没有裁剪 UI 或额外黑边。"
                        : head + "：" + detail +
                          "\n\n处理已停止。请确认视频是 16:9 或接近 16:9 的完整游戏剧情录屏，且没有裁剪 UI 或额外黑边。";
                    await ShowErrorAsync("视频处理出错", message);
                }
            }),
            OnProgress = p => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _processSessionId || _stopRequested) return;
                _viewModel.Progress = p;
                _viewModel.ProgressText = $"{p:P}";
            }),
            OnFps = (fps, eta) =>
            {
                // 200ms 节流：避免 UI 线程被 FPS 推送淹没。
                var now = DateTime.UtcNow;
                if ((now - _lastFpsRender).TotalMilliseconds < 200) return;
                _lastFpsRender = now;
                Dispatcher.UIThread.Post(() =>
                {
                    if (sessionId != _processSessionId || _stopRequested) return;
                    _viewModel.Fps = fps;
                    _viewModel.FpsText = $"FPS: {fps}";
                    _viewModel.EtaText = eta.TotalMilliseconds > 1000
                        ? $"ETA: {FormatEta(eta)}"
                        : string.Empty;
                });
            },
            // 关键：上游 StartPreviewConsumer 在调用本回调后会 *立刻* Dispose 原 Mat，
            // 而 Dispatcher.UIThread.Post 是异步派发，UI 线程实际访问 mat 时它已被
            // native 释放 → EmguCV 解引用越界 → 进程级 segfault（无托管异常、无弹框）。
            // 因此必须在后台线程立刻 Clone（在原 Mat 还活着时拷贝出独立 native buffer），
            // 把所有权完整转交给 UI 线程，UI 线程用完自己 dispose。
            OnFramePreviewImage = mat =>
            {
                if (mat is null || mat.IsEmpty) return;
                Mat clone;
                try
                {
                    clone = mat.Clone();
                }
                catch
                {
                    return;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (sessionId != _processSessionId || _stopRequested) return;
                        UpdateFramePreview(clone);
                    }
                    finally
                    {
                        clone.Dispose();
                    }
                });
            },
            OnNewDialog = set => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _processSessionId || _stopRequested) return;
                AppendLineCard(new DialogLineCardViewModel(set));
                _viewModel.DialogCurrent++;
            }),
            OnNewBanner = set => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _processSessionId || _stopRequested) return;
                AppendLineCard(new BannerLineCardViewModel(set));
                _viewModel.BannerCurrent++;
            }),
            OnNewMarker = set => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _processSessionId || _stopRequested) return;
                AppendLineCard(new MarkerLineCardViewModel(set));
                _viewModel.MarkerCurrent++;
            }),
            // 上游引擎会在单次可恢复异常时也推 OnException（并继续处理），
            // 只有连续异常达到阈值才会 break + StopReason=ExceptionThreshold。
            // 这里不再立即判定为失败，仅记录最近一次异常，由 OnTaskFinished 根据停止原因决定 UI。
            OnException = ex => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _processSessionId) return;
                _lastEngineExceptionMessage = ex.Message;
            }),
        };
    }

    private void AppendLineCard(LineCardViewModelBase card)
    {
        var scroller = this.FindControl<ScrollViewer>("LineScroller");
        var stickToBottom = false;
        if (scroller != null)
        {
            var extentHeight = scroller.Extent.Height;
            var viewportHeight = scroller.Viewport.Height;
            var offset = scroller.Offset.Y;
            // 距底小于 2 个卡片高度（估计 ≈ 200px）时认为在跟随底部。
            stickToBottom = extentHeight <= viewportHeight + 1 ||
                            offset >= extentHeight - viewportHeight - 200;
        }

        _viewModel.AddLineCard(card);

        if (stickToBottom && scroller != null)
        {
            // 需等一个渲染轮让 ItemsControl 重新 measure。
            Dispatcher.UIThread.Post(() => scroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void UpdateFramePreview(Mat mat)
    {
        if (mat is null || mat.IsEmpty) return;
        if (!_viewModel.ShowPreview) return;

        try
        {
            // 高频路径：尽量复用 WriteableBitmap，仅尺寸变化时重建。
            if (_previewBitmap == null ||
                _previewBitmap.PixelSize.Width != mat.Width ||
                _previewBitmap.PixelSize.Height != mat.Height)
            {
                _previewBitmap?.Dispose();
                _previewBitmap = EmguCvAvaloniaInterop.CreateWriteableBitmap(mat);
                _viewModel.FramePreview = _previewBitmap;
            }

            if (!EmguCvAvaloniaInterop.WriteTo(mat, _previewBitmap))
            {
                _previewBitmap = EmguCvAvaloniaInterop.CreateWriteableBitmap(mat);
                EmguCvAvaloniaInterop.WriteTo(mat, _previewBitmap);
                _viewModel.FramePreview = _previewBitmap;
            }
            else
            {
                // WriteableBitmap 内容更新后强制 Image 重绘。
                _viewModel.FramePreview = _previewBitmap;
            }
        }
        catch
        {
            // 帧预览不影响主流程，吞掉异常。
        }
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
            return $"{(int)eta.TotalHours}h{eta.Minutes:D2}m{eta.Seconds:D2}s";
        if (eta.TotalMinutes >= 1)
            return $"{(int)eta.TotalMinutes}m{eta.Seconds:D2}s";
        return $"{(int)eta.TotalSeconds}s";
    }

    #endregion

    #region 预览显示切换

    private void OnPreviewDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        _viewModel.ShowPreview = false;
    }

    private void OnShowPreviewClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.ShowPreview = true;
    }

    #endregion

    #region 资源引导

    /// <summary>
    /// 检查/下载 VideoProcess 模板资源（OCR 匹配的若干 PNG）。成功后打开 Start 按钮，
    /// 失败则在覆盖层上显示错误与重试按钮。
    /// </summary>
    private async Task EnsureResourcesAsync()
    {
        // 后台线程执行 IO + 网络，避免阻塞 UI；状态回写通过 Dispatcher 主线程应用。
        await Task.Run(async () =>
        {
            try
            {
                await SetResourceStateAsync(ResourceReadyState.Checking, "正在检查模板资源…");

                var ok = await ResourceManager.Instance.CheckResource(ResourceType.VideoProcess);
                if (!ok)
                {
                    await SetResourceStateAsync(
                        ResourceReadyState.Checking,
                        "正在下载模板资源（首次运行需要从服务器拉取，可能需要几分钟）…");
                    await ResourceManager.Instance.EnsureResource(ResourceType.VideoProcess);
                }

                EnsureRequiredVideoProcessResources();
                await SetResourceStateAsync(ResourceReadyState.Ready,
                    "模板资源已准备完成，可以开始处理视频。");
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("访问权限") || msg.Contains("WSAEACCES") ||
                    msg.Contains("permission", StringComparison.OrdinalIgnoreCase))
                    msg += "\n\n提示：Windows 上可尝试以管理员模式启动应用解决此问题。";
                await SetResourceStateAsync(ResourceReadyState.Failed,
                    "模板资源准备失败：\n" + msg);
            }
        });
    }

    private Task SetResourceStateAsync(ResourceReadyState state, string text)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed) return;
            _viewModel.ResourceState = state;
            _viewModel.ResourceStatusText = text;
        }).GetTask();
    }

    private void OnRetryResourceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.ResourceOverlayDismissed = false;
        _ = EnsureResourcesAsync();
    }

    private void OnDismissResourceOverlayClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.ResourceOverlayDismissed = true;
    }

    private static void EnsureRequiredVideoProcessResources()
    {
        string[] required =
        [
            "menu-107px.png",
            "FOT-RodinNTLGPro-DB.otf",
            "FOT-RodinNTLGPro-EB.otf",
        ];

        foreach (var file in required)
        {
            _ = ResourceManager.Instance.ResourcePath(ResourceType.VideoProcess, file);
        }
    }

    #endregion

    #region Column Splitter

    private bool _splitterDragging;
    private Point _splitterStart;
    private double _splitterOriginalWidth;

    private void SetupColumnSplitter()
    {
        var splitter = this.FindControl<Border>("ColumnSplitter");
        if (splitter is null) return;

        splitter.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
            _splitterDragging = true;
            _splitterStart = e.GetPosition(this);
            var grid = splitter.Parent as Grid;
            if (grid != null)
                _splitterOriginalWidth = grid.ColumnDefinitions[0].ActualWidth;
            e.Pointer.Capture(splitter);
            e.Handled = true;
        };

        splitter.PointerMoved += (_, e) =>
        {
            if (!_splitterDragging) return;
            var pos = e.GetPosition(this);
            var delta = pos.X - _splitterStart.X;
            var grid = splitter.Parent as Grid;
            if (grid is null) return;
            var col = grid.ColumnDefinitions[0];
            var newWidth = Math.Clamp(_splitterOriginalWidth + delta, col.MinWidth, col.MaxWidth);
            col.Width = new GridLength(newWidth, GridUnitType.Pixel);
            e.Handled = true;
        };

        splitter.PointerReleased += (_, e) =>
        {
            if (!_splitterDragging) return;
            _splitterDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested = true;
        ++_processSessionId;

        var startup = _processorStartupTask;
        if (startup != null)
            await startup.ConfigureAwait(false);

        var processors = new[] { _videoProcessor, _retiringVideoProcessor }
            .Where(processor => processor != null)
            .Distinct()
            .Cast<VideoProcessor>()
            .ToArray();
        _videoProcessor = null;
        _retiringVideoProcessor = null;
        foreach (var processor in processors) processor.StopProcess();
        await Task.WhenAll(processors.Select(processor => Task.Run(processor.Dispose)));

        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }
}
