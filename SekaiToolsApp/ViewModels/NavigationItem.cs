using System;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace SekaiToolsApp.ViewModels;

/// <summary>
/// 主窗口左侧导航的一项，绑定到 <see cref="NavigationView"/>。
/// </summary>
public sealed class NavigationItem
{
    private UserControl? _content;

    public NavigationItem(string title, Symbol icon, Func<UserControl> contentFactory, string tag)
    {
        Title = title;
        Icon = icon;
        ContentFactory = contentFactory;
        Tag = tag;
    }

    public string Title { get; }

    public Symbol Icon { get; }

    /// <summary>
    /// 选中时构造对应页面。延迟构造，避免 App 启动一次性初始化全部页面。
    /// </summary>
    public Func<UserControl> ContentFactory { get; }

    public string Tag { get; }

    internal UserControl? CreatedContent => _content;

    public UserControl GetContent()
    {
        if (_content is not null) return _content;

        var content = ContentFactory();
        _content = content;
        return _content;
    }
}
