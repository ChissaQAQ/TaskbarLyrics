"""诊断脚本：列出当前系统所有 SMTC 媒体会话。

用法：先用任意播放器（尤其是网易云音乐）播放一首歌，然后运行：
    .venv/Scripts/python.exe debug_smtc.py
如果能看到 cloudmusic 相关会话，说明网易云音乐上报了播放信息，主程序可用。
"""
import asyncio

from winsdk.windows.media.control import (
    GlobalSystemMediaTransportControlsSessionManager as SessionManager,
)


async def main() -> None:
    manager = await SessionManager.request_async()
    sessions = manager.get_sessions()
    if not sessions:
        print("当前没有任何 SMTC 会话（请先播放一首歌再运行）")
        return
    for s in sessions:
        props = await s.try_get_media_properties_async()
        timeline = s.get_timeline_properties()
        info = s.get_playback_info()
        print(f"来源: {s.source_app_user_model_id}")
        print(f"  标题: {props.title} | 歌手: {props.artist}")
        print(f"  进度: {timeline.position} / {timeline.end_time}")
        print(f"  状态: {info.playback_status}")
        print()


if __name__ == "__main__":
    asyncio.run(main())
