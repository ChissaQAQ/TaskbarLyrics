# 任务栏歌词（Taskbar Lyrics）

[![release](https://img.shields.io/github/v/release/ChissaQAQ/TaskbarLyrics?label=release)](https://github.com/ChissaQAQ/TaskbarLyrics/releases/latest)
[![downloads](https://img.shields.io/github/downloads/ChissaQAQ/TaskbarLyrics/total?label=downloads)](https://github.com/ChissaQAQ/TaskbarLyrics/releases)
[![build](https://github.com/ChissaQAQ/TaskbarLyrics/actions/workflows/build.yml/badge.svg)](https://github.com/ChissaQAQ/TaskbarLyrics/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

在 Windows 任务栏里显示正在播放的歌的歌词。歌词条是任务栏的子窗口，不是浮在上面的悬浮窗。

## 下载

在 [Releases](https://github.com/ChissaQAQ/TaskbarLyrics/releases/latest) 下载 `TaskbarLyrics.exe`，双击运行，不用安装。

需要先装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)，没装的话程序会提示。

更新记录见 [CHANGELOG.md](CHANGELOG.md)。

### 前提：播放器必须打开「系统媒体控件（SMTC）」上报

程序通过 Windows 的系统媒体控件（SMTC）知道你在听什么。播放器没开这个上报的话，歌词条会一直显示「暂无歌词」。

检查方法：播放时按一下音量键，系统弹出的媒体浮层里能看到歌名和封面就没问题。

看不到的话去播放器设置里找，一般在「设置 → 播放」下面，名字类似「在系统媒体控件中显示」。网易云音乐和 QQ 音乐的部分版本默认是关的。Spotify 和浏览器默认会上报，不过浏览器在程序的黑名单里，要用的话在设置里取消屏蔽。

## 功能

- 任务栏模式和浮动窗口模式
- 宽度跟着歌词长短变，也可以自动避开任务栏图标
- 逐字高亮（酷狗 KRC），太长的行会跟着进度横向滚动
- 双行显示：外文歌下行是译文或罗马音，中文歌下行是下一句
- 鼠标移上去显示封面、上一首 / 播放 / 下一首和歌名
- 专辑封面
- 过滤作词、作曲这类制作信息
- 文字颜色跟随系统深浅色，也可以自定义
- 居中或左对齐
- 全屏时自动隐藏
- 单曲循环时歌词会重新开始
- 播放器黑名单
- 自动检查更新，一键更新
- 歌词缓存到本地（30 天，最多 400 首），听过的歌断网也有词
- 锁定位置后鼠标穿透
- 托盘图标

设置保存在 exe 旁边的 `config.json`，删掉就恢复默认。如果那个目录不能写（比如放在 Program Files 里），会改存到 `%AppData%\TaskbarLyrics\`。

## 原理

- 歌名、歌手和播放状态从 SMTC 读。网易云不报播放进度，所以进度是本地计时算的
- 歌词依次从网易云、QQ 音乐、LRCLIB 找，逐字时间轴来自酷狗 KRC
- 用 `SetParent` 把窗口挂进任务栏（主屏 `Shell_TrayWnd`，副屏 `Shell_SecondaryTrayWnd`）

## 已知问题

- 拖进度条感知不到，歌词会接着按原来的节奏走，换首歌就好了
- 程序启动前就在放的歌，从启动那一刻开始计时
- 罗马音只有网易云有

## 开发

```powershell
dotnet build wpf/TaskbarLyrics -c Release

# 先关掉正在运行的程序，不然 exe 被占用会报 MSB4018
Stop-Process -Name TaskbarLyrics -Force -ErrorAction SilentlyContinue
dotnet publish wpf/TaskbarLyrics -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o wpf/publish

# 测试歌词抓取（不读缓存）。时长秒可省略，网易云客户端不报时长
wpf/publish/TaskbarLyrics.exe --lyrics-test "歌名" "歌手" [translation|romaji|off] [时长秒]

# 测试检查更新
wpf/publish/TaskbarLyrics.exe --update-test
```

代码在 `wpf/TaskbarLyrics/`：

| 文件 | 内容 |
| --- | --- |
| `MainController.cs` | 主循环，读 SMTC、找当前行、交给界面 |
| `SmtcListener.cs` | 监听系统媒体控件 |
| `Lyrics.cs` | 抓歌词、选版本、对齐译文和逐字时间 |
| `LyricsCache.cs` | 歌词缓存 |
| `OverlayWindow.xaml(.cs)` | 歌词显示和动画 |
| `NativeMethods.cs` | 挂进任务栏、置顶、鼠标穿透 |
| `TaskbarFreeSpace.cs` | 计算任务栏上的空位 |
| `SettingsWindow.xaml(.cs)` / `TrayIcon.cs` / `AppConfig.cs` | 设置、托盘、配置 |
| `Updater.cs` | 检查和安装更新 |

不少看着多余的写法是为了绕开 Windows 的坑，注释里有写原因，改之前可以先看看。

## 反馈

有问题可以提 [Issue](https://github.com/ChissaQAQ/TaskbarLyrics/issues)。提之前先确认 SMTC 是通的，再附上程序、Windows 和播放器的版本。歌词有问题的话请写上歌名和歌手。

## 许可

代码使用 [MIT](LICENSE) 许可。

歌词来自网易云音乐、QQ 音乐、LRCLIB 和酷狗的公开接口，版权归原权利方。本地缓存只是为了少发请求，30 天后过期。仅供个人使用。
