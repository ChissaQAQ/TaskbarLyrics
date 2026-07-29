"""Win32 原生窗口与任务栏工具（ctypes 封装）。

提供：窗口类注册/创建、UpdateLayeredWindow 逐像素透明更新、
显示器/任务栏枚举、任务栏挂靠所需的样式调整。
"""
import ctypes
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
kernel32 = ctypes.windll.kernel32

# ---- 函数原型（避免默认 32 位 int 截断句柄/指针）----
user32.FindWindowW.restype = wintypes.HWND
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.FindWindowExW.restype = wintypes.HWND
user32.FindWindowExW.argtypes = [wintypes.HWND, wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.GetWindowLongPtrW.restype = ctypes.c_longlong
user32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
user32.SetWindowLongPtrW.restype = ctypes.c_longlong
user32.SetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_longlong]
user32.SetParent.restype = wintypes.HWND
user32.SetParent.argtypes = [wintypes.HWND, wintypes.HWND]
user32.GetParent.restype = wintypes.HWND
user32.GetParent.argtypes = [wintypes.HWND]
user32.MonitorFromWindow.restype = wintypes.HANDLE
user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
user32.CreateWindowExW.restype = wintypes.HWND
user32.CreateWindowExW.argtypes = [
    wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
    ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID,
]
user32.DefWindowProcW.restype = ctypes.c_long
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.UpdateLayeredWindow.restype = wintypes.BOOL
user32.UpdateLayeredWindow.argtypes = [
    wintypes.HWND, wintypes.HDC, ctypes.c_void_p, ctypes.c_void_p,
    wintypes.HDC, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD,
]
gdi32.CreateDIBSection.restype = wintypes.HBITMAP
gdi32.CreateDIBSection.argtypes = [
    wintypes.HDC, ctypes.c_void_p, wintypes.UINT,
    ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.DWORD,
]
gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = wintypes.HANDLE
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HANDLE]
gdi32.DeleteObject.restype = wintypes.BOOL
gdi32.DeleteObject.argtypes = [wintypes.HANDLE]
gdi32.DeleteDC.restype = wintypes.BOOL
gdi32.DeleteDC.argtypes = [wintypes.HDC]
gdi32.GetDIBits.restype = ctypes.c_int
gdi32.GetDIBits.argtypes = [
    wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
    ctypes.c_void_p, ctypes.c_void_p, wintypes.UINT,
]
user32.GetDC.restype = wintypes.HDC
user32.GetDC.argtypes = [wintypes.HWND]
user32.GetWindowDC.restype = wintypes.HDC
user32.GetWindowDC.argtypes = [wintypes.HWND]
user32.ReleaseDC.restype = ctypes.c_int
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
user32.SetWindowPos.restype = wintypes.BOOL
user32.SetWindowPos.argtypes = [
    wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
    ctypes.c_int, ctypes.c_int, wintypes.UINT,
]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.DestroyWindow.restype = wintypes.BOOL
user32.DestroyWindow.argtypes = [wintypes.HWND]
user32.PrintWindow.restype = wintypes.BOOL
user32.PrintWindow.argtypes = [wintypes.HWND, wintypes.HDC, wintypes.UINT]
user32.EnumWindows.restype = wintypes.BOOL
user32.EnumWindows.argtypes = [ctypes.c_void_p, wintypes.LPARAM]
user32.EnumChildWindows.restype = wintypes.BOOL
user32.EnumChildWindows.argtypes = [wintypes.HWND, ctypes.c_void_p, wintypes.LPARAM]
user32.GetClassNameW.restype = ctypes.c_int
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowRect.restype = wintypes.BOOL
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetClientRect.restype = wintypes.BOOL
user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.ScreenToClient.restype = wintypes.BOOL
user32.ScreenToClient.argtypes = [wintypes.HWND, ctypes.c_void_p]
user32.SetCapture.restype = wintypes.HWND
user32.SetCapture.argtypes = [wintypes.HWND]
user32.ReleaseCapture.restype = wintypes.BOOL
user32.GetCursorPos.restype = wintypes.BOOL
user32.GetCursorPos.argtypes = [ctypes.c_void_p]
user32.RegisterClassW.restype = wintypes.ATOM
user32.RegisterClassW.argtypes = [ctypes.c_void_p]
user32.LoadCursorW.restype = wintypes.HANDLE
user32.LoadCursorW.argtypes = [wintypes.HINSTANCE, wintypes.LPCWSTR]
user32.EnumDisplayMonitors.restype = wintypes.BOOL
user32.EnumDisplayMonitors.argtypes = [wintypes.HDC, ctypes.c_void_p, ctypes.c_void_p, wintypes.LPARAM]
user32.GetMonitorInfoW.restype = wintypes.BOOL
user32.GetMonitorInfoW.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetForegroundWindow.argtypes = []
user32.ShowWindow.restype = wintypes.BOOL
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]

