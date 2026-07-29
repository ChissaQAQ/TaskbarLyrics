"""打包脚本：生成图标并用 PyInstaller 打单文件 exe。

用法：.venv/Scripts/python.exe build.py
产物：dist/TaskbarLyrics.exe
"""
import subprocess
import sys

from tray import make_icon_image


def main() -> None:
    make_icon_image(256).save(
        "icon.ico",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print("icon.ico 已生成")
    subprocess.run(
        [
            sys.executable, "-m", "PyInstaller",
            "--onefile", "--noconsole",
            "--name", "TaskbarLyrics",
            "--icon", "icon.ico",
            # 注意：不要 --collect-all winsdk，静态分析已能跟踪用到的命名空间，
            # 全量收集只会多 1MB；体积大头是 Python 运行时和 tkinter
            "--clean", "-y",
            "main.py",
        ],
        check=True,
    )
    print("打包完成：dist/TaskbarLyrics.exe")


if __name__ == "__main__":
    main()
