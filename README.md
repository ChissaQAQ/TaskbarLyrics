# 任务栏歌词（Taskbar Lyrics）

[![release](https://img.shields.io/github/v/release/ChissaQAQ/TaskbarLyrics?label=release)](https://github.com/ChissaQAQ/TaskbarLyrics/releases/latest)
[![downloads](https://img.shields.io/github/downloads/ChissaQAQ/TaskbarLyrics/total?label=downloads)](https://github.com/ChissaQAQ/TaskbarLyrics/releases)
[![build](https://github.com/ChissaQAQ/TaskbarLyrics/actions/workflows/build.yml/badge.svg)](https://github.com/ChissaQAQ/TaskbarLyrics/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

把网易云音乐当前播放歌曲的同步歌词，**原生显示在 Windows 任务栏里**
（窗口是任务栏的子窗口，不是悬浮窗）。对标 Lyricify Lite 的视觉与动画体验。

> WPF（.NET 10）实现，单文件 exe（框架依赖，约 25MB —— 大头是 Windows SDK 的
> WinRT 投影程序集，读 SMTC 必须带上它）。

## 下载

到 [Releases](https://github.com/ChissaQAQ/TaskbarLyrics/releases/latest) 下载
`TaskbarLyrics.exe`，双击即用（无控制台窗口、无需安装），然后用网易云音乐播放歌曲。

前置条件只有一个：[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。
没装的话双击会弹提示并给出下载链接。

历次改动见 [CHANGELOG.md](CHANGELOG.md)。

### 前提：播放器必须打开「系统媒体控件（SMTC）」上报

本程序不读播放器内存、不装插件，当前曲目全靠 Windows 自带的**系统媒体传输控件（SMTC）**
获取。播放器如果没把播放信息上报给系统，任务栏这边就**什么都收不到** —— 表现为歌词条
一直是「暂无歌词」或只有占位音符，换歌也没反应。

**先自查通不通**：播放中按一下键盘音量键（或 `Win + G` 打开 Xbox Game Bar）。
系统弹出的媒体浮层里能看到歌名、歌手、封面，SMTC 就是通的；浮层里没有这首歌，
就得去播放器里打开开关。

**开关位置**：一般在播放器的「设置 → 播放」里，名字类似「允许系统媒体控件显示歌曲信息」
「在系统媒体控制中心显示」「SMTC」。各客户端不同版本的文案和层级都有差异，
搜设置里的「系统媒体」或「媒体控件」最快。网易云音乐、QQ 音乐都有这个选项，
**部分版本默认是关闭的**；Spotify、Apple Music 和各浏览器默认就上报，不用设置
（浏览器默认在本程序的播放器黑名单里，需要跟踪的话去设置里取消屏蔽）。

## 功能

- **两种模式**
  - 任务栏模式（默认）：歌词嵌在任务栏里，左键按住可水平拖动
  - 浮动模式：置顶悬浮窗，可拖到屏幕任意位置
- **紧凑布局**：窗口宽度随当前行歌词自适应，不浪费任务栏空间（设置里可调最大宽度）
- **自动避让任务栏元素**（可开关，右键菜单可快捷切换）：UIA 枚举任务栏图标/托盘占用区域，
  窗口自动停靠在空档里（可指定左/右半边），宽度随可用空间伸缩，永不与其他元素重叠；
  开启后任务栏内拖动停用
- **逐字歌词**：卡拉 OK 式扫过——唱过的字纯白、没唱的暗灰（Win11 原生风格深浅变化）。
  酷狗 KRC 源；整首歌内匹配不上的行自动合成匀速扫过，效果始终一致
- **长歌词横向滚动**：超出最大宽度时不缩字号，跟随逐字进度平滑滚动
  （正在唱的位置保持在视口约 1/3 处），译文按比例同步滚动
- **双行显示**
  - 外文歌：上行原文 + 下行译文（或罗马音，可关）
  - 中文歌（无译文）：上行当前句 + 下行下一句，切行时走"传送带"动画
    （下一句补位到上一行）
- **切行动画**：旧句整行上滚淡出、新句从下方滚入，两行共用同一条缓动曲线与位移幅度
  （看起来像一条传送带在动，而不是两件各自为政的事）
- **悬停交互（对标 Lyricify）**
  - 悬停时浮现 Lyricify 式布局：封面 | ⏮ ▶ ⏭ | 歌名 + 歌手
  - 按钮列宽度展开动画，内容随之平滑右移；整窗淡白遮罩（同 Win11 任务栏图标）
  - 暂停超过 5 秒自动切换为「歌名 + 歌手」显示
- **专辑封面**：任务栏显示当前曲目封面（SMTC 缩略图，可开关）；
  无封面时显示音符占位，切歌即时同步，不误滤素色真实封面
- **制作信息过滤**：作词/作曲/编曲/制作人等 credits 行不占用任务栏
- **统一设置窗口**（Win11 Fluent 风格，跟随系统明暗）：右键菜单「打开设置…」，
  通用 / 歌词 / 外观 / 关于四个标签页，全部选项集中管理、应用即时生效
- **文字对齐**：居中 / 左对齐可选（配封面左对齐更整齐）
- **全屏自动隐藏**：前台有全屏应用（游戏/视频）时自动隐藏歌词（仅同显示器生效）
- **单曲循环检测**：歌曲重播时歌词自动归零
- **播放器黑名单**：默认不跟踪浏览器，可在设置中屏蔽/取消屏蔽当前播放器
- **检查更新**：启动时自动检查 [GitHub Releases](https://github.com/ChissaQAQ/TaskbarLyrics/releases)
  上的新版本（可关），发现后一键更新
  （下载 → 新进程接力替换 → 自动重启，配置保留）
- **歌词磁盘缓存**：抓到的歌词与逐字表按歌落盘（30 天 / 上限 400 首），
  切歌瞬间出词，断网时听过的歌照样有歌词（设置页可清空）
- **锁定位置**：锁定后鼠标穿透，完全不干扰操作（通过托盘菜单解锁）
- **系统托盘**：右下角托盘图标，菜单与歌词条右键一致
- 所有设置保存在 exe 同目录的 `config.json`，删除即恢复默认

## 原理

- 通过 Windows SMTC 读取网易云音乐上报的歌名 / 歌手 / 播放状态
  （网易云不上报播放进度，进度由本地计时推算；暂停/恢复上报延迟已做对称补偿）
- 歌词来源：网易云（含译文，主）→ QQ 音乐 → LRCLIB；
  歌手名宽松匹配（多歌手 / 繁简变体不漏歌），同首歌多版本时优先带译文的版本
- 逐字歌词来源：酷狗 KRC（解密后估计全局时间偏移，再按时间 + 文本归一化匹配挂载）
- WPF 分层窗口 `SetParent` 挂进任务栏（主屏 `Shell_TrayWnd` / 副屏 `Shell_SecondaryTrayWnd`），
  WinEvent 前台钩子即时重断言 z-order，ClearType 文字渲染

## 已知限制

- 拖动播放进度条无法感知，歌词会按原节奏继续（切歌后恢复准确）
- 软件启动前已开始播放的歌曲，从启动时刻开始计词，切下一首后完全同步
- 罗马音仅网易云歌词源支持；备选源（QQ / LRCLIB）没有罗马音数据

## 开发

```powershell
dotnet build wpf/TaskbarLyrics -c Release

# 打包前先停掉正在跑的实例，否则单文件 exe 被占用会报 MSB4018
Stop-Process -Name TaskbarLyrics -Force -ErrorAction SilentlyContinue
dotnet publish wpf/TaskbarLyrics -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o wpf/publish

# 歌词抓取控制台验证（不走缓存，直接打真实抓取结果和译文覆盖率）
wpf/publish/TaskbarLyrics.exe --lyrics-test "歌名" "歌手" [translation|romaji|off]

# 检查更新诊断：打印检查结果与 GitHub 剩余配额（「检查失败」时先跑这个）
wpf/publish/TaskbarLyrics.exe --update-test
```

代码在 `wpf/TaskbarLyrics/`，按职责分文件：

| 文件 | 职责 |
| --- | --- |
| `MainController.cs` | 50ms 主节拍：读 SMTC → 定位当前行 → 推给渲染层 |
| `SmtcListener.cs` | 系统媒体控件监听（网易云不报进度，本地计时推算） |
| `Lyrics.cs` | 三家曲库抓取、候选择优、译文/罗马音与逐字（KRC）对齐 |
| `LyricsCache.cs` | 歌词磁盘缓存（键里带 schema 版本，算法一改旧条目自动失联） |
| `OverlayWindow.xaml(.cs)` | 歌词渲染、逐字扫过、滚动与切行动画 |
| `NativeMethods.cs` | 任务栏挂靠（`SetParent`）、z-order 断言、点击穿透 |
| `TaskbarFreeSpace.cs` | UIA 枚举任务栏元素，算出可用空档（自动避让） |
| `SettingsWindow.xaml(.cs)` / `TrayIcon.cs` / `AppConfig.cs` | 设置界面、托盘菜单、配置存取 |
| `Updater.cs` | 检查 GitHub Releases 并接力替换自身 |

代码里的注释以「**为什么这么写**」为主（尤其是各种 Windows 平台坑的成因），
改动前建议先读相关注释 —— 很多看起来多余的写法都是踩过坑之后的结果。

## 反馈

用着有问题欢迎提 [Issue](https://github.com/ChissaQAQ/TaskbarLyrics/issues)。
提之前麻烦确认两件事，能省一轮来回：

1. SMTC 开关是通的（见上文自查方法）
2. 附上程序版本、Windows 版本、播放器版本；歌词相关的问题请**带上歌名和歌手**

## 许可与免责

代码以 [MIT](LICENSE) 许可发布。

歌词数据来自网易云音乐、QQ 音乐、LRCLIB、酷狗的公开接口，**版权归各自权利方所有**。
本程序只做展示、不存储分发歌词内容（本地缓存仅为减少重复请求，30 天后自动过期），
仅供个人学习与日常听歌使用。
