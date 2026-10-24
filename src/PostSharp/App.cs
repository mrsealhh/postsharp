using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using PostSharp.Services;
using PostSharp.ViewModels;
using PostSharp.Views;

namespace PostSharp;

public partial class App : Application
{
    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            await ThemeService.ApplyAsync(viewModel.Settings.Theme, isStartup: true);

            var window = new MainWindow { DataContext = viewModel };

            // Leave a margin around the window on small or scaled screens instead of overflowing the desktop.
            if (window.Screens.Primary is { } screen)
            {
                var area = screen.WorkingArea;
                window.Width = Math.Clamp(area.Width - 80, window.MinWidth, 1400);
                window.Height = Math.Clamp(area.Height - 80, window.MinHeight, 900);
            }

            desktop.MainWindow = window;
        }

        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            Log.Error(eventArgs.Exception, "UI 线程未处理异常");

        base.OnFrameworkInitializationCompleted();
    }
}
