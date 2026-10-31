using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PostSharp.Services;

/// <summary>
/// A Windows tray icon with a real Win32 popup menu. Avalonia's managed TrayPopupRoot is a separate
/// window; when the icon lives in the Windows 11 overflow flyout it can be dismissed before it is
/// visible. TrackPopupMenu is synchronous and works from the overflow, so the menu always appears.
/// </summary>
internal sealed class WindowsTrayIconHost : ITrayIconHost
{
    private const int TrayMessage = 0x8000 + 1;
    private const int WmNull = 0x0000;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int MfString = 0x00000000;
    private const int MfSeparator = 0x00000800;
    private const int TpmReturnCmd = 0x0100;
    private const int TpmRightButton = 0x0002;
    private const int IdiApplication = 32512;

    private readonly IReadOnlyList<TrayMenuEntry?> _menuEntries;
    private readonly string _toolTipText;
    private readonly WndProc _wndProc;
    private readonly IntPtr _instance;
    private readonly string _className;
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly int _iconId = 1;
    private bool _added;
    private bool _visible;
    private bool _disposed;

    public WindowsTrayIconHost(string toolTipText, IReadOnlyList<TrayMenuEntry?> menuEntries)
    {
        _toolTipText = toolTipText;
        _menuEntries = menuEntries;
        _instance = GetModuleHandle(null);
        _className = "PostSharpTray_" + Guid.NewGuid().ToString("N");
        _wndProc = WindowProc;

        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = _className,
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册托盘消息窗口。");
        }

        _window = CreateWindowEx(
            0,
            _className,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            _instance,
            IntPtr.Zero
        );
        if (_window == IntPtr.Zero)
        {
            UnregisterClass(_className, _instance);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘消息窗口。");
        }

        (_icon, _ownsIcon) = LoadIcon();
        if (_icon == IntPtr.Zero)
        {
            DestroyWindow(_window);
            UnregisterClass(_className, _instance);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载托盘图标。");
        }
    }

    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            ApplyIcon();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
        }

        if (!string.IsNullOrEmpty(_className))
        {
            UnregisterClass(_className, _instance);
        }

        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _visible = false;
    }

    private void ApplyIcon()
    {
        var data = CreateData();
        if (_visible)
        {
            var message = _added ? NimModify : NimAdd;
            if (!Shell_NotifyIcon(message, ref data))
            {
                Log.Warning("托盘图标添加失败 {Error}", Marshal.GetLastWin32Error());
                _visible = false;
                return;
            }

            _added = true;
            LogTrayBounds();
            return;
        }

        if (_added)
        {
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
    }

    private NotifyIconData CreateData() =>
        new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = _iconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayMessage,
            hIcon = _icon,
            szTip = _toolTipText,
        };

    private void LogTrayBounds()
    {
        var identifier = new NotifyIconIdentifier
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            hWnd = _window,
            uID = (uint)_iconId,
        };
        if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0)
        {
            Log.Debug(
                "托盘图标位置 {Left},{Top},{Right},{Bottom}",
                rect.Left,
                rect.Top,
                rect.Right,
                rect.Bottom
            );
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayMessage)
        {
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage is WmLButtonUp or WmLButtonDoubleClick)
            {
                _menuEntries.FirstOrDefault()?.Action();
                return IntPtr.Zero;
            }

            if (mouseMessage == WmRButtonUp)
            {
                ShowMenu();
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var index = 0; index < _menuEntries.Count; index++)
            {
                var entry = _menuEntries[index];
                AppendMenu(menu, entry is null ? MfSeparator : MfString, index + 1, entry?.Header);
            }

            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var command = TrackPopupMenuEx(
                menu,
                TpmReturnCmd | TpmRightButton,
                point.X,
                point.Y,
                _window,
                IntPtr.Zero
            );
            PostMessage(_window, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (
                command > 0
                && command <= _menuEntries.Count
                && _menuEntries[command - 1] is { } selected
            )
            {
                selected.Action();
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "显示托盘菜单失败");
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static (IntPtr Icon, bool OwnsIcon) LoadIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            if (
                ExtractIconEx(executable, 0, out var large, out var small, 1) > 0
                && (small != IntPtr.Zero || large != IntPtr.Zero)
            )
            {
                var icon = small != IntPtr.Zero ? small : large;
                if (small != IntPtr.Zero && large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                return (icon, true);
            }
        }

        return (LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication)), false);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(
        ref NotifyIconIdentifier identifier,
        out Rect iconLocation
    );

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter
    );

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(
        string file,
        int index,
        out IntPtr largeIcon,
        out IntPtr smallIcon,
        uint iconCount
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        IntPtr menu,
        int flags,
        int x,
        int y,
        IntPtr hWnd,
        IntPtr parameters
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
