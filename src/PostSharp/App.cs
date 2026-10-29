using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Threading;
using PostSharp.Services;
using PostSharp.ViewModels;
using PostSharp.Views;

namespace PostSharp;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _allowExit;

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
            Styles.Add(
                new StyleInclude(new Uri("avares://PostSharp/"))
                {
                    Source = new Uri("avares://PostSharp/Styles/SelectableRowStyles.axaml"),
                }
            );

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _desktop = desktop;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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

                _mainWindow = window;
                window.Closing += OnMainWindowClosing;
                CreateTrayIcon();
                _viewModel.Settings.PropertyChanged += OnSettingsChanged;
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

    private void CreateTrayIcon()
    {
        if (_viewModel is null)
        {
            return;
        }

        var menu = new NativeMenu();
        var show = new NativeMenuItem("打开 PostSharp");
        show.Click += (_, _) => ShowMainWindow();
        var exit = new NativeMenuItem("退出 PostSharp");
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(
                AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png"))
            ),
            ToolTipText = "PostSharp · MCP 后台常驻",
            Menu = menu,
            IsVisible = _viewModel.Settings.KeepMcpResident,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowExit || e.CloseReason == WindowCloseReason.OSShutdown)
        {
            return;
        }

        if (_viewModel?.Settings.KeepMcpResident == true)
        {
            e.Cancel = true;
            _mainWindow?.Hide();
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = true;
            }

            Log.Information("主窗口已隐藏，MCP 后台服务继续运行");
            return;
        }

        _allowExit = true;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
        }

        _desktop?.Shutdown();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Models.AppSettings.KeepMcpResident) || _trayIcon is null)
        {
            return;
        }

        _trayIcon.IsVisible = _viewModel?.Settings.KeepMcpResident == true;
        if (_viewModel?.Settings.KeepMcpResident == false && _mainWindow?.IsVisible == false)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is not { } window)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        });
    }

    private void ExitApplication()
    {
        _allowExit = true;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
        }

        _desktop?.Shutdown();
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
