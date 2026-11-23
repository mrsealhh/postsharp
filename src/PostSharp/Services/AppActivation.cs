namespace PostSharp.Services;

/// <summary>
/// Lets a later launch wake the running client instead of exiting silently. The named auto-reset
/// event keeps a request made during startup pending, so the window still opens once the first
/// instance finishes initializing.
/// </summary>
public sealed class AppActivation : IDisposable
{
    private const string EventName = @"Local\PostSharp.ShowMainWindow";

    private readonly EventWaitHandle _handle;
    private readonly RegisteredWaitHandle _wait;

    private AppActivation(EventWaitHandle handle, Action onActivate)
    {
        _handle = handle;
        _wait = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, _) => onActivate(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false
        );
    }

    /// <summary>Starts listening for activation requests; returns null when unsupported.</summary>
    public static AppActivation? Start(Action onActivate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var handle = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                EventName
            );
            return new AppActivation(handle, onActivate);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "无法注册单实例唤起事件");
            return null;
        }
    }

    /// <summary>Asks the running client to show its main window.</summary>
    public static bool TryActivateRunningInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                return false;
            }

            using (handle)
            {
                return handle.Set();
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "无法唤起已运行的 PostSharp 实例");
            return false;
        }
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _handle.Dispose();
    }
}
