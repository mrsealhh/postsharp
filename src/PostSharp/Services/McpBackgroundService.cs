using System.Diagnostics;

namespace PostSharp.Services;

/// <summary>
/// Controls the optional headless MCP process. The service uses the same executable with
/// <c>--mcp-service</c>, keeps a pid file for single-instance checks, and watches a stop file so
/// the desktop client can take over the workspace cleanly.
/// </summary>
public static class McpBackgroundService
{
    private const string ServiceArgument = "--mcp-service";
    private const string PidFileName = ".mcp-service.pid";
    private const string StopFileName = ".mcp-service.stop";

    public static string Argument => ServiceArgument;

    public static string StopFile => Path.Combine(AppPaths.Root, StopFileName);

    private static string PidFile => Path.Combine(AppPaths.Root, PidFileName);

    public static bool IsRunning()
    {
        try
        {
            if (!File.Exists(PidFile))
            {
                return false;
            }

            var text = File.ReadAllText(PidFile).Trim();
            if (!int.TryParse(text, out var pid))
            {
                return false;
            }

            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            // The process has already exited; clean up the stale marker below.
        }
        catch (InvalidOperationException)
        {
            // The process disappeared between reading and querying it.
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "读取 MCP 后台服务状态失败");
        }

        Cleanup();
        return false;
    }

    /// <summary>Starts the headless service if one is not already running.</summary>
    public static bool Start()
    {
        CleanupStopFile();
        if (IsRunning())
        {
            return true;
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                Log.Warning("无法确定 MCP 后台服务可执行文件");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = ServiceArgument,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["POSTSHARP_DATA_DIR"] = AppPaths.Root;
            Process.Start(startInfo);
            Log.Information("已启动 MCP 后台服务 {Executable}", executable);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "启动 MCP 后台服务失败");
            return false;
        }
    }

    /// <summary>Asks a running service to stop and waits for it to release the workspace/port.</summary>
    public static bool StopAndWait(TimeSpan? timeout = null)
    {
        if (!IsRunning())
        {
            Cleanup();
            return true;
        }

        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(StopFile, "stop");
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "写入 MCP 后台服务停止标记失败");
            return false;
        }

        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning())
            {
                Cleanup();
                return true;
            }

            Thread.Sleep(100);
        }

        Log.Warning("MCP 后台服务未在 {Timeout} 内停止", limit);
        return false;
    }

    public static bool IsStopRequested() => File.Exists(StopFile);

    public static void MarkRunning()
    {
        try
        {
            AppPaths.EnsureRoot();
            CleanupStopFile();
            File.WriteAllText(PidFile, Environment.ProcessId.ToString());
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "写入 MCP 后台服务 pid 失败");
        }
    }

    public static void Cleanup()
    {
        CleanupStopFile();
        try
        {
            if (File.Exists(PidFile))
            {
                File.Delete(PidFile);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "清理 MCP 后台服务 pid 失败");
        }
    }

    private static void CleanupStopFile()
    {
        try
        {
            if (File.Exists(StopFile))
            {
                File.Delete(StopFile);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "清理 MCP 后台服务停止标记失败");
        }
    }
}
