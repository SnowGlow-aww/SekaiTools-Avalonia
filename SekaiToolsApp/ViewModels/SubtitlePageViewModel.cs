using System;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SekaiToolsCore.Process.Config;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsApp.Services;
using SekaiToolsApp.ViewModels.LineCards;

namespace SekaiToolsApp.ViewModels;

/// <summary>
/// 字幕生成主页面 ViewModel。
///
/// 该页面驱动一个 <see cref="SekaiToolsCore.VideoProcessor"/> 后台流水线：
/// - 用户依次给出视频 / 剧本 (json|asset) / 翻译 (txt) 三个文件路径；
/// - 后台逐帧匹配模板，识别对话 / 横幅 / 地点角标三类元素；
/// - 实时回调进度、FPS/ETA、当前帧预览。
///
/// 本 VM 只承载 <see cref="SubtitleRunState"/> 与展示数据，VideoProcessor 的实际驱动
/// 写在 <see cref="Views.Pages.SubtitlePageView"/> code-behind 中（避免 VM 直接持有
/// 高频更新的 Mat / Avalonia 控件，便于关闭页面时正确释放）。
/// </summary>
public partial class SubtitlePageViewModel : ViewModelBase
{
    public SubtitlePageViewModel()
    {
        // 默认占位预览：256x144 全黑，避免控件初始 0x0 引起的布局抖动。
        FramePreview = CreateBlankPreview(256, 144);
    }

    #region 文件路径

    [ObservableProperty] private string _videoFilePath = string.Empty;
    [ObservableProperty] private string _scriptFilePath = string.Empty;
    [ObservableProperty] private string _translateFilePath = string.Empty;

    public bool HasVideoFilePath => !string.IsNullOrWhiteSpace(VideoFilePath);
    public bool HasScriptFilePath => !string.IsNullOrWhiteSpace(ScriptFilePath);
    public bool HasTranslateFilePath => !string.IsNullOrWhiteSpace(TranslateFilePath);

