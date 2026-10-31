using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    private ITrayIconHost? _trayIcon;
    private ITrayIconHost? _serviceTrayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private DispatcherTimer? _serviceStopTimer;
    private bool _allowExit;
    private bool _startBackgroundServiceOnExit;
    private bool _serviceShutdownRequested;
    private bool _wasMaximizedBeforeTray;

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
            Styles.Add(new PostSharp.Styles.ToolTipStyles());
            Styles.Add(new PostSharp.Styles.SelectableRowStyles());

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

        _trayIcon = CreateTrayHost(
            "PostSharp · 左键打开主页面，右键显示菜单",
            [
                new TrayMenuEntry(
                    "显示主页面",
                    () =>
                        Dispatcher.UIThread.Post(
                            () => ShowMainWindow(maximize: false),
                            DispatcherPriority.Background
                        )
                ),
                new TrayMenuEntry(
                    "最大化打开",
                    () =>
                        Dispatcher.UIThread.Post(
                            () => ShowMainWindow(maximize: true),
                            DispatcherPriority.Background
                        )
                ),
                null,
                new TrayMenuEntry(
                    "退出 PostSharp",
                    () => Dispatcher.UIThread.Post(ExitApplication, DispatcherPriority.Background)
                ),
            ]
        );
        _trayIcon.IsVisible = _viewModel.Settings.McpServiceMode != McpServiceModes.Disabled;
        Log.Information("托盘图标已创建 {Mode}", _viewModel.Settings.McpServiceMode);
    }

    private ITrayIconHost CreateTrayHost(
        string toolTipText,
        IReadOnlyList<TrayMenuEntry?> menuEntries
    )
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new WindowsTrayIconHost(toolTipText, menuEntries);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "创建原生托盘菜单失败，回退 Avalonia 托盘");
            }
        }

        return new AvaloniaTrayIconHost(this, toolTipText, menuEntries);
    }

    private static void RemoveTrayIcon(ITrayIconHost? icon)
    {
        if (icon is null)
        {
            return;
        }

        try
        {
            icon.IsVisible = false;
            icon.Dispose();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "移除托盘图标失败");
        }
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
            HideToTray(showTrayIcon: true);
            Log.Information("主窗口已隐藏，MCP 页面常驻服务继续运行");
            return;
        }

        _startBackgroundServiceOnExit = mode == McpServiceModes.Background;
        _allowExit = true;
        RemoveTrayIcon(_trayIcon);
        _trayIcon = null;
        _desktop?.Shutdown();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _serviceStopTimer?.Stop();
        RemoveTrayIcon(_trayIcon);
        _trayIcon = null;
        RemoveTrayIcon(_serviceTrayIcon);
        _serviceTrayIcon = null;
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
        Log.Information("托盘图标可见性已更新 {Mode} {Visible}", mode, _trayIcon.IsVisible);
        if (mode != McpServiceModes.Page && _mainWindow is { IsVisible: false })
        {
            ShowMainWindow(maximize: false);
        }
    }

    /// <summary>True when the sidebar has a tray icon that can bring the window back.</summary>
    public bool CanMinimizeToTray => _trayIcon is { IsVisible: true };

    /// <summary>Minimize to the tray when it is available; otherwise fall back to a normal taskbar minimize.</summary>
    public void MinimizeToTray(Window window)
    {
        if (!CanMinimizeToTray || !ReferenceEquals(window, _mainWindow))
        {
            window.WindowState = WindowState.Minimized;
            return;
        }

        HideToTray(showTrayIcon: false);
    }

    private void HideToTray(bool showTrayIcon)
    {
        if (_mainWindow is not { } window)
        {
            return;
        }

        _wasMaximizedBeforeTray = window.WindowState == WindowState.Maximized;
        window.ShowInTaskbar = false;
        window.Hide();
        if (showTrayIcon && _trayIcon is not null)
        {
            _trayIcon.IsVisible = true;
        }

        Log.Information("主窗口已隐藏到托盘");
    }

    private void ShowMainWindow(bool maximize)
    {
        if (_mainWindow is not { } window)
        {
            return;
        }

        try
        {
            var restoreMaximized =
                maximize
                || _wasMaximizedBeforeTray
                || (window.IsVisible && window.WindowState == WindowState.Maximized);
            _wasMaximizedBeforeTray = false;
            window.ShowInTaskbar = true;
            window.Show();
            window.WindowState = restoreMaximized ? WindowState.Maximized : WindowState.Normal;
            window.Activate();
            Log.Information("已从托盘显示主窗口 {Maximized}", restoreMaximized);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "从托盘显示主窗口失败");
            _viewModel?.DataNotice.Warn(
                "无法从托盘显示主窗口。请用任务栏或重新启动 PostSharp；详细信息已写入日志。"
            );
        }
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _startBackgroundServiceOnExit = false;
        RemoveTrayIcon(_trayIcon);
        _trayIcon = null;
        Log.Information("用户从托盘退出 PostSharp");
        _desktop?.Shutdown();
    }

    private void CreateServiceTrayIcon()
    {
        if (_viewModel is null)
        {
            return;
        }

        _serviceTrayIcon = CreateTrayHost(
            "PostSharp · 后台服务 · 右键显示菜单",
            [
                new TrayMenuEntry(
                    "显示主页面",
                    () => Dispatcher.UIThread.Post(LaunchClient, DispatcherPriority.Background)
                ),
                null,
                new TrayMenuEntry(
                    "退出后台服务",
                    () => Dispatcher.UIThread.Post(StopMcpService, DispatcherPriority.Background)
                ),
            ]
        );
        _serviceTrayIcon.IsVisible = true;
        Log.Information("MCP 后台服务托盘图标已创建");
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
        RemoveTrayIcon(_serviceTrayIcon);
        _serviceTrayIcon = null;
        Log.Information("用户从托盘退出 MCP 后台服务");
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
