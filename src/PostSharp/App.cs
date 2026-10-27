using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using PostSharp.Services;
using PostSharp.ViewModels;
using PostSharp.Views;

namespace PostSharp;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;

    public override async void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
        try
        {
            Styles.Add(
                new StyleInclude(new Uri("avares://PostSharp/"))
                {
                    Source = new Uri("avares://PostSharp/Styles/ToolTipStyles.axaml"),
                }
            );

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _viewModel = new MainWindowViewModel();
                UiScaleService.SetScale(_viewModel.Settings.UiScale);
                await ThemeService.ApplyAsync(
                    _viewModel.Settings.Theme,
                    isStartup: true,
                    cornerScale: _viewModel.Settings.CornerScale
                );

                var window = new MainWindow { DataContext = _viewModel };
                UiScaleService.ApplyToAll(_viewModel.Settings.UiScale);

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
        catch (Exception exception)
        {
            Log.Fatal(exception, "界面初始化失败");
            throw;
        }
    }

    private void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常");
        e.Handled = true;
        if (_viewModel is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
            _viewModel.DataNotice.Warn(
                "上次界面操作发生异常，已恢复主界面。详细信息已写入日志；如重复出现请重启应用。"
            )
        );
    }
}
