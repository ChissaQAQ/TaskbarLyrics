"""开机自启：读写注册表 HKCU\\...\\Run 下的 TaskbarLyrics 项。"""
import os
import sys
import winreg

_RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
_VALUE_NAME = "TaskbarLyrics"


def _command() -> str:
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}"'
    # 开发模式：用 venv 的 pythonw 启动 main.py
    root = os.path.dirname(os.path.abspath(__file__))
    pythonw = os.path.join(root, ".venv", "Scripts", "pythonw.exe")
    return f'"{pythonw}" "{os.path.join(root, "main.py")}"'


def is_enabled() -> bool:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _RUN_KEY) as key:
            winreg.QueryValueEx(key, _VALUE_NAME)
        return True
    except OSError:
        return False


def set_enabled(enabled: bool) -> None:
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, _RUN_KEY) as key:
        if enabled:
            winreg.SetValueEx(key, _VALUE_NAME, 0, winreg.REG_SZ, _command())
        else:
            try:
                winreg.DeleteValue(key, _VALUE_NAME)
            except OSError:
                pass
