using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using SekaiToolsApp.Views.Pages;

namespace SekaiToolsApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    public MainWindowViewModel()
    {
        MenuItems = new List<NavigationItem>
        {
            new("自动轴机", Symbol.Edit, () => new SubtitlePageView(), nameof(SubtitlePageView)),
            new("脚本翻译", Symbol.Globe, () => new TranslatePageView(), nameof(TranslatePageView)),
            new("数据下载", Symbol.Download, () => new DownloadPageView(), nameof(DownloadPageView)),
            new("视频压制", Symbol.Video, () => new SuppressPageView(), nameof(SuppressPageView)),
        };

        FooterMenuItems = new List<NavigationItem>
        {
            new("设置与关于", Symbol.Setting, () => new SettingPageView(), nameof(SettingPageView)),
        };

        SelectedItem = MenuItems[0];
    }

    public IReadOnlyList<NavigationItem> MenuItems { get; }

    public IReadOnlyList<NavigationItem> FooterMenuItems { get; }

    [ObservableProperty]
    private NavigationItem? _selectedItem;

    [ObservableProperty]
    private UserControl? _currentPage;

    partial void OnSelectedItemChanged(NavigationItem? value)
    {
        try
        {
            CurrentPage = value?.GetContent();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Navigation] failed to create page {value?.Tag ?? "<null>"}: {ex}");
            CurrentPage = new PlaceholderPage(
                value?.Icon ?? Symbol.Globe,
                value?.Title ?? "页面加载失败",
                "页面在初始化时发生异常。",
                ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in MenuItems.Concat(FooterMenuItems))
        {
            switch (item.CreatedContent)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}
