using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SekaiToolsApp.Services;
using SekaiToolsApp.ViewModels;

namespace SekaiToolsApp.Views.Pages;

/// <summary>
/// 视频压制页 code-behind。本类做 UI 编排（文件对话框、资源引导、生命周期管理），
/// <see cref="Suppressor"/> 跑后台子进程，<see cref="SuppressPageViewModel"/> 承载状态。
/// </summary>
public partial class SuppressPageView : UserControl, IAsyncDisposable
{
    private static readonly string[] VideoExtensions = [".mp4", ".avi", ".mkv", ".webm", ".wmv"];
    private static readonly string[] SubtitleExtensions = [".ass"];

    private readonly SuppressPageViewModel _viewModel = new();
    private Suppressor? _suppressor;
    private Task? _startupTask;
    private int _runSessionId;
    private volatile bool _disposed;

    public SuppressPageView()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _ = EnsureResourcesAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SuppressPageViewModel ViewModel => _viewModel;

    #region 文件选择

    private async void OnBrowseVideoClicked(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("视频文件", VideoExtensions);
        if (path != null) _viewModel.SourceVideo = path;
    }

    private async void OnBrowseSubtitleClicked(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("字幕文件", SubtitleExtensions);
        if (path != null) _viewModel.SourceSubtitle = path;
    }

