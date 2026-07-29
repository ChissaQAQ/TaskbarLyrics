"""监听 Windows SMTC，获取网易云音乐（或其他播放器）的播放状态。

后台线程中运行 poll_loop：订阅会话的播放信息/媒体属性事件（暂停、恢复、
切歌立即推送），并以 0.5s 轮询兜底。通过回调把 PlaybackState 推给主程序。
SMTC 的进度只在切歌/暂停/拖动时刷新，播放中由 current_position() 本地插值。
"""
import asyncio
import queue
import threading
import time
from dataclasses import dataclass

from winsdk.windows.media.control import (
    GlobalSystemMediaTransportControlsSessionManager as SessionManager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus,
)

POLL_INTERVAL = 0.5  # 秒（兜底轮询；事件到达会立即刷新）
SMTC_LATENCY_S = 0.45  # 网易云上报暂停/恢复的固有延迟（实测均值 0.42~0.56s）


@dataclass
class PlaybackState:
    title: str = ""
    artist: str = ""
    duration_s: float = 0.0
    playing: bool = False
    source_id: str = ""  # SMTC 来源（如 cloudmusic.exe）
    cover_bytes: bytes | None = None  # 专辑封面（JPEG/PNG 字节）
    # 进度基准：base_position_s 是 base_time 时刻的播放进度
    base_position_s: float = 0.0
    base_time: float = 0.0
    # SMTC 上报的原始进度（网易云恒为 0，用于判断是否有真实进度更新）
    raw_position_s: float = 0.0

    @property
    def key(self) -> str:
        return f"{self.title}｜{self.artist}"

    def current_position_s(self) -> float:
        if self.playing:
            pos = self.base_position_s + (time.monotonic() - self.base_time)
        else:
            pos = self.base_position_s
        if self.duration_s > 0:
            pos = min(pos, self.duration_s)
        return max(pos, 0.0)

    def merge_from(self, prev: "PlaybackState | None") -> None:
        """同一首歌且 SMTC 进度没变化（网易云不上报进度）时，沿用本地计时。"""
        if prev and prev.key == self.key and self.raw_position_s == prev.raw_position_s:
            self.base_position_s = prev.current_position_s()
            self.base_time = time.monotonic()
        # 否则视为切歌/拖动进度条：从 raw 进度重新计时（base 即 raw）


def _pick_session(sessions, source: str, blocklist: list[str] | None = None):
    """source: auto（优先网易云，兜底任意播放器）| netease（仅网易云）| others（排除网易云）。

    blocklist: 不跟踪的来源关键词列表（匹配 source_app_user_model_id 小写子串）。
    """
    if not sessions:
        return None
    blocklist = [b.lower() for b in (blocklist or [])]

    def usable(s) -> bool:
        sid = s.source_app_user_model_id.lower()
        return not any(b in sid for b in blocklist)

    def is_netease(s) -> bool:
        return "cloudmusic" in s.source_app_user_model_id.lower()

    if source == "netease":
        return next((s for s in sessions if is_netease(s)), None)
    if source == "others":
        others = [s for s in sessions if not is_netease(s) and usable(s)]
        return next(
            (s for s in others if s.get_playback_info().playback_status == PlaybackStatus.PLAYING),
            others[0] if others else None,
        )
    # auto
    chosen = next((s for s in sessions if is_netease(s)), None)
    if chosen is None:
        chosen = next(
            (s for s in sessions
             if usable(s) and s.get_playback_info().playback_status == PlaybackStatus.PLAYING),
            None,
        )
    if chosen is None:
        chosen = next((s for s in sessions if usable(s)), None)
    return chosen


async def _read_from_session(session) -> PlaybackState:
    props = await session.try_get_media_properties_async()
    timeline = session.get_timeline_properties()
    info = session.get_playback_info()
    position_s = timeline.position.total_seconds()
    return PlaybackState(
        title=props.title or "",
        artist=props.artist or "",
        duration_s=timeline.end_time.total_seconds(),
        playing=info.playback_status == PlaybackStatus.PLAYING,
        source_id=session.source_app_user_model_id or "",
        base_position_s=position_s,
        base_time=time.monotonic(),
        raw_position_s=position_s,
    )


# ---- 播放控制 ----

_CONTROL_QUEUE: queue.Queue = queue.Queue()


