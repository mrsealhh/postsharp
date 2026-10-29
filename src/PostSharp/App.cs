using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Threading;
using PostSharp.Models;
using PostSharp.Services;
using PostSharp.ViewModels;
using PostSharp.Views;

namespace PostSharp;

public partial class App : Application
{
    public static bool IsMcpService { get; set; }

    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private TrayIcon? _serviceTrayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private DispatcherTimer? _serviceStopTimer;
    private bool _allowExit;
    private bool _startBackgroundServiceOnExit;
    private bool _serviceShutdownRequested;

    public override async void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
        if (IsMcpService)
        {
            await InitializeMcpServiceAsync();
            base.OnFrameworkInitializationCompleted();
            return;
        }

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
                desktop.Exit += OnDesktopExit;
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
        show.Click += (_, _) => ShowMainWindow(maximize: false);
        var maximize = new NativeMenuItem("最大化打开");
        maximize.Click += (_, _) => ShowMainWindow(maximize: true);
        var exit = new NativeMenuItem("退出 PostSharp");
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(show);
        menu.Items.Add(maximize);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(
                AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png"))
            ),
            ToolTipText = "PostSharp · MCP 后台常驻",
            Menu = menu,
            IsVisible = _viewModel.Settings.McpServiceMode != McpServiceModes.Disabled,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow(maximize: false);
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowExit || e.CloseReason == WindowCloseReason.OSShutdown)
        {
            return;
        }

        var mode = _viewModel?.Settings.McpServiceMode ?? McpServiceModes.Disabled;
        if (mode == McpServiceModes.Page)
        {
            e.Cancel = true;
            _mainWindow?.Hide();
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = true;
            }

            Log.Information("主窗口已隐藏，MCP 页面常驻服务继续运行");
            return;
        }

        _startBackgroundServiceOnExit = mode == McpServiceModes.Background;
        _allowExit = true;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
        }

        _desktop?.Shutdown();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _serviceStopTimer?.Stop();
        if (IsMcpService)
        {
            if (!_serviceShutdownRequested)
            {
                _viewModel?.Shutdown();
            }

            McpBackgroundService.Cleanup();
        }
        else if (_startBackgroundServiceOnExit)
        {
            McpBackgroundService.Start();
        }
    }

    private async Task InitializeMcpServiceAsync()
    {
        _desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (_desktop is null)
        {
            Log.Warning("MCP 后台服务没有桌面生命周期，直接退出");
            return;
        }

        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _desktop.Exit += OnDesktopExit;
        // The desktop process may still be releasing port 5199 and SQLite when it starts us.
        await Task.Delay(800);
        _viewModel = new MainWindowViewModel();
        McpBackgroundService.MarkRunning();
        CreateServiceTrayIcon();
        _serviceStopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _serviceStopTimer.Tick += (_, _) =>
        {
            if (!McpBackgroundService.IsStopRequested())
            {
                return;
            }

            _serviceStopTimer?.Stop();
            _serviceShutdownRequested = true;
            _allowExit = true;
            Log.Information("收到停止标记，MCP 后台服务退出");
            _viewModel?.Shutdown();
            _desktop?.Shutdown();
        };
        _serviceStopTimer.Start();
        Log.Information("MCP 后台服务已启动 {DataRoot}", AppPaths.Root);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Models.AppSettings.McpServiceMode) || _trayIcon is null)
        {
            return;
        }

        var mode = _viewModel?.Settings.McpServiceMode ?? McpServiceModes.Disabled;
        _trayIcon.IsVisible = mode != McpServiceModes.Disabled;
        if (mode != McpServiceModes.Page && _mainWindow?.IsVisible == false)
        {
            ShowMainWindow(maximize: false);
        }
    }

    private void ShowMainWindow(bool maximize)
    {
        if (_mainWindow is not { } window)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            window.Show();
            window.WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
            window.Activate();
        });
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _startBackgroundServiceOnExit = false;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
        }

        _desktop?.Shutdown();
    }

    private void CreateServiceTrayIcon()
    {
        if (_viewModel is null)
        {
            return;
        }

        var menu = new NativeMenu();
        var open = new NativeMenuItem("打开 PostSharp 客户端");
        open.Click += (_, _) => LaunchClient();
        var exit = new NativeMenuItem("退出 MCP 后台服务");
        exit.Click += (_, _) => StopMcpService();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _serviceTrayIcon = new TrayIcon
        {
            Icon = new WindowIcon(
                AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png"))
            ),
            ToolTipText = "PostSharp · MCP 后台独立服务",
            Menu = menu,
            IsVisible = true,
        };
        TrayIcon.SetIcons(this, new TrayIcons { _serviceTrayIcon });
    }

    private void LaunchClient()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = true,
            };
            startInfo.Environment["POSTSHARP_DATA_DIR"] = AppPaths.Root;
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "从 MCP 后台服务打开客户端失败");
        }
    }

    private void StopMcpService()
    {
        _serviceShutdownRequested = true;
        _allowExit = true;
        if (_serviceTrayIcon is not null)
        {
            _serviceTrayIcon.IsVisible = false;
        }

        _viewModel?.Shutdown();
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