    private async void OnBrowseOutputClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var suggested = string.IsNullOrEmpty(_viewModel.SourceVideo)
            ? "output.mp4"
            : "[STVS]" + Path.GetFileNameWithoutExtension(_viewModel.SourceVideo) + ".mp4";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存压制输出",
            SuggestedFileName = suggested,
            DefaultExtension = "mp4",
            FileTypeChoices =
            [
                new FilePickerFileType("MP4 视频") { Patterns = ["*.mp4"] },
                new FilePickerFileType("所有文件") { Patterns = ["*.*"] },
            ],
        });

        var local = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local)) _viewModel.OutputPath = local;
    }

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return null;

        var fileTypes = new List<FilePickerFileType>
        {
            new(title) { Patterns = extensions.Select(e => "*" + e).ToArray() },
            new("所有文件") { Patterns = ["*.*"] },
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"选择{title}",
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    #endregion

    #region 启动 / 停止 / 重置

    private async void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _viewModel.IsRunning) return;
        if (!_viewModel.IsPlatformSupported)
        {
            await ShowErrorAsync("平台不支持",
                "当前平台不在支持范围内。");
            return;
        }
        if (!_viewModel.IsResourceReady)
        {
            await ShowErrorAsync("压制环境未就绪", _viewModel.ResourceStatusText);
            return;
        }
        if (!_viewModel.HasSourceVideo || !_viewModel.HasOutputPath)
        {
            await ShowErrorAsync("配置不完整", "请先选择视频文件和输出路径。");
            return;
        }
        if (!File.Exists(_viewModel.SourceVideo))
        {
            await ShowErrorAsync("文件不存在", "视频文件不存在：\n" + _viewModel.SourceVideo);
            return;
        }
        if (_viewModel.HasSourceSubtitle && !File.Exists(_viewModel.SourceSubtitle))
        {
            await ShowErrorAsync("文件不存在", "字幕文件不存在：\n" + _viewModel.SourceSubtitle);
            return;
        }

        // 释放上一次的 Suppressor（如果之前是 Failed/Stopped 状态留着没清）。
        DisposeCurrentSuppressor();

        _viewModel.ResetRunData();
        _viewModel.RunState = SuppressRunState.Running;

        var sessionId = ++_runSessionId;
        var options = _viewModel.BuildOptions();

        Suppressor? suppressor = null;
        Exception? constructError = null;
        var startupTask = Task.Run(() =>
        {
            try
            {
                suppressor = new Suppressor(options, BuildCallbacks(sessionId));
                suppressor.Start();
                if (_disposed || sessionId != Volatile.Read(ref _runSessionId))
                {
                    suppressor.StopAsync().GetAwaiter().GetResult();
                    suppressor.Dispose();
                    suppressor = null;
                }
            }
            catch (Exception ex)
            {
                constructError = ex;
                suppressor?.Dispose();
                suppressor = null;
            }
        });
        _startupTask = startupTask;
        try
        {
            await startupTask;
        }
        finally
        {
            if (ReferenceEquals(_startupTask, startupTask))
                _startupTask = null;
        }

        if (_disposed || sessionId != _runSessionId)
        {
            if (suppressor != null)
            {
                try { await suppressor.StopAsync(); }
                finally { suppressor.Dispose(); }
            }
            return;
        }

        if (constructError != null || suppressor == null)
        {
            _viewModel.RunState = SuppressRunState.Failed;
            _viewModel.StopReasonText = "启动失败";
            await ShowErrorAsync("无法启动", FormatExceptionMessage(constructError));
            return;
        }

        _suppressor = suppressor;
    }

    private async void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsRunning || _suppressor == null) return;

        _viewModel.RunState = SuppressRunState.Stopped;
        _viewModel.StopReasonText = "正在停止…";
        try
        {
            await _suppressor.StopAsync();
        }
        catch
        {
            // StopAsync 内部已尽量吞掉子进程退出异常，外层留底。
        }
    }

    private void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning) return;

        _runSessionId++;
        DisposeCurrentSuppressor();
        _viewModel.ResetAll();
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        // 与 OnResetClicked 区别：仅清运行数据，保留路径配置，方便用户重压。
        if (_viewModel.IsRunning) return;

        _runSessionId++;
        DisposeCurrentSuppressor();
        _viewModel.ResetRunData();
    }

    private void OnShowOutputClicked(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanShowOutput) return;
        FileManagerService.RevealPath(_viewModel.OutputPath);
    }

    private void DisposeCurrentSuppressor()
    {
        var current = _suppressor;
        _suppressor = null;
        try { current?.Dispose(); }
        catch { /* Dispose 内部已 try/catch，外层留底。 */ }
    }

    #endregion

    #region 回调

    private SuppressorCallbacks BuildCallbacks(int sessionId)
    {
        return new SuppressorCallbacks
        {
            OnStarted = () => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _runSessionId) return;
                _viewModel.RunState = SuppressRunState.Running;
                _viewModel.AppendLog("[Sekai] 子进程已启动。");
            }),
            OnLogLine = line => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _runSessionId) return;
                _viewModel.AppendLog(line);
            }),
            OnProgressLogLine = line => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _runSessionId) return;
                _viewModel.ReplaceLastLog(line);
            }),
            OnProgress = (frame, total, fps) => Dispatcher.UIThread.Post(() =>
            {
                if (sessionId != _runSessionId) return;
                if (total > 0)
                {
                    _viewModel.Progress = Math.Clamp((double)frame / total, 0, 1);
                }
                _viewModel.Fps = fps;
                _viewModel.FpsText = fps > 0 ? $"FPS: {fps:0.##}" : string.Empty;
            }),
            OnFinished = (reason, error) => Dispatcher.UIThread.Post(async () =>
            {
                if (sessionId != _runSessionId) return;

                switch (reason)
                {
                    case SuppressorStopReason.Completed:
                        _viewModel.RunState = SuppressRunState.Finished;
                        _viewModel.StopReasonText = "压制完成";
                        _viewModel.Progress = 1;
                        break;
                    case SuppressorStopReason.Canceled:
                        _viewModel.RunState = SuppressRunState.Stopped;
                        _viewModel.StopReasonText = "已停止";
                        break;
                    case SuppressorStopReason.Failed:
                        _viewModel.RunState = SuppressRunState.Failed;
                        _viewModel.StopReasonText = "压制出错";
                        await ShowErrorAsync("压制出错", FormatExceptionMessage(error));
                        break;
                }
            }),
        };
    }

    #endregion

    #region 资源引导

    private async Task EnsureResourcesAsync()
    {
        await Task.Run(async () =>
        {
            try
            {
                await SetResourceStateAsync(ResourceReadyState.Checking, "正在检查压制运行环境…");

                var ffmpegHint = SettingsService.Instance.Current.FfmpegPath;
                var probe = Suppressor.ProbeRuntime(ffmpegHint);
                var message = probe.IsReady
                    ? probe.Message + "\n点击「开始使用」即可开始压制。"
                    : probe.Message;

                await SetResourceStateAsync(
                    probe.IsReady ? ResourceReadyState.Ready : ResourceReadyState.Failed,
                    message);

                if (probe.IsReady)
                {
                    var encoders = await SuppressRuntimeService.ProbeAvailableEncodersAsync(ffmpegHint);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _viewModel.AvailableEncoders = encoders;
                    }).GetTask();
                }
            }
            catch (Exception ex)
            {
                await SetResourceStateAsync(ResourceReadyState.Failed,
                    "压制环境检查失败：\n" + ex.Message);
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

    private void OnRetryResourceClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.ResourceOverlayDismissed = false;
        _ = EnsureResourcesAsync();
    }

    private void OnDismissResourceOverlayClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.ResourceOverlayDismissed = true;
    }

    #endregion

    #region 工具

    private async Task ShowErrorAsync(string title, string message)
    {
        var owner = GetOwnerWindow();
        if (owner is null) return;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
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

    private static Window? GetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    private static string FormatExceptionMessage(Exception? ex)
    {
        if (ex == null) return "未知错误。";
        var inner = ex.InnerException;
        return inner != null
            ? $"{ex.Message}\n\n内层错误：{inner.GetType().Name}: {inner.Message}"
            : ex.Message;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ++_runSessionId;
        var startup = _startupTask;
        if (startup != null)
            await startup.ConfigureAwait(false);

        var current = _suppressor;
        _suppressor = null;
        if (current == null) return;

        try
        {
            await current.StopAsync();
        }
        finally
        {
            current.Dispose();
        }
    }

    #endregion
}