def control(action: str) -> None:
    """向当前会话发送播放控制：prev | play_pause | next。"""
    _CONTROL_QUEUE.put(action)


async def _read_thumbnail(session) -> bytes | None:
    """读取当前曲目的专辑封面（SMTC 缩略图），失败返回 None。"""
    try:
        from winsdk.windows.storage.streams import DataReader

        props = await session.try_get_media_properties_async()
        if not props.thumbnail:
            return None
        stream = await props.thumbnail.open_read_async()
        reader = DataReader(stream)
        await reader.load_async(stream.size)
        buf = bytearray(stream.size)
        reader.read_bytes(buf)
        return bytes(buf)
    except Exception:
        return None


async def _drain_control(session, last_playing: bool) -> None:
    """在监听循环线程里执行控制命令。"""
    while session is not None:
        try:
            action = _CONTROL_QUEUE.get_nowait()
        except queue.Empty:
            return
        try:
            if action == "prev":
                await session.try_skip_previous_async()
            elif action == "next":
                await session.try_skip_next_async()
            elif action == "play_pause":
                if last_playing:
                    await session.try_pause_async()
                else:
                    await session.try_play_async()
        except Exception:
            pass  # 播放器不支持该操作时忽略


async def _poll_async(on_state, stop_event: threading.Event, get_source, get_blocklist) -> None:
    manager = await SessionManager.request_async()
    loop = asyncio.get_running_loop()
    changed = asyncio.Event()
    session = None
    tokens: list = []

    def on_smtc_event(sender, args) -> None:
        loop.call_soon_threadsafe(changed.set)

    def unsubscribe() -> None:
        nonlocal tokens
        if session is not None:
            for remove, token in tokens:
                try:
                    remove(token)
                except Exception:
                    pass
        tokens = []

    prev: PlaybackState | None = None
    thumb_key = ""      # 已读封面的歌曲
    thumb_bytes = None  # 当前歌曲的封面字节
    try:
        while not stop_event.is_set():
            try:
                new_session = _pick_session(manager.get_sessions(), get_source(), get_blocklist())
                if new_session is not session:
                    unsubscribe()
                    session = new_session
                    if session is not None:
                        # 暂停/恢复 → PlaybackInfoChanged；切歌 → MediaPropertiesChanged
                        tokens = [
                            (session.remove_playback_info_changed,
                             session.add_playback_info_changed(on_smtc_event)),
                            (session.remove_media_properties_changed,
                             session.add_media_properties_changed(on_smtc_event)),
                        ]
                await _drain_control(session, prev.playing if prev else False)
                state = await _read_from_session(session) if session is not None else None
                # 封面只在切歌时读取一次
                if state is not None and session is not None:
                    if state.key != thumb_key:
                        thumb_key = state.key
                        thumb_bytes = await _read_thumbnail(session)
                    state.cover_bytes = thumb_bytes
            except Exception:
                state = None  # SMTC 偶发异常不应杀死监听循环
            if state is not None:
                state.merge_from(prev)
                # 暂停/恢复检测有固有延迟：对称补偿，消除逐次累积的进度漂移。
                # 恢复时把计时起点提前 L；暂停时回退多算的 L。
                # 无论真实音频何时停/起，同歌内正负相抵，不再累积误差。
                if prev is not None and prev.key == state.key and prev.playing != state.playing:
                    if state.playing:
                        state.base_time -= SMTC_LATENCY_S
                    else:
                        state.base_position_s = max(0.0, state.base_position_s - SMTC_LATENCY_S)
            prev = state
            on_state(state)

            changed.clear()
            try:
                await asyncio.wait_for(changed.wait(), timeout=POLL_INTERVAL)
            except asyncio.TimeoutError:
                pass
    finally:
        unsubscribe()


def poll_loop(on_state, stop_event: threading.Event, get_source=lambda: "auto",
              get_blocklist=lambda: []) -> None:
    """后台线程入口：事件驱动 + 兜底轮询，回调 on_state(PlaybackState | None)。

    get_source: 返回当前播放器来源过滤（auto/netease/others），每次轮询时取值。
    get_blocklist: 返回不跟踪的来源关键词列表，每次轮询时取值。
    """
    asyncio.run(_poll_async(on_state, stop_event, get_source, get_blocklist))
