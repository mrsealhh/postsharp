using System.Runtime;
using Avalonia.Threading;

namespace PostSharp.Services;

/// <summary>
/// 重量级窗口（授权、设置、工作流编辑器、文本查看器）关闭后，把这一轮占用的托管堆还给系统。
/// .NET 只在内存压力下才归还已提交的段，桌面工具需要主动一点，否则峰值会一直挂在任务管理器里。
/// </summary>
public static class HeapTrim
{
    private const long MinimumIntervalMilliseconds = 30_000;

    private static long _lastRequestAt;

    public static void Request()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRequestAt);
        if (now - last < MinimumIntervalMilliseconds)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastRequestAt, now, last) != last)
        {
            return;
        }

        // 关窗动画和 Dispatcher 队列清空之后再压缩，避免卡在关闭过程里。
        Dispatcher.UIThread.Post(Trim, DispatcherPriority.ApplicationIdle);
    }

    private static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Aggressive,
                blocking: true,
                compacting: true
            );
            GC.WaitForPendingFinalizers();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "压缩托管堆失败，跳过本次内存回收");
        }
    }
}
