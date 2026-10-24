using Serilog.Core;

namespace PostSharp.Services;

/// <summary>
/// Owns the Serilog file sink. Logs roll daily under <c>%APPDATA%\PostSharp\logs</c> by default; changing the
/// directory in the settings window reopens the sink without restarting the application.
/// </summary>
public static class AppLog
{
    private const long FileSizeLimitBytes = 50 * 1024 * 1024;
    private const int RetainedFileCount = 30;

    private static readonly object Gate = new();

    private static string _directory = AppPaths.DefaultLogDirectory;
    private static bool _started;

    public static void Initialize(string? directory)
    {
        lock (Gate)
        {
            if (_started)
            {
                Reopen(directory);
                return;
            }

            _directory = PrepareDirectory(directory);
            Log.Logger = CreateLogger(_directory);
            _started = true;
            Log.Information(
                "PostSharp 启动，日志目录 {LogDirectory}，日志按天滚动，最多保留 {RetainedFileCount} 个文件",
                _directory,
                RetainedFileCount
            );
        }
    }

    public static void Reconfigure(string? directory)
    {
        lock (Gate)
        {
            if (!_started)
            {
                _directory = PrepareDirectory(directory);
                Log.Logger = CreateLogger(_directory);
                _started = true;
                return;
            }

            Reopen(directory);
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_started)
            {
                return;
            }

            Log.Information("PostSharp 退出，正在刷新日志");
            Log.CloseAndFlush();
            _started = false;
        }
    }

    private static void Reopen(string? directory)
    {
        var resolved = PrepareDirectory(directory);
        if (string.Equals(resolved, _directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Log.Information(
            "日志目录从 {PreviousDirectory} 切换到 {LogDirectory}",
            _directory,
            resolved
        );
        Log.CloseAndFlush();
        _directory = resolved;
        Log.Logger = CreateLogger(_directory);
        Log.Information("新的日志写入 {LogDirectory}", _directory);
    }

    private static Logger CreateLogger(string directory)
    {
        var configuration = new LoggerConfiguration().MinimumLevel.Debug().Enrich.FromLogContext();

        if (!string.IsNullOrEmpty(directory))
        {
            configuration = configuration.WriteTo.File(
                Path.Combine(directory, "postsharp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            );
        }

        return configuration.CreateLogger();
    }

    private static string PrepareDirectory(string? directory)
    {
        var desired = string.IsNullOrWhiteSpace(directory)
            ? AppPaths.DefaultLogDirectory
            : directory.Trim();

        if (TryPrepareDirectory(desired, out var full))
        {
            return full;
        }

        var fallback = Path.Combine(Path.GetTempPath(), "PostSharp", "logs");
        if (TryPrepareDirectory(fallback, out var temporary))
        {
            Serilog.Debugging.SelfLog.WriteLine(
                "日志目录 {LogDirectory} 不可用，改用临时目录 {TemporaryDirectory}",
                desired,
                temporary
            );
            return temporary;
        }

        Serilog.Debugging.SelfLog.WriteLine("没有可写的日志目录，本次会话仅输出到内存");
        return string.Empty;
    }

    private static bool TryPrepareDirectory(string directory, out string full)
    {
        try
        {
            full = Path.GetFullPath(directory);
            System.IO.Directory.CreateDirectory(full);
            return true;
        }
        catch (Exception exception)
        {
            full = string.Empty;
            Serilog.Debugging.SelfLog.WriteLine(
                "日志目录 {LogDirectory} 不可用：{Message}",
                directory,
                exception.Message
            );
            return false;
        }
    }
}
