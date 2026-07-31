using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SekaiToolsApp.Services;
using SekaiToolsApp.Views;

namespace SekaiToolsApp;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                HandleCrash(ex, "UnhandledException");
            else
                Console.Error.WriteLine($"[Unhandled] {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HandleCrash(e.Exception, "UnobservedTaskException");
            e.SetObserved();
        };

        SettingsService.Instance.ApplyCurrentTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void HandleCrash(Exception ex, string context)
    {
        try
        {
            var logPath = CrashLogService.WriteLog(ex, context);
            Console.Error.WriteLine($"[{context}] {ex}");
            Dispatcher.UIThread.Post(() => ShowCrashDialog(logPath));
        }
        catch
        {
            Console.Error.WriteLine($"[{context}] {ex}");
        }
    }

    private async void ShowCrashDialog(string logPath)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            return;

        var dialog = new Window
        {
            Title = "发生错误",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var stack = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = "程序遇到了一个错误，日志已保存。",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "若报错持续发生，请将日志发送给雪莹酱。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBox
        {
            Text = logPath,
            IsReadOnly = true,
            FontSize = 12,
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var okBtn = new Button { Content = "确定", Classes = { "accent" } };
        okBtn.Click += (_, _) => dialog.Close();
        btnPanel.Children.Add(okBtn);
        stack.Children.Add(btnPanel);

        dialog.Content = stack;
        await dialog.ShowDialog(owner);
    }
}
