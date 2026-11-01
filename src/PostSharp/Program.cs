using System.Text;
using Avalonia;
using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using TKCode.Arc.Themes.Corners;

namespace PostSharp;

internal static class Program
{
    private const string SingleInstanceMutexName = "PostSharp.SingleInstance";
    private const string ServiceInstanceMutexName = "PostSharp.McpService";
    private static string _renderingMode = RenderingModes.Software;

    [STAThread]
    public static void Main(string[] args)
    {
        var settings = new SettingsStore().Load();
        _renderingMode = settings.RenderingMode;
        AppLog.Initialize(settings.LogDirectory, settings.LogLevel);
        // Settings are read before logging starts, so a problem with them is written down here.
        if (SettingsStore.StartupProblem is { } problem)
        {
            Log.Warning(
                "启动时设置读取异常 {StorageFile} {State} {SetAsideFile}",
                AppPaths.DatabaseFile,
                problem.State,
                problem.SetAsidePath
            );
        }

        var isHeadless = args.Any(argument =>
            string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase)
        );
        var isMcpService =
            isHeadless
            || args.Any(argument =>
                string.Equals(
                    argument,
                    McpBackgroundService.Argument,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        App.IsMcpService = isMcpService;
        App.IsHeadless = isHeadless;

        using var singleInstance = SingleInstanceGuard.Acquire(
            isMcpService ? ServiceInstanceMutexName : SingleInstanceMutexName
        );
        if (!singleInstance.IsAcquired)
        {
            Log.Information("已有 PostSharp 实例运行，本次启动直接退出");
            AppLog.Shutdown();
            return;
        }

        if (isMcpService)
        {
            McpBackgroundService.MarkRunning();
        }
        else if (!McpBackgroundService.StopAndWait())
        {
            Log.Warning("启动桌面端前无法停止旧的 MCP 后台服务，继续启动");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Log.Fatal(
                eventArgs.ExceptionObject as Exception,
                "应用域未处理异常，进程即将结束 {IsTerminating}",
                eventArgs.IsTerminating
            );
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "未观察到的后台任务异常");
            eventArgs.SetObserved();
        };

        try
        {
            // Chinese APIs still answer in GBK/GB18030 often enough that the code page encodings must be available.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "应用启动或主循环失败");
            throw;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (
            OperatingSystem.IsWindows()
            && !string.Equals(_renderingMode, RenderingModes.Auto, StringComparison.Ordinal)
        )
        {
            builder = builder.With(
                new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }
            );
        }

        return builder
            .WithInterFont()
            .WithArcFonts()
            .UseArc(arc =>
                arc.UseArcControls()
                    .UseArcWorkflow()
                    .UseArcCorners()
                    .WithTheme(theme => theme.Appearance(ArcAppearance.System))
            );
    }
}
