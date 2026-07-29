@echo off
rem 双击启动任务栏歌词（无控制台窗口）
cd /d "%~dp0"
start "" ".venv\Scripts\pythonw.exe" main.py