# ---- 常量 ----
GWL_STYLE = -16
GWL_EXSTYLE = -20
WS_POPUP = 0x80000000
WS_CHILD = 0x40000000
WS_VISIBLE = 0x10000000
WS_EX_LAYERED = 0x00080000
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_NOACTIVATE = 0x08000000
WS_EX_TOPMOST = 0x00000008
WS_EX_TRANSPARENT = 0x00000020
SWP_NOACTIVATE = 0x0010
SWP_NOZORDER = 0x0004
SWP_FRAMECHANGED = 0x0020
SWP_SHOWWINDOW = 0x0040
MONITOR_DEFAULTTONEAREST = 2
ULW_ALPHA = 2

WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_long, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)


class WNDCLASSW(ctypes.Structure):
    _fields_ = [
        ("style", wintypes.UINT),
        ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", wintypes.HINSTANCE),
        ("hIcon", wintypes.HICON),
        ("hCursor", wintypes.HANDLE),
        ("hbrBackground", wintypes.HBRUSH),
        ("lpszMenuName", wintypes.LPCWSTR),
        ("lpszClassName", wintypes.LPCWSTR),
    ]


class SIZE(ctypes.Structure):
    _fields_ = [("cx", ctypes.c_long), ("cy", ctypes.c_long)]


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class BLENDFUNCTION(ctypes.Structure):
    _fields_ = [
        ("BlendOp", ctypes.c_byte),
        ("BlendFlags", ctypes.c_byte),
        ("SourceConstantAlpha", ctypes.c_byte),
        ("AlphaFormat", ctypes.c_byte),
    ]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


class MONITORINFOEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT),
        ("dwFlags", wintypes.DWORD),
        ("szDevice", wintypes.WCHAR * 32),
    ]


def register_class(class_name: str, wndproc) -> None:
    """注册窗口类（已注册则忽略）。"""
    wc = WNDCLASSW()
    wc.lpfnWndProc = wndproc
    wc.hInstance = kernel32.GetModuleHandleW(None)
    wc.lpszClassName = class_name
    wc.hCursor = user32.LoadCursorW(None, wintypes.LPCWSTR(32512))  # IDC_ARROW
    if not user32.RegisterClassW(ctypes.byref(wc)):
        ERROR_CLASS_ALREADY_EXISTS = 1410
        if kernel32.GetLastError() != ERROR_CLASS_ALREADY_EXISTS:
            raise ctypes.WinError()


def create_window(class_name: str, width: int, height: int) -> int:
    """创建分层层叠窗口（初始为弹出式，之后可挂进任务栏变成子窗口）。"""
    hwnd = user32.CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        class_name, "", WS_POPUP,
        0, 0, width, height,
        None, None, kernel32.GetModuleHandleW(None), None,
    )
    if not hwnd:
        raise ctypes.WinError()
    return hwnd


def update_layered(hwnd: int, width: int, height: int, bgra_premultiplied: bytes) -> None:
    """用预乘 alpha 的 BGRA 位图更新分层窗口内容。"""
    hdc_screen = user32.GetDC(None)
    memdc = gdi32.CreateCompatibleDC(hdc_screen)
    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = width
    bmi.bmiHeader.biHeight = -height  # 负数：从上到下
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bits = ctypes.c_void_p()
    bmp = gdi32.CreateDIBSection(hdc_screen, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    ctypes.memmove(bits, bgra_premultiplied, len(bgra_premultiplied))
    old_bmp = gdi32.SelectObject(memdc, bmp)

    size = SIZE(width, height)
    pt_src = POINT(0, 0)
    blend = BLENDFUNCTION(0, 0, 255, 1)  # AC_SRC_OVER, AC_SRC_ALPHA
    kernel32.SetLastError(0)
    ok = user32.UpdateLayeredWindow(
        hwnd, hdc_screen, None, ctypes.byref(size), memdc, ctypes.byref(pt_src), 0,
        ctypes.byref(blend), ULW_ALPHA,
    )
    if not ok:
        err = kernel32.GetLastError()
        gdi32.SelectObject(memdc, old_bmp)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(memdc)
        user32.ReleaseDC(None, hdc_screen)
        raise ctypes.WinError(err)

    gdi32.SelectObject(memdc, old_bmp)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(memdc)
    user32.ReleaseDC(None, hdc_screen)


def make_child_of(hwnd: int, parent: int) -> None:
    """把窗口挂为 parent 的子窗口（保留 WS_EX_LAYERED 扩展样式）。"""
    if user32.GetParent(hwnd) != parent:
        user32.SetParent(hwnd, parent)
    style = user32.GetWindowLongPtrW(hwnd, GWL_STYLE) & 0xFFFFFFFF
    style = (style & (~WS_POPUP & 0xFFFFFFFF)) | WS_CHILD | WS_VISIBLE
    user32.SetWindowLongPtrW(hwnd, GWL_STYLE, style)


def make_popup(hwnd: int, topmost: bool = True) -> None:
    """恢复为独立弹出窗口（浮动模式用）。"""
    if user32.GetParent(hwnd):
        user32.SetParent(hwnd, None)
    style = user32.GetWindowLongPtrW(hwnd, GWL_STYLE) & 0xFFFFFFFF
    style = (style & (~WS_CHILD & 0xFFFFFFFF)) | WS_POPUP | WS_VISIBLE
    user32.SetWindowLongPtrW(hwnd, GWL_STYLE, style)
    exstyle = user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & 0xFFFFFFFF
    if topmost:
        exstyle |= WS_EX_TOPMOST
    else:
        exstyle &= ~WS_EX_TOPMOST & 0xFFFFFFFF
    user32.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, exstyle)


