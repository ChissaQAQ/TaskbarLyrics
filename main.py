"""任务栏歌词主程序（exe 入口）。

结构：tk 隐藏根窗口的 mainloop 作为主消息泵；Win32 分层窗口显示歌词；
后台线程轮询 SMTC；托盘图标独立线程；菜单动作统一在主线程执行。
"""
import ctypes
import queue
import threading
import time
import tkinter as tk

import autostart
import lyrics
import settings
import settings_window
import smtc_listener
import tray as tray_mod
import win32util as w
from overlay import TIMER_DOCK, WM_APP_ACTION, Overlay, set_dpi_awareness


class App:
    def __init__(self) -> None:
        set_dpi_awareness()
        # 提高系统定时器精度到 1ms，16ms 的刷新定时器才能真正跑 ~60fps
        try:
            ctypes.windll.winmm.timeBeginPeriod(1)
        except Exception:
            pass
        self.root = tk.Tk()
        self.root.withdraw()  # 隐藏根窗口，只借用它的消息循环
        try:  # 让 tk 界面按系统 DPI 缩放
            dpi = ctypes.windll.user32.GetDpiForSystem()
            self.root.tk.call("tk", "scaling", dpi / 72)
        except Exception:
            pass

        self.cfg = settings.load()
        self._state: smtc_listener.PlaybackState | None = None
        self._lines: lyrics.LyricLines | None = None
        self._karaoke: dict = {}
        self._song_key = ""
        self._retry_at: float | None = None
        self._retry_count = 0
        self._replayed = False  # 单曲循环重播检测标记
        self._cover_song = ""   # 已设置封面的歌曲
        self._cover_img = None  # 当前封面（PIL Image）
        self._last_second_line = self.cfg["second_line"]  # 设置窗口里改第二行时对比用
        self._stop = threading.Event()
        self._pending: queue.Queue = queue.Queue()

        self.overlay = Overlay(self)
        self.tray = tray_mod.Tray(self)
        self._poll_thread = threading.Thread(
            target=smtc_listener.poll_loop,
            args=(self._on_state, self._stop,
                  lambda: self.cfg["player_source"],
                  lambda: self.cfg["player_blocklist"]),
            daemon=True,
        )
        self._poll_thread.start()

    # ---- SMTC 回调（后台线程，只更新数据）----

    def _on_state(self, state: smtc_listener.PlaybackState | None) -> None:
        if state is None or not state.title:
            self._state = None
            return
        # 专辑封面（切歌时由监听线程读好字节，这里只解码；纯白占位图不显示）
        if state.cover_bytes is not None and state.key != self._cover_song:
            self._cover_song = state.key
            cover = None
            try:
                import io

                from PIL import Image, ImageStat

                img = Image.open(io.BytesIO(state.cover_bytes)).convert("L")
                # 接近纯色的封面（如网易云的白色占位图）视为无封面
                if max(ImageStat.Stat(img.resize((8, 8))).stddev) > 8:
                    cover = Image.open(io.BytesIO(state.cover_bytes))
            except Exception:
                cover = None
            self._cover_img = cover
            self.overlay.set_cover(cover, state.key if cover else "")
        # 首选源（网易云）偶发失败时会落到无译文的备选源，且结果缓存整首歌——
        # 标记 5 秒后重试（每首歌最多 2 次），让译文自愈
        need_retry = (
            self._retry_at is not None
            and time.monotonic() >= self._retry_at
            and state.key == self._song_key
        )
        if state.key != self._song_key or need_retry:
            if state.key != self._song_key:
                self._retry_count = 0
                self._replayed = False  # 切歌后重置单曲循环检测
            self._song_key = state.key
            self._retry_at = None
            try:
                self._lines, self._karaoke, source = lyrics.fetch_lyrics(
                    state.title, state.artist, state.duration_s,
                    with_karaoke=self.cfg["karaoke"],
                    second_line=self.cfg["second_line"],
                )
            except Exception:
                self._lines, self._karaoke, source = None, {}, ""  # 网络异常时退化为显示歌名
            if (self._lines is None or source != "_fetch_netease") and self._retry_count < 2:
                self._retry_count += 1
                self._retry_at = time.monotonic() + 5
        self._state = state

    # ---- 定时器（主线程 WndProc 调用）----

    def on_timer(self, timer_id: int) -> None:
        if timer_id == TIMER_DOCK:
            self.overlay.dock()
            self.overlay.update_fullscreen()
            return
        # TIMER_REFRESH：按本地计时刷新当前歌词行
        state = self._state
        if state is None:
            self.overlay.set_lyric("", "")
            return
        if self._lines:
            pos_ms = int(state.current_position_s() * 1000) + self.cfg["offset_ms"]
            pos_ms = max(0, pos_ms)
            # 单曲循环检测：进度远超最后一句歌词 → 判定为重播，计时归零
            if not self._replayed and pos_ms > self._lines[-1][0] + 20000:
                self._replayed = True
                state.base_position_s = 0.0
                state.base_time = time.monotonic()
                pos_ms = max(0, self.cfg["offset_ms"])
            index, original, translation = lyrics.current_line(self._lines, pos_ms)
            karaoke = None
            if original and index >= 0 and self.cfg["karaoke"]:
                words = self._karaoke.get(self._lines[index][0])
                if words:
                    karaoke = (words, pos_ms - self._lines[index][0])
        else:
            original = f"{state.title} - {state.artist}" if state.artist else state.title
            translation = ""
            karaoke = None
        self.overlay.set_lyric(original, translation, karaoke, playing=state.playing)
        self.overlay.maybe_render()

    # ---- 菜单动作 ----

    def save_cfg(self) -> None:
        settings.save(self.cfg)
        self.notify_menu_changed()

    def notify_menu_changed(self) -> None:
        tray = getattr(self, "tray", None)
        if tray is not None:
            tray.refresh()

    def set_mode(self, mode: str) -> None:
        self.cfg["mode"] = mode
        self.save_cfg()
        self.overlay.apply_layout()

    def set_locked(self, locked: bool) -> None:
        self.cfg["locked"] = locked
        self.save_cfg()
        self.overlay.set_locked(locked, save=False)

    def set_font_size(self, size: int) -> None:
        self.cfg["font_size"] = size
        self.save_cfg()
        self.overlay.apply_layout()

    def set_position(self, position: str) -> None:
        self.cfg["position"] = position
        self.cfg["x_offset"] = None
        self.save_cfg()
        self.overlay.apply_layout()

    def set_width(self, width: int) -> None:
        self.cfg["width"] = width
        self.save_cfg()
        self.overlay.apply_layout()

    def set_monitor(self, index: int) -> None:
        self.cfg["monitor"] = index
        self.save_cfg()
        self.overlay.apply_layout()

    def adjust_offset(self, delta_ms: int, absolute: bool = False) -> None:
        self.cfg["offset_ms"] = delta_ms if absolute else self.cfg["offset_ms"] + delta_ms
        self.cfg["offset_ms"] = max(-3000, min(3000, self.cfg["offset_ms"]))
        self.save_cfg()

    def set_second_line(self, mode: str) -> None:
        self.cfg["second_line"] = mode
        self.save_cfg()
        self._song_key = ""  # 强制重新抓歌词（换第二行内容）

    def set_karaoke(self, enabled: bool) -> None:
        self.cfg["karaoke"] = enabled
        self.save_cfg()
        self._song_key = ""  # 强制重新抓歌词（带上/卸下逐字数据）

    def set_show_controls(self, show: bool) -> None:
        self.cfg["show_controls"] = show
        self.save_cfg()
        self.overlay.apply_layout()

    def set_hide_on_fullscreen(self, hide: bool) -> None:
        self.cfg["hide_on_fullscreen"] = hide
        self.save_cfg()
        self.overlay.update_fullscreen()

    def set_player_source(self, source: str) -> None:
        self.cfg["player_source"] = source
        self.save_cfg()

    def control(self, action: str) -> None:
        """播放控制按钮：prev | play_pause | next。"""
        smtc_listener.control(action)

    def current_source_id(self) -> str:
        return self._state.source_id if self._state else ""

    def block_current_label(self) -> str:
        sid = self.current_source_id()
        if not sid:
            return "屏蔽当前播放器（无会话）"
        blocked = any(b in sid.lower() for b in self.cfg["player_blocklist"])
        return f"取消屏蔽『{sid}』" if blocked else f"屏蔽『{sid}』"

    def toggle_block_current(self) -> None:
        sid = self.current_source_id()
        if not sid:
            return
        blocklist = self.cfg["player_blocklist"]
        existing = next((b for b in blocklist if b in sid.lower()), None)
        if existing:
            blocklist.remove(existing)
        else:
            blocklist.append(sid.lower())
        self.save_cfg()

    def set_autostart(self, enabled: bool) -> None:
        try:
            autostart.set_enabled(enabled)
        except OSError:
            pass  # 注册表写失败不致命
        self.notify_menu_changed()

    def apply_appearance(self) -> None:
        self.overlay.apply_layout()

    def open_settings(self) -> None:
        settings_window.open_settings(self)

    # ---- 托盘动作跨线程投递 ----

    def post_action(self, action) -> None:
        """托盘线程的菜单动作投递到主线程执行。"""
        self._pending.put(action)
        w.user32.PostMessageW(self.overlay.hwnd, WM_APP_ACTION, 0, 0)

    def run_pending_actions(self) -> None:
        while True:
            try:
                action = self._pending.get_nowait()
            except queue.Empty:
                break
            try:
                action()
            except Exception:
                pass  # 单个动作失败不影响后续
        self.notify_menu_changed()

    # ---- 退出 ----

    def quit(self) -> None:
        self._stop.set()
        self.tray.stop()
        self.overlay.destroy()
        try:
            ctypes.windll.winmm.timeEndPeriod(1)
        except Exception:
            pass
        self.root.quit()

    def run(self) -> None:
        self.root.mainloop()


def main() -> None:
    App().run()


if __name__ == "__main__":
    main()