    partial void OnVideoFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasVideoFilePath));
        OnPropertyChanged(nameof(CanReset));
    }

    partial void OnScriptFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasScriptFilePath));
        OnPropertyChanged(nameof(CanReset));
    }

    partial void OnTranslateFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasTranslateFilePath));
        OnPropertyChanged(nameof(CanReset));
    }

    #endregion

    #region 运行状态

    [ObservableProperty] private SubtitleRunState _runState = SubtitleRunState.NotStarted;

    public bool HasNotStarted => RunState == SubtitleRunState.NotStarted;
    public bool IsRunning => RunState == SubtitleRunState.Running;
    public bool IsFinished => RunState == SubtitleRunState.Finished;
    public bool IsStopped => RunState == SubtitleRunState.Stopped;
    public bool CanExport => IsFinished || IsStopped;
    public bool CanReset => !HasNotStarted ||
                            HasVideoFilePath || HasScriptFilePath || HasTranslateFilePath;

    // Start 同时要求资源已就绪，否则 VideoProcessor 会在构造阶段抛文件缺失。
    public bool CanStart => HasNotStarted && IsResourceReady;
    public bool CanStop => IsRunning;

    public string RunStateText => RunState switch
    {
        SubtitleRunState.NotStarted => "未开始",
        SubtitleRunState.Running => "运行中",
        SubtitleRunState.Finished => "已完成",
        SubtitleRunState.Stopped => "已停止",
        SubtitleRunState.Failed => "已失败",
        _ => string.Empty,
    };

    partial void OnRunStateChanged(SubtitleRunState value)
    {
        OnPropertyChanged(nameof(HasNotStarted));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(RunStateText));
    }

    #endregion

    #region 进度 / FPS / ETA

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;

    [ObservableProperty] private int _fps;
    [ObservableProperty] private string _fpsText = string.Empty;

    [ObservableProperty] private string _etaText = string.Empty;

    /// <summary>
    /// 后台引擎结束后的停止原因文本（上游 <c>VideoProcessor.StopReason</c> 的 UI 投影）。
    /// 仅在 <see cref="RunState"/> 进入 Finished/Stopped/Failed 时设置，运行中保持空串。
    /// </summary>
    [ObservableProperty] private string _stopReasonText = string.Empty;

    public bool HasFpsText => !string.IsNullOrEmpty(FpsText);
    public bool HasEtaText => !string.IsNullOrEmpty(EtaText);
    public bool HasStopReasonText => !string.IsNullOrEmpty(StopReasonText);

    partial void OnFpsTextChanged(string value) => OnPropertyChanged(nameof(HasFpsText));
    partial void OnEtaTextChanged(string value) => OnPropertyChanged(nameof(HasEtaText));
    partial void OnStopReasonTextChanged(string value) => OnPropertyChanged(nameof(HasStopReasonText));

    #endregion

    #region 三类元素计数

    [ObservableProperty] private int _dialogTotal;
    [ObservableProperty] private int _dialogCurrent;
    [ObservableProperty] private int _bannerTotal;
    [ObservableProperty] private int _bannerCurrent;
    [ObservableProperty] private int _markerTotal;
    [ObservableProperty] private int _markerCurrent;

    /// <summary>
    /// 三类元素统一行卡片集合，按引擎回调的插入顺序排列。
    /// 行卡片视图通过 <see cref="LineCardViewModelBase.Visible"/> 控制隐藏，避免重新构造控件。
    /// </summary>
    public ObservableCollection<LineCardViewModelBase> LineCards { get; } = new();

    public void AddLineCard(LineCardViewModelBase card)
    {
        ApplyVisibility(card);
        LineCards.Add(card);
        OnPropertyChanged(nameof(CanExport));
    }

    public void RefreshLineVisibility()
    {
        foreach (var card in LineCards)
            ApplyVisibility(card);
    }

    private void ApplyVisibility(LineCardViewModelBase card)
    {
        card.Visible = card switch
        {
            DialogLineCardViewModel { IsTooLongLine: true } => ShowDialog,
            DialogLineCardViewModel => ShowDialog && !ShowTooLongOnly,
            BannerLineCardViewModel => ShowBanner,
            MarkerLineCardViewModel => ShowMarker,
            _ => true,
        };
    }

    #endregion

    #region 资源引导

    [ObservableProperty] private ResourceReadyState _resourceState = ResourceReadyState.NotChecked;
    [ObservableProperty] private string _resourceStatusText = string.Empty;
    [ObservableProperty] private bool _resourceOverlayDismissed;

    public bool IsResourceReady => ResourceState == ResourceReadyState.Ready;
    public bool IsResourceNotReady => ResourceState != ResourceReadyState.Ready ||
                                      (ResourceState == ResourceReadyState.Ready && !ResourceOverlayDismissed);
    public bool IsResourceChecking => ResourceState is ResourceReadyState.Checking or ResourceReadyState.NotChecked;
    public bool IsResourceFailed => ResourceState == ResourceReadyState.Failed;

    partial void OnResourceStateChanged(ResourceReadyState value)
    {
        OnPropertyChanged(nameof(IsResourceReady));
        OnPropertyChanged(nameof(IsResourceNotReady));
        OnPropertyChanged(nameof(IsResourceChecking));
        OnPropertyChanged(nameof(IsResourceFailed));
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnResourceOverlayDismissedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsResourceNotReady));
    }

    #endregion

    #region 预览 / 过滤显示

    [ObservableProperty] private Bitmap? _framePreview;
    [ObservableProperty] private bool _showPreview = true;

    [ObservableProperty] private bool _showDialog = true;
    [ObservableProperty] private bool _showBanner = true;
    [ObservableProperty] private bool _showMarker = true;
    [ObservableProperty] private bool _showTooLongOnly;

    partial void OnShowDialogChanged(bool value) => RefreshLineVisibility();
    partial void OnShowBannerChanged(bool value) => RefreshLineVisibility();
    partial void OnShowMarkerChanged(bool value) => RefreshLineVisibility();
    partial void OnShowTooLongOnlyChanged(bool value) => RefreshLineVisibility();

    #endregion

    #region 状态转移辅助

    public void ResetAll()
    {
        VideoFilePath = string.Empty;
        ScriptFilePath = string.Empty;
        TranslateFilePath = string.Empty;
        RunState = SubtitleRunState.NotStarted;
        Progress = 0;
        ProgressText = string.Empty;
        Fps = 0;
        FpsText = string.Empty;
        EtaText = string.Empty;
        StopReasonText = string.Empty;
        DialogTotal = 0;
        DialogCurrent = 0;
        BannerTotal = 0;
        BannerCurrent = 0;
        MarkerTotal = 0;
        MarkerCurrent = 0;
        LineCards.Clear();
        OnPropertyChanged(nameof(CanExport));
        FramePreview?.Dispose();
        FramePreview = CreateBlankPreview(256, 144);
    }

    public void ResetRunData()
    {
        RunState = SubtitleRunState.NotStarted;
        Progress = 0;
        ProgressText = string.Empty;
        Fps = 0;
        FpsText = string.Empty;
        EtaText = string.Empty;
        StopReasonText = string.Empty;
        DialogTotal = 0;
        DialogCurrent = 0;
        BannerTotal = 0;
        BannerCurrent = 0;
        MarkerTotal = 0;
        MarkerCurrent = 0;
        LineCards.Clear();
        OnPropertyChanged(nameof(CanExport));
    }

    public Config BuildEngineConfig(AppSettings appSettings)
    {
        if (appSettings is null) throw new ArgumentNullException(nameof(appSettings));

        return new Config(
            VideoFilePath,
            ScriptFilePath,
            TranslateFilePath,
            new StyleFontConfig
            {
                DialogFontFamily = appSettings.DialogFontFamily,
                BannerFontFamily = appSettings.BannerFontFamily,
                MarkerFontFamily = appSettings.MarkerFontFamily,
            },
            new ExportStyleConfig
            {
                ExportLine1 = appSettings.ExportLine1,
                ExportLine2 = appSettings.ExportLine2,
                ExportLine3 = appSettings.ExportLine3,
                ExportCharacter = appSettings.ExportCharacter,
                ExportBannerMask = appSettings.ExportBannerMask,
                ExportBannerText = appSettings.ExportBannerText,
                ExportMarkerMask = appSettings.ExportMarkerMask,
                ExportMarkerText = appSettings.ExportMarkerText,
                ExportScreenComment = appSettings.ExportScreenComment,
            },
            new TypewriterSetting
            {
                FadeTime = appSettings.TypewriterFadeTime,
                CharTime = appSettings.TypewriterCharTime,
            },
            new MatchingThreshold
            {
                DialogNametagNormal = appSettings.ThresholdDialogNametagNormal,
                DialogNametagSpecial = appSettings.ThresholdDialogNametagSpecial,
                DialogContentNormal = appSettings.ThresholdDialogContentNormal,
                DialogContentSpecial = appSettings.ThresholdDialogContentSpecial,
                BannerNormal = appSettings.ThresholdBannerNormal,
                MarkerNormal = appSettings.ThresholdMarkerNormal,
                DialogDropGraceSeconds = appSettings.DialogDropGraceSeconds,
            }
        );
    }

    private static Bitmap CreateBlankPreview(int width, int height)
    {
        var pixelSize = new Avalonia.PixelSize(width, height);
        var dpi = new Avalonia.Vector(96, 96);
        var bmp = new WriteableBitmap(pixelSize, dpi, Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Unpremul);
        return bmp;
    }

    #endregion
}

public enum SubtitleRunState
{
    NotStarted,
    Running,
    Finished,
    Stopped,
    Failed,
}

public enum ResourceReadyState
{
    /// <summary>尚未发起检查。</summary>
    NotChecked,

    /// <summary>正在检查或下载。</summary>
    Checking,

    /// <summary>所有模板资源均可用。</summary>
    Ready,

    /// <summary>检查/下载失败，需要手动重试。</summary>
    Failed,
}
