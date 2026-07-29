"""歌词覆盖窗口：Win32 原生分层窗口（逐像素透明，PIL 渲染）。

两种模式：
- taskbar：挂进任务栏（Shell_TrayWnd/副屏 Shell_SecondaryTrayWnd）成为子窗口
- floating：独立置顶悬浮窗，可拖到屏幕任意位置
左键拖动、右键弹出菜单、锁定时整窗鼠标穿透。
悬停时播放控制按钮淡入浮现（离开时淡出），切歌/切行有滑动淡入动画。
"""
import ctypes
import time
from ctypes import wintypes

import menu as menu_mod
import render
import win32util as w

_CLASS_NAME = "TaskbarLyricsWnd"

_WM_DESTROY = 0x0002
_WM_LBUTTONDOWN = 0x0201
_WM_LBUTTONUP = 0x0202
_WM_MOUSEMOVE = 0x0200
_WM_MOUSELEAVE = 0x02A3
_WM_RBUTTONUP = 0x0205
_WM_TIMER = 0x0113
_WM_NULL = 0x0000

_MF_STRING = 0x0000
_MF_CHECKED = 0x0008
_MF_GRAYED = 0x0001
_MF_SEPARATOR = 0x0800
_MF_POPUP = 0x0010
_TPM_RETURNCMD = 0x0100
_TPM_NONOTIFY = 0x0080
_TPM_RIGHTBUTTON = 0x0002
_TME_LEAVE = 0x00000002

_SW_HIDE = 0
_SW_SHOWNA = 8

_ANIM_LINE_MS = 0.18   # 切行动画时长（秒）
_ANIM_FADE_MS = 0.12   # 按钮浮现/淡出时长（秒）

# 菜单/定时器相关原型
w.user32.CreatePopupMenu.restype = wintypes.HMENU
w.user32.CreatePopupMenu.argtypes = []
w.user32.AppendMenuW.restype = wintypes.BOOL
w.user32.AppendMenuW.argtypes = [wintypes.HMENU, wintypes.UINT, ctypes.c_ulonglong, wintypes.LPCWSTR]
w.user32.TrackPopupMenuEx.restype = ctypes.c_int
w.user32.TrackPopupMenuEx.argtypes = [
    wintypes.HMENU, wintypes.UINT, ctypes.c_int, ctypes.c_int, wintypes.HWND, ctypes.c_void_p,
]
w.user32.DestroyMenu.restype = wintypes.BOOL
w.user32.DestroyMenu.argtypes = [wintypes.HMENU]
w.user32.SetForegroundWindow.restype = wintypes.BOOL
w.user32.SetForegroundWindow.argtypes = [wintypes.HWND]
w.user32.PostMessageW.restype = wintypes.BOOL
w.user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
w.user32.SetTimer.restype = ctypes.c_ulonglong
w.user32.SetTimer.argtypes = [wintypes.HWND, ctypes.c_ulonglong, wintypes.UINT, ctypes.c_void_p]
w.user32.KillTimer.restype = wintypes.BOOL
w.user32.KillTimer.argtypes = [wintypes.HWND, ctypes.c_ulonglong]
w.user32.PostQuitMessage.argtypes = [ctypes.c_int]


class _TRACKMOUSEEVENT(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("hwndTrack", wintypes.HWND),
        ("dwHoverTime", wintypes.DWORD),
    ]


w.user32.TrackMouseEvent.restype = wintypes.BOOL
w.user32.TrackMouseEvent.argtypes = [ctypes.POINTER(_TRACKMOUSEEVENT)]

TIMER_REFRESH = 1  # 歌词刷新（16ms ≈ 60fps）
TIMER_DOCK = 2     # 周期贴合（1500ms）
WM_APP_ACTION = 0x8001  # 托盘线程投递过来的菜单动作


def set_dpi_awareness() -> None:
    """声明 Per-Monitor V2 DPI 感知：坐标用物理像素，文字不糊。"""
    try:  # Win10 1703+
        w.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except Exception:
            pass


