"""配置读写：所有设置持久化到 config.json（与 exe 同目录 / 项目目录）。"""
import json
import os
import sys

# 打包成 exe 时配置文件放在 exe 旁边，开发时放在项目目录
if getattr(sys, "frozen", False):
    _BASE_DIR = os.path.dirname(sys.executable)
else:
    _BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(_BASE_DIR, "config.json")

DEFAULTS: dict = {
    # 外观
    "font_family": "Microsoft YaHei UI",
    "font_size": 13,            # 原文字号
    "text_color": "#FFFFFF",
    "trans_color": "#C8C8C8",
    "highlight_color": None,    # 逐字高亮色，None = 跟随系统强调色
    "shadow": True,             # 文字阴影
    "width": 560,               # 歌词区宽度（不含播放控制按钮）
    "show_controls": True,      # 任务栏播放控制按钮
    # 位置
    "mode": "taskbar",          # taskbar | floating
    "position": "tray_left",    # tray_left | left | center | right | custom
    "x_offset": None,           # position=custom 时任务栏内 x
    "float_x": None,            # 浮动模式屏幕坐标
    "float_y": None,
    "monitor": 0,
    "locked": False,            # 锁定后鼠标穿透
    "show_cover": True,         # 显示专辑封面
    # 歌词
    "second_line": "translation",  # 第二行：translation 译文 | romaji 罗马音 | off 关闭
    "karaoke": True,            # 逐字歌词（酷狗 KRC 源，匹配不上时逐行）
    "offset_ms": 0,             # 歌词时间偏移（提前为负）
    # 行为
    "hide_on_fullscreen": True,  # 全屏应用在前台时自动隐藏
    # 播放源
    "player_source": "auto",    # auto | netease | others
    "player_blocklist": ["chrome", "msedge", "firefox"],  # 不跟踪的播放器
}


def _migrate(cfg: dict) -> dict:
    """旧配置迁移：show_translation 布尔 → second_line 枚举。"""
    if "show_translation" in cfg and "second_line" not in cfg:
        cfg["second_line"] = "translation" if cfg["show_translation"] else "off"
    cfg.pop("show_translation", None)
    return cfg

_cache: dict | None = None


def load() -> dict:
    """读取配置（带内存缓存，save 后刷新）。"""
    global _cache
    if _cache is None:
        cfg = dict(DEFAULTS)
        try:
            with open(CONFIG_PATH, encoding="utf-8") as f:
                cfg.update(json.load(f))
        except Exception:
            pass
        _cache = _migrate(cfg)
    return _cache


def save(cfg: dict) -> None:
    global _cache
    _cache = cfg
    try:
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
    except Exception:
        pass  # 设置存不上不影响使用


def get(key: str):
    return load().get(key, DEFAULTS.get(key))


def set(key: str, value) -> None:
    cfg = load()
    cfg[key] = value
    save(cfg)
