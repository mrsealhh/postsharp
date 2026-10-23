using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using PostSharp.ViewModels;
using PostSharp.Views;

namespace PostSharp;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
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

        base.OnFrameworkInitializationCompleted();
    }
}