class Overlay:
    def __init__(self, app) -> None:
        self.app = app  # 提供 cfg、on_timer(id)、on_quit() 等
        self._original = ""
        self._translation = ""
        self._karaoke = None
        self._playing = False
        self._cover = None        # PIL 封面图
        self._cover_key = ""
        self._last_sig = None     # 渲染签名，跳过无变化的重复渲染
        self._fs_hidden = False   # 因全屏应用而隐藏
        self._hovered = False     # 鼠标在窗口上
        self._hover_zone = -1     # 悬停的按钮序号
        self._hover_change = 0.0  # 悬停状态变化时刻（浮现/淡出动画起点）
        self._anim_start = 0.0    # 切行动画起点
        self._tracking = False    # TrackMouseEvent 是否已注册
        self._drag: tuple[int, int, int, int] | None = None  # (起始光标x,y, 起始窗口x,y)
        self._visible = False

        self._wndproc = w.WNDPROC(self._on_message)  # 防止被 GC
        w.register_class(_CLASS_NAME, self._wndproc)
        self.hwnd = w.create_window(_CLASS_NAME, app.cfg["width"], 48)

        w.user32.SetTimer(self.hwnd, TIMER_REFRESH, 16, None)  # ~60fps 逐字扫过
        w.user32.SetTimer(self.hwnd, TIMER_DOCK, 1500, None)
        self.dock()
        self.set_locked(app.cfg["locked"], save=False)

    # ---- 内容 ----

    def set_lyric(self, original: str, translation: str, karaoke=None, playing: bool = False) -> None:
        if original != self._original and original and self._original:
            self._anim_start = time.monotonic()  # 切行动画
        self._original = original
        self._translation = translation
        self._karaoke = karaoke
        self._playing = playing
        self._set_visible(bool(original))

    def set_cover(self, cover, cover_key: str) -> None:
        """设置专辑封面（PIL Image 或 None）。"""
        if cover_key != self._cover_key:
            self._cover = cover
            self._cover_key = cover_key
            if self._visible:
                self._render()

    def _set_visible(self, visible: bool) -> None:
        if visible != self._visible:
            self._visible = visible
            w.user32.ShowWindow(self.hwnd, _SW_SHOWNA if visible else _SW_HIDE)
            if visible:
                self.dock()

    # ---- 布局 ----

    def _total_width(self) -> int:
        """窗口总宽 = 歌词区 + （可选）封面区（宽度=高度）。按钮悬停浮现不占宽。"""
        cfg = self.app.cfg
        width = cfg["width"]
        if cfg["show_cover"]:
            width += self._current_height()
        return width

    def _current_height(self) -> int:
        cfg = self.app.cfg
        if cfg["mode"] == "taskbar":
            tray, _ = w.resolve_taskbar(cfg["monitor"])
            if tray:
                rc = wintypes.RECT()
                w.user32.GetClientRect(tray, ctypes.byref(rc))
                return max(24, rc.bottom)
            return 48
        # 浮动模式：按文字行数自适应
        h = render.pt_to_px(cfg["font_size"]) + 10
        if self._translation and cfg["second_line"] != "off":
            h += render.pt_to_px(max(7, cfg["font_size"] - 4)) + 2
        return h

    def dock(self) -> None:
        """按当前模式与配置摆放窗口（周期调用以跟随任务栏变化/重建）。"""
        cfg = self.app.cfg
        width = self._total_width()
        height = self._current_height()

        if cfg["mode"] == "taskbar":
            tray, notify = w.resolve_taskbar(cfg["monitor"])
            if not tray:
                return
            w.make_child_of(self.hwnd, tray)
            rc = wintypes.RECT()
            w.user32.GetClientRect(tray, ctypes.byref(rc))

            position = cfg["position"]
            if position == "custom" and cfg["x_offset"] is not None:
                x = cfg["x_offset"]
            elif position == "left":
                x = 8
            elif position == "center":
                x = (rc.right - width) // 2
            elif position == "right":
                x = rc.right - width - 8
            else:  # tray_left
                right_edge = rc.right
                if notify:
                    nrc = wintypes.RECT()
                    w.user32.GetWindowRect(notify, ctypes.byref(nrc))
                    pt = wintypes.POINT(nrc.left, nrc.top)
                    w.user32.ScreenToClient(tray, ctypes.byref(pt))
                    right_edge = pt.x
                x = right_edge - 12 - width
            max_x = max(0, rc.right - width)
            x = min(max(x, 0), max_x)
            w.user32.SetWindowPos(
                self.hwnd, None, x, 0, width, height,
                w.SWP_NOACTIVATE | w.SWP_NOZORDER | w.SWP_FRAMECHANGED,
            )
        else:  # floating
            w.make_popup(self.hwnd, topmost=True)
            if cfg["float_x"] is not None and cfg["float_y"] is not None:
                x, y = cfg["float_x"], cfg["float_y"]
            else:
                mons = w.monitors()
                rect = mons[cfg["monitor"]]["rect"] if 0 <= cfg["monitor"] < len(mons) else mons[0]["rect"]
                x = (rect[0] + rect[2] - width) // 2
                y = rect[3] - height - 80
            w.user32.SetWindowPos(
                self.hwnd, None, x, y, width, height,
                w.SWP_NOACTIVATE | w.SWP_FRAMECHANGED,
            )

    def apply_layout(self) -> None:
        """外观/尺寸类设置变化后调用：重新摆放并按当前歌词重绘。"""
        self.dock()
        if self._visible:
            self._render()

    # ---- 渲染 ----

    def _buttons_fade(self) -> float:
        """按钮浮现动画进度 0~1。"""
        p = min(1.0, (time.monotonic() - self._hover_change) / _ANIM_FADE_MS)
        return p if self._hovered else 1.0 - p

    def _render(self) -> None:
        cfg = self.app.cfg
        translation = self._translation if cfg["second_line"] != "off" else ""
        width = self._total_width()
        height = self._current_height()
        anim_p = min(1.0, (time.monotonic() - self._anim_start) / _ANIM_LINE_MS) if self._anim_start else 1.0
        fade = self._buttons_fade() if cfg["show_controls"] else 0.0
        data = render.render_lyric(
            self._original, translation, width, height,
            family=cfg["font_family"],
            orig_pt=cfg["font_size"],
            trans_pt=max(7, cfg["font_size"] - 4),
            color=cfg["text_color"],
            trans_color=cfg["trans_color"],
            shadow=cfg["shadow"],
            karaoke=self._karaoke,
            controls_playing=self._playing if cfg["show_controls"] else None,
            buttons_fade=fade,
            has_cover_zone=cfg["show_cover"],
            cover=self._cover if cfg["show_cover"] else None,
            cover_key=self._cover_key,
            hover_zone=self._hover_zone,
            anim_p=anim_p,
        )
        w.update_layered(self.hwnd, width, height, data)

    def maybe_render(self) -> None:
        """定时器驱动：仅在签名变化时重绘（60fps 逐字/动画）。"""
        if not self._visible:
            return
        anim_p = min(1.0, (time.monotonic() - self._anim_start) / _ANIM_LINE_MS) if self._anim_start else 1.0
        fade = self._buttons_fade()
        sig = (
            self._original, self._translation, self._playing,
            self._hover_zone,
            int(fade * 6) if 0 < fade < 1 else round(fade),
            int(anim_p * 10) if anim_p < 1 else -1,
            render.karaoke_boundary_px(self._karaoke) if self._karaoke else None,
            self._cover_key,
        )
        if sig != self._last_sig:
            self._last_sig = sig
            self._render()

    # ---- 全屏自动隐藏 ----

    def update_fullscreen(self) -> None:
        """前台同屏出现全屏窗口时隐藏歌词，退出后恢复。"""
        cfg = self.app.cfg
        hidden = (cfg["hide_on_fullscreen"]
                  and w.is_fullscreen_foreground(ignore_hwnd=self.hwnd, same_monitor_as=self.hwnd))
        if hidden != self._fs_hidden:
            self._fs_hidden = hidden
            w.user32.ShowWindow(self.hwnd, _SW_HIDE if hidden else (_SW_SHOWNA if self._visible else _SW_HIDE))

    # ---- 锁定 ----

    def set_locked(self, locked: bool, save: bool = True) -> None:
        w.set_click_through(self.hwnd, locked)
        if save:
            self.app.cfg["locked"] = locked
            self.app.save_cfg()

    # ---- 消息处理 ----

    def _button_zone_at(self, client_x: int) -> int:
        """命中按钮序号（0~2），未命中返回 -1。仅按钮可见（浮现中/完全浮现）时可点。"""
        cfg = self.app.cfg
        if not cfg["show_controls"] or self._buttons_fade() <= 0.5:
            return -1
        origin = self._current_height() if cfg["show_cover"] else 0
        if origin <= client_x < origin + render.CONTROL_CELL * 3:
            return (client_x - origin) // render.CONTROL_CELL
        return -1

    def _update_hover(self, client_x: int) -> None:
        if not self._hovered:
            self._hovered = True
            self._hover_change = time.monotonic()
            if not self._tracking:  # 注册离开通知
                tme = _TRACKMOUSEEVENT()
                tme.cbSize = ctypes.sizeof(_TRACKMOUSEEVENT)
                tme.dwFlags = _TME_LEAVE
                tme.hwndTrack = self.hwnd
                w.user32.TrackMouseEvent(ctypes.byref(tme))
                self._tracking = True
        self._hover_zone = self._button_zone_at(client_x) if self._buttons_fade() > 0.5 else -1

    def _on_message(self, hwnd, msg, wparam, lparam):
        if msg == WM_APP_ACTION:
            self.app.run_pending_actions()
            return 0
        if msg == _WM_TIMER:
            self.app.on_timer(wparam)
            return 0
        if msg == _WM_MOUSEMOVE:
            if self._drag is not None:
                start_x, start_y, win_x, win_y = self._drag
                pt = wintypes.POINT()
                w.user32.GetCursorPos(ctypes.byref(pt))
                cfg = self.app.cfg
                if cfg["mode"] == "taskbar":
                    tray, _ = w.resolve_taskbar(cfg["monitor"])
                    trc = wintypes.RECT()
                    w.user32.GetWindowRect(tray, ctypes.byref(trc))
                    cfg["position"] = "custom"
                    cfg["x_offset"] = win_x - trc.left + pt.x - start_x
                else:
                    cfg["float_x"] = win_x + pt.x - start_x
                    cfg["float_y"] = win_y + pt.y - start_y
                self.dock()
            else:
                self._update_hover(ctypes.c_short(lparam & 0xFFFF).value)
            return 0
        if msg == _WM_MOUSELEAVE:
            self._tracking = False
            if self._hovered:
                self._hovered = False
                self._hover_change = time.monotonic()
            self._hover_zone = -1
            return 0
        if msg == _WM_LBUTTONDOWN:
            if self.app.cfg["locked"]:
                return 0
            # 命中浮现中的按钮 → 控制命令；否则开始拖动
            click_x = ctypes.c_short(lparam & 0xFFFF).value
            zone = self._button_zone_at(click_x)
            if zone >= 0:
                self.app.control(("prev", "play_pause", "next")[zone])
                return 0
            pt = wintypes.POINT()
            w.user32.GetCursorPos(ctypes.byref(pt))
            rc = wintypes.RECT()
            w.user32.GetWindowRect(hwnd, ctypes.byref(rc))
            self._drag = (pt.x, pt.y, rc.left, rc.top)
            w.user32.SetCapture(hwnd)
            return 0
        if msg == _WM_LBUTTONUP and self._drag is not None:
            self._drag = None
            w.user32.ReleaseCapture()
            self.app.save_cfg()
            return 0
        if msg == _WM_RBUTTONUP:
            if not self.app.cfg["locked"]:
                pt = wintypes.POINT()
                w.user32.GetCursorPos(ctypes.byref(pt))
                self._popup_menu(pt.x, pt.y)
            return 0
        if msg == _WM_DESTROY:
            w.user32.KillTimer(hwnd, TIMER_REFRESH)
            w.user32.KillTimer(hwnd, TIMER_DOCK)
            return 0
        return w.user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    # ---- 右键菜单 ----

    def _popup_menu(self, x: int, y: int) -> None:
        actions: dict[int, object] = {}
        next_id = [1000]

        def fill(hmenu, items):
            for item in items:
                kind = item["kind"]
                if kind == "separator":
                    w.user32.AppendMenuW(hmenu, _MF_SEPARATOR, 0, None)
                    continue
                if kind == "submenu":
                    sub = w.user32.CreatePopupMenu()
                    fill(sub, item["children"])
                    w.user32.AppendMenuW(hmenu, _MF_POPUP, sub, item["label"])
                    continue
                cmd = next_id[0]
                next_id[0] += 1
                actions[cmd] = item["action"]
                flags = _MF_STRING
                if item.get("checked"):
                    flags |= _MF_CHECKED
                if not item.get("enabled", True):
                    flags |= _MF_GRAYED
                w.user32.AppendMenuW(hmenu, flags, cmd, item["label"])

        hmenu = w.user32.CreatePopupMenu()
        fill(hmenu, menu_mod.build_spec(self.app))
        w.user32.SetForegroundWindow(self.hwnd)  # 保证菜单能正常关闭
        cmd = w.user32.TrackPopupMenuEx(
            hmenu, _TPM_RETURNCMD | _TPM_NONOTIFY | _TPM_RIGHTBUTTON, x, y, self.hwnd, None,
        )
        w.user32.PostMessageW(self.hwnd, _WM_NULL, 0, 0)
        w.user32.DestroyMenu(hmenu)
        if cmd and cmd in actions:
            actions[cmd]()
            self.app.notify_menu_changed()

    # ---- 销毁 ----

    def destroy(self) -> None:
        w.user32.DestroyWindow(self.hwnd)
