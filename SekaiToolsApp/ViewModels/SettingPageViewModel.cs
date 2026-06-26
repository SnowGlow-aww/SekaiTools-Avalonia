using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiToolsApp.Services;

namespace SekaiToolsApp.ViewModels;

/// <summary>
/// 设置页 ViewModel。所有可见属性都是 ObservableProperty，任意变更自动持久化。
/// 主题项变更会立刻应用到当前 Application（无需重启）。
/// </summary>
public partial class SettingPageViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private bool _suppressPersist;
    private bool _suppressTextUpdate;
    private string _proxyPortText = "1080";
    private string _typewriterFadeTimeText = "50";
    private string _typewriterCharTimeText = "80";

    public SettingPageViewModel() : this(SettingsService.Instance)
    {
    }

    public SettingPageViewModel(SettingsService settings)
    {
        _settings = settings;
        LoadFromService();
    }

    public string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    [ObservableProperty] private int _currentApplicationTheme;

    [ObservableProperty] private int _proxyType;

    [ObservableProperty] private string _proxyHost = "127.0.0.1";

    [ObservableProperty] private int _proxyPort = 1080;

    [ObservableProperty] private string _downloadDirectory = string.Empty;

    [ObservableProperty] private string _ffmpegPath = string.Empty;

    public string ProxyPortText
    {
        get => _proxyPortText;
        set
        {
            if (SetProperty(ref _proxyPortText, value))
                ValidateProxyPortText();
        }
    }

    /// <summary>是否需要显示代理 Host / Port 输入框（ProxyType != 0）。</summary>
    public bool ProxyChangeable => ProxyType != 0;

    [ObservableProperty] private int _typewriterFadeTime = 50;

    public string TypewriterFadeTimeText
    {
        get => _typewriterFadeTimeText;
        set
        {
            if (SetProperty(ref _typewriterFadeTimeText, value))
                ValidateTypewriterFadeTimeText();
        }
    }

    [ObservableProperty] private int _typewriterCharTime = 80;

    public string TypewriterCharTimeText
    {
        get => _typewriterCharTimeText;
        set
        {
            if (SetProperty(ref _typewriterCharTimeText, value))
                ValidateTypewriterCharTimeText();
        }
    }

    [ObservableProperty] private string _dialogFontFamily = "";

    [ObservableProperty] private string _bannerFontFamily = "";

    [ObservableProperty] private string _markerFontFamily = "";

    [ObservableProperty] private bool _exportLine1;
    [ObservableProperty] private bool _exportLine2;
    [ObservableProperty] private bool _exportLine3;
    [ObservableProperty] private bool _exportCharacter;
    [ObservableProperty] private bool _exportBannerMask;
    [ObservableProperty] private bool _exportBannerText;
    [ObservableProperty] private bool _exportMarkerMask;
    [ObservableProperty] private bool _exportMarkerText;
    [ObservableProperty] private bool _exportScreenComment;
    [ObservableProperty] private double _thresholdDialogNametagNormal = 0.80;
    [ObservableProperty] private double _thresholdDialogNametagSpecial = 0.60;
    [ObservableProperty] private double _thresholdDialogContentNormal = 0.80;
    [ObservableProperty] private double _thresholdDialogContentSpecial = 0.60;
    [ObservableProperty] private double _thresholdBannerNormal = 0.80;
    [ObservableProperty] private double _thresholdMarkerNormal = 0.80;
    [ObservableProperty] private double _dialogDropGraceSeconds = 0.30;
    [ObservableProperty] private string _proxyPortError = "";
    [ObservableProperty] private string _typewriterCharTimeError = "";
    [ObservableProperty] private string _typewriterFadeTimeError = "";
    [ObservableProperty] private string _saveStatusMessage = "";
    [ObservableProperty] private bool _saveStatusIsError;

    public bool HasProxyPortError => !string.IsNullOrWhiteSpace(ProxyPortError);
    public bool HasTypewriterCharTimeError => !string.IsNullOrWhiteSpace(TypewriterCharTimeError);
    public bool HasTypewriterFadeTimeError => !string.IsNullOrWhiteSpace(TypewriterFadeTimeError);
    public bool HasSaveStatus => !string.IsNullOrWhiteSpace(SaveStatusMessage);
    public bool SaveStatusIsSuccess => HasSaveStatus && !SaveStatusIsError;
    public bool HasValidationErrors => HasProxyPortError || HasTypewriterCharTimeError || HasTypewriterFadeTimeError;

    private void LoadFromService()
    {
        _suppressPersist = true;
        try
        {
            var s = _settings.Current;
            CurrentApplicationTheme = s.CurrentApplicationTheme;
            ProxyType = s.ProxyType;
            ProxyHost = s.ProxyHost;
            ProxyPort = s.ProxyPort;
            DownloadDirectory = s.DownloadDirectory;
            FfmpegPath = s.FfmpegPath;
            TypewriterFadeTime = s.TypewriterFadeTime;
            TypewriterCharTime = s.TypewriterCharTime;
            SetNumericTextFromValues();
            ClearValidationErrors();
            ClearSaveStatus();
            DialogFontFamily = s.DialogFontFamily;
            BannerFontFamily = s.BannerFontFamily;
            MarkerFontFamily = s.MarkerFontFamily;
            ExportLine1 = s.ExportLine1;
            ExportLine2 = s.ExportLine2;
            ExportLine3 = s.ExportLine3;
            ExportCharacter = s.ExportCharacter;
            ExportBannerMask = s.ExportBannerMask;
            ExportBannerText = s.ExportBannerText;
            ExportMarkerMask = s.ExportMarkerMask;
            ExportMarkerText = s.ExportMarkerText;
            ExportScreenComment = s.ExportScreenComment;
            ThresholdDialogNametagNormal = s.ThresholdDialogNametagNormal;
            ThresholdDialogNametagSpecial = s.ThresholdDialogNametagSpecial;
            ThresholdDialogContentNormal = s.ThresholdDialogContentNormal;
            ThresholdDialogContentSpecial = s.ThresholdDialogContentSpecial;
            ThresholdBannerNormal = s.ThresholdBannerNormal;
            ThresholdMarkerNormal = s.ThresholdMarkerNormal;
            DialogDropGraceSeconds = s.DialogDropGraceSeconds;
        }
        finally
        {
            _suppressPersist = false;
        }
        OnPropertyChanged(nameof(ProxyChangeable));
    }

    private void Persist()
    {
        if (_suppressPersist || HasValidationErrors) return;
        var s = _settings.Current;
        s.CurrentApplicationTheme = CurrentApplicationTheme;
        s.ProxyType = ProxyType;
        s.ProxyHost = ProxyHost;
        s.ProxyPort = ProxyPort;
        s.DownloadDirectory = DownloadDirectory;
        s.FfmpegPath = FfmpegPath;
        s.TypewriterFadeTime = TypewriterFadeTime;
        s.TypewriterCharTime = TypewriterCharTime;
        s.DialogFontFamily = DialogFontFamily;
        s.BannerFontFamily = BannerFontFamily;
        s.MarkerFontFamily = MarkerFontFamily;
        s.ExportLine1 = ExportLine1;
        s.ExportLine2 = ExportLine2;
        s.ExportLine3 = ExportLine3;
        s.ExportCharacter = ExportCharacter;
        s.ExportBannerMask = ExportBannerMask;
        s.ExportBannerText = ExportBannerText;
        s.ExportMarkerMask = ExportMarkerMask;
        s.ExportMarkerText = ExportMarkerText;
        s.ExportScreenComment = ExportScreenComment;
        s.ThresholdDialogNametagNormal = ThresholdDialogNametagNormal;
        s.ThresholdDialogNametagSpecial = ThresholdDialogNametagSpecial;
        s.ThresholdDialogContentNormal = ThresholdDialogContentNormal;
        s.ThresholdDialogContentSpecial = ThresholdDialogContentSpecial;
        s.ThresholdBannerNormal = ThresholdBannerNormal;
        s.ThresholdMarkerNormal = ThresholdMarkerNormal;
        s.DialogDropGraceSeconds = DialogDropGraceSeconds;
        _settings.Save();
    }

    private void SetNumericTextFromValues()
    {
        _suppressTextUpdate = true;
        try
        {
            ProxyPortText = ProxyPort.ToString();
            TypewriterFadeTimeText = TypewriterFadeTime.ToString();
            TypewriterCharTimeText = TypewriterCharTime.ToString();
        }
        finally
        {
            _suppressTextUpdate = false;
        }
    }

    private void SetNumericText(string? proxyPortText = null, string? typewriterFadeTimeText = null, string? typewriterCharTimeText = null)
    {
        _suppressTextUpdate = true;
        try
        {
            if (proxyPortText is not null)
                ProxyPortText = proxyPortText;
            if (typewriterFadeTimeText is not null)
                TypewriterFadeTimeText = typewriterFadeTimeText;
            if (typewriterCharTimeText is not null)
                TypewriterCharTimeText = typewriterCharTimeText;
        }
        finally
        {
            _suppressTextUpdate = false;
        }
    }

    private void ClearValidationErrors()
    {
        ProxyPortError = "";
        TypewriterCharTimeError = "";
        TypewriterFadeTimeError = "";
    }

    private void ClearSaveStatus()
    {
        SaveStatusMessage = "";
        SaveStatusIsError = false;
    }

    private void SetSaveStatus(string message, bool isError)
    {
        SaveStatusMessage = message;
        SaveStatusIsError = isError;
    }

    private void ValidateProxyPortText()
    {
        if (_suppressTextUpdate) return;
        ClearSaveStatus();

        if (!ProxyChangeable)
        {
            ProxyPortError = "";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProxyPortText))
        {
            ProxyPortError = "端口不能为空。";
            return;
        }

        if (!int.TryParse(ProxyPortText, out var port))
        {
            ProxyPortError = "端口必须是数字。";
            return;
        }

        if (port < 0 || port > 65535)
        {
            ProxyPortError = "端口必须在 0 到 65535 之间。";
            return;
        }

        ProxyPortError = "";
        ProxyPort = port;
    }

    private void ValidateTypewriterCharTimeText()
    {
        if (_suppressTextUpdate) return;
        ClearSaveStatus();

        if (string.IsNullOrWhiteSpace(TypewriterCharTimeText))
        {
            TypewriterCharTimeError = "单字时间不能为空。";
            return;
        }

        if (!int.TryParse(TypewriterCharTimeText, out var time))
        {
            TypewriterCharTimeError = "单字时间必须是数字。";
            return;
        }

        if (time < 0)
        {
            TypewriterCharTimeError = "单字时间不能小于 0。";
            return;
        }

        if (!int.TryParse(TypewriterFadeTimeText, out var fadeTime))
        {
            TypewriterCharTimeError = "";
            TypewriterCharTime = time;
            return;
        }

        if (time < fadeTime)
        {
            TypewriterCharTimeError = "单字时间不能小于渐变时间。";
            return;
        }

        TypewriterCharTimeError = "";
        TypewriterCharTime = time;
        if (int.TryParse(TypewriterFadeTimeText, out var currentFadeTime) && currentFadeTime <= time)
            TypewriterFadeTimeError = "";
    }

    private void ValidateTypewriterFadeTimeText()
    {
        if (_suppressTextUpdate) return;
        ClearSaveStatus();

        if (string.IsNullOrWhiteSpace(TypewriterFadeTimeText))
        {
            TypewriterFadeTimeError = "渐变时间不能为空。";
            return;
        }

        if (!int.TryParse(TypewriterFadeTimeText, out var time))
        {
            TypewriterFadeTimeError = "渐变时间必须是数字。";
            return;
        }

        if (time < 0)
        {
            TypewriterFadeTimeError = "渐变时间不能小于 0。";
            return;
        }

        if (int.TryParse(TypewriterCharTimeText, out var charTime) && time > charTime)
        {
            TypewriterFadeTimeError = "渐变时间不能大于单字时间。";
            return;
        }

        TypewriterFadeTimeError = "";
        TypewriterFadeTime = time;
        if (int.TryParse(TypewriterCharTimeText, out var currentCharTime) && currentCharTime >= time)
            TypewriterCharTimeError = "";
    }

    private void ValidateAll()
    {
        ValidateProxyPortText();
        ValidateTypewriterCharTimeText();
        ValidateTypewriterFadeTimeText();
    }

    partial void OnCurrentApplicationThemeChanged(int value)
    {
        SettingsService.ApplyTheme(value);
        Persist();
    }

    partial void OnProxyTypeChanged(int value)
    {
        OnPropertyChanged(nameof(ProxyChangeable));
        if (!ProxyChangeable)
            ProxyPortError = "";
        else
            ValidateProxyPortText();
        Persist();
    }

    partial void OnProxyHostChanged(string value) => Persist();
    partial void OnProxyPortChanged(int value)
    {
        SetNumericText(proxyPortText: value.ToString());
        ProxyPortError = "";
        Persist();
    }

    partial void OnDownloadDirectoryChanged(string value) => Persist();
    partial void OnFfmpegPathChanged(string value) => Persist();

    partial void OnTypewriterFadeTimeChanged(int value)
    {
        if (!_suppressPersist && TypewriterCharTime < value)
            TypewriterCharTime = value;
        SetNumericText(typewriterFadeTimeText: value.ToString());
        TypewriterFadeTimeError = "";
        Persist();
    }

    partial void OnTypewriterCharTimeChanged(int value)
    {
        if (!_suppressPersist && TypewriterFadeTime > value)
            TypewriterFadeTime = value;
        SetNumericText(typewriterCharTimeText: value.ToString());
        TypewriterCharTimeError = "";
        Persist();
    }
    partial void OnDialogFontFamilyChanged(string value) => Persist();
    partial void OnBannerFontFamilyChanged(string value) => Persist();
    partial void OnMarkerFontFamilyChanged(string value) => Persist();
    partial void OnExportLine1Changed(bool value) => Persist();
    partial void OnExportLine2Changed(bool value) => Persist();
    partial void OnExportLine3Changed(bool value) => Persist();
    partial void OnExportCharacterChanged(bool value) => Persist();
    partial void OnExportBannerMaskChanged(bool value) => Persist();
    partial void OnExportBannerTextChanged(bool value) => Persist();
    partial void OnExportMarkerMaskChanged(bool value) => Persist();
    partial void OnExportMarkerTextChanged(bool value) => Persist();
    partial void OnExportScreenCommentChanged(bool value) => Persist();
    partial void OnThresholdDialogNametagNormalChanged(double value) => Persist();
    partial void OnThresholdDialogNametagSpecialChanged(double value) => Persist();
    partial void OnThresholdDialogContentNormalChanged(double value) => Persist();
    partial void OnThresholdDialogContentSpecialChanged(double value) => Persist();
    partial void OnThresholdBannerNormalChanged(double value) => Persist();
    partial void OnThresholdMarkerNormalChanged(double value) => Persist();
    partial void OnProxyPortErrorChanged(string value) => NotifyValidationStateChanged();
    partial void OnTypewriterCharTimeErrorChanged(string value) => NotifyValidationStateChanged();
    partial void OnTypewriterFadeTimeErrorChanged(string value) => NotifyValidationStateChanged();
    partial void OnSaveStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasSaveStatus));
        OnPropertyChanged(nameof(SaveStatusIsSuccess));
    }
    partial void OnSaveStatusIsErrorChanged(bool value) => OnPropertyChanged(nameof(SaveStatusIsSuccess));

    private void NotifyValidationStateChanged()
    {
        OnPropertyChanged(nameof(HasProxyPortError));
        OnPropertyChanged(nameof(HasTypewriterCharTimeError));
        OnPropertyChanged(nameof(HasTypewriterFadeTimeError));
        OnPropertyChanged(nameof(HasValidationErrors));
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        _settings.ResetToDefault();
        LoadFromService();
        SettingsService.ApplyTheme(CurrentApplicationTheme);
    }

    [RelayCommand]
    private void Save()
    {
        ValidateAll();
        if (HasValidationErrors)
        {
            SetSaveStatus("请先修正设置项中的错误。", true);
            return;
        }

        Persist();
        SetSaveStatus("设置已保存。", false);
    }

    [RelayCommand]
    private void IncrementProxyPort()
    {
        ProxyPort = Math.Min(65535, ProxyPort + 1);
        ClearSaveStatus();
    }

    [RelayCommand]
    private void DecrementProxyPort()
    {
        ProxyPort = Math.Max(0, ProxyPort - 1);
        ClearSaveStatus();
    }

    [RelayCommand]
    private void IncrementTypewriterCharTime()
    {
        TypewriterCharTime += 1;
        ClearSaveStatus();
    }

    [RelayCommand]
    private void DecrementTypewriterCharTime()
    {
        TypewriterCharTime = Math.Max(TypewriterFadeTime, TypewriterCharTime - 1);
        ClearSaveStatus();
    }

    [RelayCommand]
    private void IncrementTypewriterFadeTime()
    {
        TypewriterFadeTime = Math.Min(TypewriterCharTime, TypewriterFadeTime + 1);
        ClearSaveStatus();
    }

    [RelayCommand]
    private void DecrementTypewriterFadeTime()
    {
        TypewriterFadeTime = Math.Max(0, TypewriterFadeTime - 1);
        ClearSaveStatus();
    }
}
