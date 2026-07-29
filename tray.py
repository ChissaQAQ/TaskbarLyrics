"""系统托盘图标（pystray）：菜单与歌词右键菜单共享 menu.build_spec。

托盘菜单动作统一通过 app.post_action 投递到主线程执行
（tkinter 对话框等只能在主线程操作）。
"""
import threading

import pystray
from PIL import Image, ImageDraw

import menu as menu_mod


def make_icon_image(size: int = 64) -> Image.Image:
    """画一个简单的八分音符图标。"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = (79, 195, 247, 255)  # 亮蓝色
    s = size / 64
    d.ellipse([12 * s, 42 * s, 32 * s, 58 * s], fill=c)          # 符头
    d.rectangle([28 * s, 10 * s, 33 * s, 48 * s], fill=c)        # 符杆
    d.polygon([(33 * s, 10 * s), (50 * s, 18 * s), (33 * s, 28 * s)], fill=c)  # 符尾
    return img


class Tray:
    def __init__(self, app) -> None:
        self.app = app
        self.icon = pystray.Icon("TaskbarLyrics", make_icon_image(), "任务栏歌词", menu=self._build())
        self._thread = threading.Thread(target=self.icon.run, daemon=True)
        self._thread.start()

    def _build(self) -> pystray.Menu:
        return pystray.Menu(*self._convert(menu_mod.build_spec(self.app)))

    def _convert(self, spec: list[dict]) -> list:
        def make_action(fn):
            def action(icon, item):
                self.app.post_action(fn)
            return action

        def make_checked(value):
            def checked(item):
                return value
            return checked

        items = []
        for it in spec:
            kind = it["kind"]
            if kind == "separator":
                items.append(pystray.Menu.SEPARATOR)
                continue
            if kind == "submenu":
                items.append(pystray.MenuItem(
                    it["label"],
                    pystray.Menu(*self._convert(it["children"])),
                    enabled=it.get("enabled", True),
                ))
                continue
            items.append(pystray.MenuItem(
                it["label"],
                make_action(it["action"]),
                checked=make_checked(it.get("checked", False)) if kind in ("check", "radio") else None,
                radio=kind == "radio",
                enabled=it.get("enabled", True),
            ))
        return items

    def refresh(self) -> None:
        """菜单状态变化后重建（勾选项、动态标签等）。"""
        try:
            self.icon.menu = self._build()
            self.icon.update_menu()
        except Exception:
            pass

    def stop(self) -> None:
        try:
            self.icon.stop()
        except Exception:
            pass
