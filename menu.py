"""共享菜单定义：同一份菜单规范，同时渲染成 win32 弹出菜单和 pystray 托盘菜单。

菜单项规范（dict）：
- kind: "item" | "check" | "radio" | "submenu" | "separator"
- label: 显示文本（"submenu"/"separator" 以外的项必填）
- checked: 是否打勾（check/radio）
- enabled: 是否可用（默认 True）
- action: 点击回调（separator/submenu 无）
- children: submenu 的子项列表

说明：详细设置统一在「打开设置…」的设置窗口中管理，
菜单只保留高频操作。
"""


def build_spec(app) -> list[dict]:
    cfg = app.cfg
    return [
        {"label": "任务栏模式", "kind": "radio", "checked": cfg["mode"] == "taskbar",
         "action": lambda: app.set_mode("taskbar")},
        {"label": "浮动模式", "kind": "radio", "checked": cfg["mode"] == "floating",
         "action": lambda: app.set_mode("floating")},
        {"label": "锁定位置（鼠标穿透）", "kind": "check", "checked": cfg["locked"],
         "action": lambda: app.set_locked(not cfg["locked"])},
        {"kind": "separator"},
        {"label": "打开设置…", "kind": "item", "action": app.open_settings},
        {"kind": "separator"},
        {"label": "退出", "kind": "item", "action": app.quit},
    ]