def set_click_through(hwnd: int, through: bool) -> None:
    """切换整窗鼠标穿透（锁定模式）。"""
    exstyle = user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & 0xFFFFFFFF
    if through:
        exstyle |= WS_EX_TRANSPARENT
    else:
        exstyle &= ~WS_EX_TRANSPARENT & 0xFFFFFFFF
    user32.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, exstyle)
    SWP_NOMOVE = 0x0002
    SWP_NOSIZE = 0x0001
    user32.SetWindowPos(
        hwnd, None, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED,
    )


# ---- 显示器 / 任务栏 ----

def monitors() -> list[dict]:
    """枚举所有显示器，主屏排第一，保证序号稳定。"""
    result = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HANDLE, wintypes.HDC, ctypes.POINTER(wintypes.RECT), wintypes.LPARAM)
    def cb(hmon, hdc, lprect, lp):
        info = MONITORINFOEXW()
        info.cbSize = ctypes.sizeof(MONITORINFOEXW)
        user32.GetMonitorInfoW(hmon, ctypes.byref(info))
        rc = info.rcMonitor
        result.append({
            "handle": hmon,
            "rect": (rc.left, rc.top, rc.right, rc.bottom),
            "primary": bool(info.dwFlags & 1),  # MONITORINFOF_PRIMARY
        })
        return True

    user32.EnumDisplayMonitors(None, None, cb, 0)
    result.sort(key=lambda m: (not m["primary"], m["rect"][0], m["rect"][1]))
    return result


def taskbars() -> list[dict]:
    """枚举所有任务栏窗口（主屏 Shell_TrayWnd + 副屏 Shell_SecondaryTrayWnd）。"""
    bars = []
    for cls, primary in (("Shell_TrayWnd", True), ("Shell_SecondaryTrayWnd", False)):
        hwnd = None
        while True:
            hwnd = user32.FindWindowExW(None, hwnd, cls, None)
            if not hwnd:
                break
            monitor = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
            bars.append({"hwnd": hwnd, "monitor": monitor, "primary": primary})
    return bars


def resolve_taskbar(monitor_index: int) -> tuple[int, int]:
    """按显示器序号找任务栏，返回 (任务栏句柄, 托盘通知区句柄)；找不到回退主任务栏。"""
    mons = monitors()
    bars = taskbars()
    if not bars:
        return 0, 0
    target = None
    if 0 <= monitor_index < len(mons):
        handle = mons[monitor_index]["handle"]
        target = next((b for b in bars if b["monitor"] == handle), None)
    if target is None:  # 该屏幕没开任务栏或序号失效 → 回退主任务栏
        target = next((b for b in bars if b["primary"]), bars[0])
    notify = user32.FindWindowExW(target["hwnd"], None, "TrayNotifyWnd", None)
    return target["hwnd"], notify


# 全屏检测时要排除的窗口类（桌面壳、任务栏、本程序）
_FS_IGNORE_CLASSES = {
    "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    "TaskbarLyricsWnd", "Windows.UI.Core.CoreWindow",
}


def is_fullscreen_foreground(ignore_hwnd: int = 0, same_monitor_as: int = 0) -> bool:
    """前台窗口是否覆盖整个屏幕（用于全屏时自动隐藏歌词）。

    same_monitor_as 非 0 时，仅当全屏窗口与该窗口处于同一显示器才返回 True。
    """
    hwnd = user32.GetForegroundWindow()
    if not hwnd or hwnd == ignore_hwnd:
        return False
    buf = ctypes.create_unicode_buffer(64)
    user32.GetClassNameW(hwnd, buf, 64)
    if buf.value in _FS_IGNORE_CLASSES:
        return False
    rc = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rc)):
        return False
    monitor = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
    if same_monitor_as:
        own = user32.MonitorFromWindow(same_monitor_as, MONITOR_DEFAULTTONEAREST)
        if monitor != own:
            return False
    info = MONITORINFOEXW()
    info.cbSize = ctypes.sizeof(MONITORINFOEXW)
    user32.GetMonitorInfoW(monitor, ctypes.byref(info))
    mrc = info.rcMonitor
    return (rc.left <= mrc.left and rc.top <= mrc.top
            and rc.right >= mrc.right and rc.bottom >= mrc.bottom)
