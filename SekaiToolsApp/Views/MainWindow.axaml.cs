using Avalonia.Controls;
using SekaiToolsApp.Services;
using SekaiToolsApp.ViewModels;

namespace SekaiToolsApp.Views;

public partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;

        try
        {
            if (DataContext is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
            TranslateRecoveryService.Instance.Clear();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Shutdown] cleanup failed: {ex}");
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
