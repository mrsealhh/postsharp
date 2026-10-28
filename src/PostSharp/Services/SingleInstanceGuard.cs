using System.Text;

namespace PostSharp.Services;

/// <summary>Cross-platform single-instance guard. Windows uses a named mutex; Unix uses an exclusive lock file.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;
    private readonly bool _ownsMutex;

    private SingleInstanceGuard(bool acquired) => IsAcquired = acquired;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        IsAcquired = ownsMutex;
    }

    private SingleInstanceGuard(FileStream lockFile)
    {
        _lockFile = lockFile;
        IsAcquired = true;
    }

    public bool IsAcquired { get; }

    public static SingleInstanceGuard Acquire(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (createdNew)
            {
                return new SingleInstanceGuard(mutex, ownsMutex: true);
            }

            mutex.Dispose();
            return new SingleInstanceGuard(acquired: false);
        }

        var path = Path.Combine(Path.GetTempPath(), $"{name}.lock");
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
            var pid = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            stream.SetLength(0);
            stream.Write(pid);
            stream.Flush(flushToDisk: true);
            return new SingleInstanceGuard(stream);
        }
        catch (IOException exception)
        {
            Log.Information(exception, "PostSharp 实例锁已被占用 {LockFile}", path);
            return new SingleInstanceGuard(acquired: false);
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.Warning(exception, "无法创建实例锁，继续启动 {LockFile}", path);
            return new SingleInstanceGuard(acquired: true);
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was not owned on this thread; disposing is still enough.
            }
        }

        _mutex?.Dispose();
        _lockFile?.Dispose();
    }
}
