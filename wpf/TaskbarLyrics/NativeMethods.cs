// Win32 原生窗口与任务栏工具（移植自 win32util.py，WPF 版只需要挂靠/枚举/全屏检测，
// 渲染交给 WPF 合成器，不再需要 UpdateLayeredWindow 那套 GDI 位图逻辑）。
using System.Runtime.InteropServices;

namespace TaskbarLyrics;

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_POPUP = 0x80000000L;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8; // 显示但不激活

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static long GetWindowLongPtr(IntPtr hWnd, int nIndex) => GetWindowLongPtr64(hWnd, nIndex).ToInt64();

    public static void SetWindowLongPtr(IntPtr hWnd, int nIndex, long value) => SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // ---- DWM ----

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>启用 Mica 背景材质（Win11 22H2+）。成功返回 true，调用方应把窗口背景设为透明。</summary>
    public static bool TryEnableMica(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 22621) return false;
        var backdrop = 2; // DWMSBT_MAINWINDOW = Mica
        return DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref backdrop, sizeof(int)) == 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // ---- WinEvent 钩子（前台切换即时重摆窗口）----

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    // ---- 窗口挂靠 ----

    /// <summary>把窗口挂为 parent 的子窗口（保留扩展样式，对应 Python make_child_of）。
    /// 注意保留 WS_VISIBLE 原状：可见性由 WPF Visibility 管理，
    /// 强制补 WS_VISIBLE 会把 WPF 隐藏的窗口变成有框无内容的“幽灵窗口”。</summary>
    public static void MakeChildOf(IntPtr hwnd, IntPtr parent)
    {
        if (GetParent(hwnd) != parent)
            SetParent(hwnd, parent);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        var newStyle = (style & ~WS_POPUP) | WS_CHILD;
        if (newStyle != style) // 样式没变就不动，避免无谓的框架重算闪烁
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, newStyle);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    /// <summary>恢复为独立弹出窗口（浮动模式用，对应 Python make_popup）。</summary>
    public static void MakePopup(IntPtr hwnd, bool topmost)
    {
        if (GetParent(hwnd) != IntPtr.Zero)
            SetParent(hwnd, IntPtr.Zero);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        var newStyle = (style & ~WS_CHILD) | WS_POPUP;
        if (newStyle != style)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, newStyle);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        long exstyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var newExstyle = topmost ? exstyle | WS_EX_TOPMOST : exstyle & ~WS_EX_TOPMOST;
        if (newExstyle != exstyle)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, newExstyle);
    }

    /// <summary>切换整窗鼠标穿透（锁定模式）。</summary>
    public static void SetClickThrough(IntPtr hwnd, bool through)
    {
        long exstyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        exstyle = through ? exstyle | WS_EX_TRANSPARENT : exstyle & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, exstyle);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    // ---- 显示器 / 任务栏 ----

    public sealed class MonitorInfo
    {
        public IntPtr Handle;
        public RECT Rect;
        public bool Primary;
    }

    /// <summary>枚举所有显示器，主屏排第一，保证序号稳定。</summary>
    public static List<MonitorInfo> Monitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            var info = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            GetMonitorInfoW(hmon, ref info);
            result.Add(new MonitorInfo
            {
                Handle = hmon,
                Rect = info.rcMonitor,
                Primary = (info.dwFlags & 1) != 0, // MONITORINFOF_PRIMARY
            });
            return true;
        }, IntPtr.Zero);
        result.Sort((a, b) =>
        {
            int c = a.Primary == b.Primary ? 0 : (a.Primary ? -1 : 1);
            if (c != 0) return c;
            c = a.Rect.Left.CompareTo(b.Rect.Left);
            return c != 0 ? c : a.Rect.Top.CompareTo(b.Rect.Top);
        });
        return result;
    }

    public sealed class TaskbarInfo
    {
        public IntPtr Hwnd;
        public IntPtr Monitor;
        public bool Primary;
    }

    /// <summary>枚举所有任务栏窗口（主屏 Shell_TrayWnd + 副屏 Shell_SecondaryTrayWnd）。</summary>
    public static List<TaskbarInfo> Taskbars()
    {
        var bars = new List<TaskbarInfo>();
        foreach (var (cls, primary) in new[] { ("Shell_TrayWnd", true), ("Shell_SecondaryTrayWnd", false) })
        {
            IntPtr hwnd = IntPtr.Zero;
            while (true)
            {
                hwnd = FindWindowExW(IntPtr.Zero, hwnd, cls, null);
                if (hwnd == IntPtr.Zero) break;
                bars.Add(new TaskbarInfo
                {
                    Hwnd = hwnd,
                    Monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST),
                    Primary = primary,
                });
            }
        }
        return bars;
    }

    /// <summary>按显示器序号找任务栏，返回 (任务栏句柄, 托盘通知区句柄)；找不到回退主任务栏。</summary>
    public static (IntPtr Tray, IntPtr Notify) ResolveTaskbar(int monitorIndex)
    {
        var mons = Monitors();
        var bars = Taskbars();
        if (bars.Count == 0) return (IntPtr.Zero, IntPtr.Zero);
        TaskbarInfo? target = null;
        if (monitorIndex >= 0 && monitorIndex < mons.Count)
            target = bars.FirstOrDefault(b => b.Monitor == mons[monitorIndex].Handle);
        target ??= bars.FirstOrDefault(b => b.Primary) ?? bars[0];
        var notify = FindWindowExW(target.Hwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        return (target.Hwnd, notify);
    }

    // 全屏检测时要排除的窗口类（桌面壳、任务栏、本程序）
    private static readonly HashSet<string> FsIgnoreClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "TaskbarLyricsWnd", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>前台窗口是否覆盖整个屏幕（sameMonitorAs 非零时仅同屏才算）。</summary>
    public static bool IsFullscreenForeground(IntPtr ignoreHwnd, IntPtr sameMonitorAs)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == ignoreHwnd) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassNameW(hwnd, sb, 64);
        if (FsIgnoreClasses.Contains(sb.ToString())) return false;
        if (!GetWindowRect(hwnd, out var rc)) return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (sameMonitorAs != IntPtr.Zero
            && MonitorFromWindow(sameMonitorAs, MONITOR_DEFAULTTONEAREST) != monitor)
            return false;
        var info = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
        GetMonitorInfoW(monitor, ref info);
        var m = info.rcMonitor;
        return rc.Left <= m.Left && rc.Top <= m.Top && rc.Right >= m.Right && rc.Bottom >= m.Bottom;
    }
}
