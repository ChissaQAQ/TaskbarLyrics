"""统一设置窗口（tkinter）：所有选项集中管理。

从歌词条右键菜单或托盘菜单的「打开设置…」进入。
"""
import tkinter as tk
from tkinter import colorchooser, font as tkfont, ttk

import autostart
import win32util as w

_SECOND_LINES = (("translation", "译文"), ("romaji", "罗马音"), ("off", "关闭"))
_PLAYER_SOURCES = (("auto", "自动（优先网易云）"), ("netease", "仅网易云音乐"), ("others", "仅其他播放器"))
_WIDTHS = (420, 560, 700)
_FONT_SIZES = (10, 12, 14, 16, 18)


def open_settings(app) -> None:
    """打开模态设置窗口；确定/应用后写配置并实时生效。"""
    cfg = app.cfg
    root = app.root

    win = tk.Toplevel(root)
    win.title("任务栏歌词 - 设置")
    win.resizable(False, False)
    win.attributes("-topmost", True)

    # ---- 变量 ----
    v = {
        "mode": tk.StringVar(value=cfg["mode"]),
        "monitor": tk.IntVar(value=cfg["monitor"]),
        "locked": tk.BooleanVar(value=cfg["locked"]),
        "hide_on_fullscreen": tk.BooleanVar(value=cfg["hide_on_fullscreen"]),
        "autostart": tk.BooleanVar(value=autostart.is_enabled()),
        "second_line": tk.StringVar(value=cfg["second_line"]),
        "karaoke": tk.BooleanVar(value=cfg["karaoke"]),
        "show_cover": tk.BooleanVar(value=cfg["show_cover"]),
        "show_controls": tk.BooleanVar(value=cfg["show_controls"]),
        "offset_s": tk.DoubleVar(value=cfg["offset_ms"] / 1000),
        "player_source": tk.StringVar(value=cfg["player_source"]),
        "font_family": tk.StringVar(value=cfg["font_family"]),
        "font_size": tk.IntVar(value=cfg["font_size"]),
        "width": tk.IntVar(value=cfg["width"]),
        "shadow": tk.BooleanVar(value=cfg["shadow"]),
        "text_color": tk.StringVar(value=cfg["text_color"]),
        "trans_color": tk.StringVar(value=cfg["trans_color"]),
    }

    nb = ttk.Notebook(win)
    nb.pack(fill="both", expand=True, padx=10, pady=10)
    pad = {"padx": 10, "pady": 5}

    def add_tab(title: str) -> ttk.Frame:
        frame = ttk.Frame(nb)
        nb.add(frame, text=title)
        return frame

    def check(parent, text, var, row, col=0, span=2):
        ttk.Checkbutton(parent, text=text, variable=var).grid(row=row, column=col, sticky="w", **pad)

    # ---- 通用 ----
    tab1 = add_tab("通用")
    ttk.Label(tab1, text="显示模式").grid(row=0, column=0, sticky="w", **pad)
    ttk.Radiobutton(tab1, text="任务栏模式", variable=v["mode"], value="taskbar").grid(row=0, column=1, sticky="w", **pad)
    ttk.Radiobutton(tab1, text="浮动模式", variable=v["mode"], value="floating").grid(row=0, column=2, sticky="w", **pad)

    ttk.Label(tab1, text="显示器").grid(row=1, column=0, sticky="w", **pad)
    mons = w.monitors()
    mon_labels = [f"显示器 {i + 1}（{m['rect'][2] - m['rect'][0]}x{m['rect'][3] - m['rect'][1]}）"
                  + ("（主）" if m["primary"] else "") for i, m in enumerate(mons)]
    mon_box = ttk.Combobox(tab1, values=mon_labels, state="readonly", width=26)
    mon_box.current(min(v["monitor"].get(), len(mon_labels) - 1))
    mon_box.grid(row=1, column=1, columnspan=2, sticky="w", **pad)

    check(tab1, "锁定位置（鼠标穿透，托盘菜单解锁）", v["locked"], 2)
    check(tab1, "全屏应用在前台时自动隐藏", v["hide_on_fullscreen"], 3)
    check(tab1, "开机自启", v["autostart"], 4)

    # ---- 歌词 ----
    tab2 = add_tab("歌词")
    ttk.Label(tab2, text="第二行").grid(row=0, column=0, sticky="w", **pad)
    for col, (key, label) in enumerate(_SECOND_LINES):
        ttk.Radiobutton(tab2, text=label, variable=v["second_line"], value=key).grid(row=0, column=col + 1, sticky="w", **pad)
    check(tab2, "逐字歌词（卡拉 OK 扫过）", v["karaoke"], 1)
    check(tab2, "显示专辑封面", v["show_cover"], 2)
    check(tab2, "悬停显示播放控制按钮", v["show_controls"], 3)

    ttk.Label(tab2, text="歌词偏移（秒）").grid(row=4, column=0, sticky="w", **pad)
    ttk.Spinbox(tab2, from_=-3.0, to=3.0, increment=0.1, textvariable=v["offset_s"], width=8).grid(row=4, column=1, sticky="w", **pad)

    ttk.Label(tab2, text="播放器来源").grid(row=5, column=0, sticky="w", **pad)
    for col, (key, label) in enumerate(_PLAYER_SOURCES):
        ttk.Radiobutton(tab2, text=label, variable=v["player_source"], value=key).grid(row=5, column=col + 1, sticky="w", **pad)

    block_btn = ttk.Button(tab2, text=app.block_current_label(), command=lambda: (app.toggle_block_current(), block_btn.configure(text=app.block_current_label())))
    block_btn.grid(row=6, column=0, columnspan=3, sticky="w", **pad)

    # ---- 外观 ----
    tab3 = add_tab("外观")
    ttk.Label(tab3, text="字体").grid(row=0, column=0, sticky="w", **pad)
    families = sorted(tkfont.families(root))
    ttk.Combobox(tab3, textvariable=v["font_family"], values=families, width=26).grid(row=0, column=1, columnspan=2, sticky="w", **pad)

    ttk.Label(tab3, text="字号").grid(row=1, column=0, sticky="w", **pad)
    ttk.Spinbox(tab3, from_=8, to=24, textvariable=v["font_size"], width=6).grid(row=1, column=1, sticky="w", **pad)

    ttk.Label(tab3, text="歌词区宽度").grid(row=2, column=0, sticky="w", **pad)
    width_box = ttk.Combobox(tab3, values=_WIDTHS, textvariable=v["width"], width=8)
    width_box.grid(row=2, column=1, sticky="w", **pad)

    def pick_color(key: str, btn: tk.Button) -> None:
        result = colorchooser.askcolor(initialcolor=v[key].get(), parent=win)
        if result and result[1]:
            v[key].set(result[1])
            btn.configure(bg=result[1])

    ttk.Label(tab3, text="原文颜色").grid(row=3, column=0, sticky="w", **pad)
    btn1 = tk.Button(tab3, width=6, bg=v["text_color"].get(), relief="ridge",
                     command=lambda: pick_color("text_color", btn1))
    btn1.grid(row=3, column=1, sticky="w", **pad)
    ttk.Label(tab3, text="译文颜色").grid(row=4, column=0, sticky="w", **pad)
    btn2 = tk.Button(tab3, width=6, bg=v["trans_color"].get(), relief="ridge",
                     command=lambda: pick_color("trans_color", btn2))
    btn2.grid(row=4, column=1, sticky="w", **pad)
    check(tab3, "文字阴影", v["shadow"], 5)

    # ---- 底部按钮 ----
    def apply() -> bool:
        try:
            cfg["font_size"] = max(8, min(24, int(v["font_size"].get())))
            cfg["width"] = max(200, min(2000, int(v["width"].get())))
            cfg["offset_ms"] = max(-3000, min(3000, int(v["offset_s"].get() * 1000)))
        except Exception:
            return False
        cfg["mode"] = v["mode"].get()
        cfg["monitor"] = mon_box.current()
        cfg["hide_on_fullscreen"] = v["hide_on_fullscreen"].get()
        cfg["second_line"] = v["second_line"].get()
        cfg["karaoke"] = v["karaoke"].get()
        cfg["show_cover"] = v["show_cover"].get()
        cfg["show_controls"] = v["show_controls"].get()
        cfg["player_source"] = v["player_source"].get()
        cfg["font_family"] = v["font_family"].get().strip() or cfg["font_family"]
        cfg["shadow"] = v["shadow"].get()
        cfg["text_color"] = v["text_color"].get()
        cfg["trans_color"] = v["trans_color"].get()
        app.set_locked(v["locked"].get())
        app.set_autostart(v["autostart"].get())
        app.save_cfg()
        app.apply_appearance()          # 外观/布局即时生效
        app.overlay.update_fullscreen()  # 全屏隐藏设置即时生效
        if cfg["second_line"] != app._last_second_line:
            app._last_second_line = cfg["second_line"]
            app._song_key = ""  # 第二行内容变化 → 重新抓歌词
        return True

    btns = ttk.Frame(win)
    btns.pack(pady=(0, 10))
    ttk.Button(btns, text="确定", command=lambda: apply() and win.destroy()).pack(side="left", padx=8)
    ttk.Button(btns, text="应用", command=apply).pack(side="left", padx=8)
    ttk.Button(btns, text="取消", command=win.destroy).pack(side="left", padx=8)

    # 屏幕居中显示
    win.update_idletasks()
    sw, sh = win.winfo_screenwidth(), win.winfo_screenheight()
    ww, wh = win.winfo_width(), win.winfo_height()
    win.geometry(f"+{(sw - ww) // 2}+{(sh - wh) // 2}")

    # 注意：不能用 transient(root)，root 是隐藏窗口，transient 会让对话框跟随隐藏
    win.deiconify()
    win.lift()
    win.focus_force()
    win.grab_set()
    root.wait_window(win)
