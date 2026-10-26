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
    private const string SingleInstanceMutexName = "PostSharp.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        var settings = new SettingsStore().Load();
        AppLog.Initialize(settings.LogDirectory, settings.LogLevel);
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var createdNew
        );
        if (!createdNew)
        {
            Log.Information("已有 PostSharp 实例运行，本次启动直接退出");
            AppLog.Shutdown();
            return;
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
