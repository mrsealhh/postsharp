using System.Text;
using AtomUI;
using AtomUI.Desktop.Controls;
using Avalonia;
using PostSharp.Services;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace PostSharp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var settings = new SettingsStore().Load();
        AppLog.Initialize(settings.LogDirectory, settings.LogLevel);

        // Settings are read before logging starts, so a problem with them is written down here.
        if (SettingsStore.StartupProblem is { } problem)
        {
            Log.Warning(
                "启动时设置文件读取异常 {SettingsFile} {State} {SetAsideFile}",
                AppPaths.SettingsFile,
                problem.State,
                problem.SetAsidePath
            );
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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithArcFonts()
            .UseAtomUI(builder =>
            {
                builder.UseDesktopControls();
                builder.UseArc();
            });
}
