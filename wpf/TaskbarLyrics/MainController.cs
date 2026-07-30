// 主装配（移植自 main.py 的 App 类）：
// SMTC 后台监听 + 歌词抓取（切歌触发、首选源失败 5s 自愈重试）+
// 50ms 歌词节拍（本地插值 → 逐字 Seek）+ 1.5s 贴合/全屏检查 + 菜单动作。
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TaskbarLyrics;

public sealed class MainController : IDisposable
{
    public AppConfig Cfg { get; }

    private OverlayWindow? _overlay;
    private TrayIcon? _tray;
    private readonly CancellationTokenSource _cts = new();
    private DispatcherTimer? _lyricsTimer;  // 50ms：按本地计时刷新当前歌词行/逐字
    private DispatcherTimer? _dockTimer;    // 1.5s：周期贴合任务栏 + 全屏检测

    // ---- 播放状态与歌词（_state/_lines/_karaoke 跨线程读写，用 volatile 引用）----
    private volatile PlaybackState? _state;
    private volatile List<LyricLine>? _lines;
    private volatile Dictionary<int, List<KaraokeWord>> _karaoke = new();
    private volatile string _songKey = "";
    private double? _retryAt;
    private int _retryCount;
    private bool _replayed;      // 单曲循环重播检测标记
    private string _coverSong = ""; // 已处理封面的歌曲
    private byte[]? _shownCoverBytes; // 已上屏的封面字节（引用比较，切歌即清、新字节即换）
    private double? _pausedSince;  // 暂停起始时刻（超时后改显歌曲信息）
    private double? _nullSince;    // SMTC 会话消失起始时刻（防抖：瞬断不隐藏窗口）
    private int _fetchVersion;   // 防止过期歌词回写

    // 当前正在显示的行（用于变化检测与设置即时生效重建）
    private string _shownOriginal = "\u0001"; // 初值保证首帧必刷新
    private string _shownTranslation = "";
    private bool _shownHasWords;
    private bool _lastPlaying;
    private bool _forceLine;
    private bool _forceMedia;    // 设置变更后强制重建歌曲信息层

    private volatile bool _quit;

    public MainController()
    {
        Cfg = AppConfig.Load();
    }

    private OverlayWindow Overlay => _overlay!;

    public void Run()
    {
        _overlay = new OverlayWindow(this);
        _overlay.Show();
        _tray = new TrayIcon(this);

        // 自启路径自愈：exe 挪位置后注册表里的旧路径会静默失效，启动时幂等刷新
        try { if (Autostart.IsEnabled()) Autostart.SetEnabled(true); }
        catch (SystemException) { /* 注册表写失败不致命 */ }

        Updater.Cleanup(); // 清掉上次更新留下的临时新 exe
        if (Cfg.UpdateCheck) // 启动时自动检查更新（发现新版本 → 右键菜单出现更新入口）
            _ = Task.Run(async () =>
            {
                try { await CheckForUpdateAsync(); }
                catch { /* 启动检查失败静默 */ }
            });

        _lyricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _lyricsTimer.Tick += (_, _) => OnLyricsTick();
        _lyricsTimer.Start();

        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _dockTimer.Tick += (_, _) =>
        {
            Overlay.Dock();
            Overlay.UpdateFullscreen();
        };
        _dockTimer.Start();

        var token = _cts.Token;
        Task.Run(() => SmtcListener.PollAsync(OnState, token,
            () => Cfg.PlayerSource, () => Cfg.PlayerBlocklist), token);
    }

    // ---- SMTC 回调（后台线程，只更新数据）----

    private void OnState(PlaybackState? state)
    {
        if (_quit) return;
        if (state == null || state.Title.Length == 0)
        {
            _state = null;
            return;
        }
        _state = state;

        // 专辑封面：切歌立即清掉旧封面，监听到新字节后立即上屏
        // （SMTC 缩略图常在切歌后晚到几百毫秒，监听侧会重读，这里按字节引用去重）
        if (state.Key != _coverSong)
        {
            _coverSong = state.Key;
            _shownCoverBytes = null;
            if (!_quit)
                Application.Current?.Dispatcher.BeginInvoke(() => Overlay.SetCover(null));
        }
        if (state.CoverBytes != null && !ReferenceEquals(state.CoverBytes, _shownCoverBytes))
        {
            _shownCoverBytes = state.CoverBytes;
            var bytes = state.CoverBytes;
            if (!_quit)
                Application.Current?.Dispatcher.BeginInvoke(() => DecodeAndSetCover(bytes));
        }

        // 首选源（网易云）偶发失败时会落到无译文的备选源，且结果缓存整首歌——
        // 标记 5 秒后重试（每首歌最多 2 次），让译文自愈
        var needRetry = _retryAt.HasValue && Clock.Now >= _retryAt && state.Key == _songKey;
        if (state.Key != _songKey || needRetry)
        {
            if (state.Key != _songKey)
            {
                _retryCount = 0;
                _replayed = false; // 切歌后重置单曲循环检测
                // 切歌立即清掉旧歌词：新歌词抓到前若继续用旧表定位新歌进度，
                // 会定位不到行而把窗口当成“无内容”隐藏（消失好几句才回来）
                _lines = null;
                _karaoke = new Dictionary<int, List<KaraokeWord>>();
            }
            _songKey = state.Key;
            _retryAt = null;
            FetchLyrics(state);
        }
    }

    private void FetchLyrics(PlaybackState state)
    {
        var version = Interlocked.Increment(ref _fetchVersion);
        var (title, artist, durationS) = (state.Title, state.Artist, state.DurationS);
        var withKaraoke = Cfg.Karaoke;
        var secondLine = Cfg.SecondLine;
        Task.Run(async () =>
        {
            List<LyricLine>? lines;
            Dictionary<int, List<KaraokeWord>> karaoke;
            string source;
            try
            {
                (lines, karaoke, source) = await Lyrics.FetchAsync(
                    title, artist, durationS, withKaraoke, secondLine);
            }
            catch
            {
                (lines, karaoke, source) = (null, new Dictionary<int, List<KaraokeWord>>(), ""); // 网络异常时退化为显示歌名
            }
            if (version != _fetchVersion || _quit) return; // 已有更新的抓取
            _lines = lines;
            _karaoke = karaoke;
            if ((lines == null || source != "_fetch_netease") && _retryCount < 2)
            {
                _retryCount++;
                _retryAt = Clock.Now + 5;
            }
        });
    }

    private void DecodeAndSetCover(byte[] bytes)
    {
        BitmapSource? cover = null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            // 只过滤「近纯白且几乎无细节」的占位图（网易云无封面时返回的白图）；
            // 低对比度的真实封面（深色/素色）保留显示
            var (stddev, mean) = GrayStats(img);
            if (!(stddev < 8 && mean > 180)) cover = img;
        }
        catch
        {
            cover = null;
        }
        Overlay.SetCover(cover); // null 时显示音符占位，不再留空白
    }

    /// <summary>缩放到 8x8 灰度图算 (标准差, 均值)（对应 PIL 的 ImageStat）。</summary>
    private static (double Stddev, double Mean) GrayStats(BitmapSource img)
    {
        var scaled = new TransformedBitmap(img,
            new ScaleTransform(8.0 / img.PixelWidth, 8.0 / img.PixelHeight));
        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        var pixels = new byte[64];
        gray.CopyPixels(pixels, 8, 0);
        var mean = 0.0;
        foreach (var b in pixels) mean += b;
        mean /= pixels.Length;
        var varSum = 0.0;
        foreach (var b in pixels) varSum += (b - mean) * (b - mean);
        return (Math.Sqrt(varSum / pixels.Length), mean);
    }

    // ---- 歌词节拍（UI 线程 50ms）----

    private void OnLyricsTick()
    {
        var state = _state;
        if (state == null)
        {
            // SMTC 会话瞬断（几秒内恢复）不闪藏窗口；消失超过 2s 才清空隐藏
            _nullSince ??= Clock.Now;
            if (Clock.Now - _nullSince.Value > 2.0)
            {
                _pausedSince = null;
                Overlay.SetInfoMode(false);
                Overlay.SetMedia("", "");
                if (_shownOriginal != "")
                {
                    _shownOriginal = "";
                    _shownTranslation = "";
                    Overlay.SetLine("", "", null, 0, false);
                }
            }
            return;
        }
        _nullSince = null;

        // 暂停计时：超时后改显歌曲信息（悬停时由覆盖层自行切换）
        if (state.Playing) _pausedSince = null;
        else _pausedSince ??= Clock.Now;
        Overlay.SetMedia(state.Title, state.Artist, _forceMedia);
        _forceMedia = false;
        Overlay.SetInfoMode(_pausedSince.HasValue && Clock.Now - _pausedSince.Value > 5);

        string original;
        string translation;
        IReadOnlyList<KaraokeWord>? words = null;
        double lineElapsed = 0;
        var nextLineMode = false; // 第二行显示的是“下一句”（无译文/罗马音时）
        var lines = _lines;
        if (lines is { Count: > 0 })
        {
            var posMs = Math.Max(0, (int)(state.CurrentPositionS() * 1000) + Cfg.OffsetMs);
            // 单曲循环检测：插值进度超过歌曲时长 → 判定为重播，计时归零。
            // 必须用未截断进度：截断进度永不超过 DurationS，
            // 长尾奏（>20s）歌曲会在第一次播放的尾奏里被误判成重播
            var unclampedMs = (int)(state.CurrentPositionUnclampedS() * 1000);
            if (!_replayed && (state.DurationS > 0
                    ? unclampedMs > (int)(state.DurationS * 1000) + 2000
                    : posMs > lines[^1].Ms + 20000)) // 时长未知时退回旧启发式
            {
                _replayed = true;
                state.BasePositionS = 0.0;
                state.BaseTime = Clock.Now;
                posMs = Math.Max(0, Cfg.OffsetMs);
            }
            var (index, orig, trans) = Lyrics.CurrentLine(lines, posMs);
            if (index < 0)
            {
                // 还没到第一句（前奏）：上行歌名、下行歌手两行显示，与闲置信息一致
                original = state.Title;
                translation = state.Artist;
            }
            else
            {
                original = orig;
                translation = trans;
                // 无译文/罗马音时（如中文歌），第二行显示下一句歌词
                if (translation.Length == 0 && Cfg.SecondLine != "off"
                    && index + 1 < lines.Count)
                {
                    translation = lines[index + 1].Text;
                    nextLineMode = true;
                }
                var lineStart = lines[index].Ms;
                lineElapsed = Math.Max(0, posMs - lineStart);
                if (orig.Length > 0 && Cfg.Karaoke)
                {
                    if (_karaoke.TryGetValue(lines[index].Ms, out var w))
                    {
                        words = w;
                    }
                    else
                    {
                        // 本行（或整首歌）没匹配到逐字：按行时长合成匀速进度，
                        // 长歌词跟随它从头滚到尾（不做来回走马灯），扫过效果整首歌一致
                        var lineDur = index + 1 < lines.Count
                            ? Math.Clamp(lines[index + 1].Ms - lines[index].Ms, 800, 10000)
                            : 5000;
                        words = Lyrics.SynthesizeWords(orig, lineDur);
                    }
                }
            }
        }
        else
        {
            // 无歌词可用：同样上行歌名、下行歌手两行显示
            original = state.Title;
            translation = state.Artist;
        }

        var hasWords = words != null;
        if (_forceLine || original != _shownOriginal || translation != _shownTranslation
            || hasWords != _shownHasWords)
        {
            _forceLine = false;
            _shownOriginal = original;
            _shownTranslation = translation;
            _shownHasWords = hasWords;
            Overlay.SetLine(original, translation, words, lineElapsed, state.Playing, nextLineMode);
        }
        else
        {
            Overlay.SyncProgress(lineElapsed, state.Playing);
        }
        if (state.Playing != _lastPlaying)
        {
            _lastPlaying = state.Playing;
            Overlay.SetPlaying(state.Playing);
        }
    }

    // ---- 菜单动作 ----

    public void SaveCfg() => Cfg.Save();

    public void SetMode(string mode)
    {
        Cfg.Mode = mode;
        SaveCfg();
        ApplySettings(refetchLyrics: false);
    }

    public void SetLocked(bool locked)
    {
        Cfg.Locked = locked;
        SaveCfg();
        _overlay?.SetLocked(locked); // --settings 模式下无覆盖层
    }

    /// <summary>右键菜单切换自动避让任务栏元素。</summary>
    public void ToggleAutoPosition()
    {
        Cfg.AutoPosition = !Cfg.AutoPosition;
        SaveCfg();
        ApplySettings(refetchLyrics: false);
    }

    // ---- 检查更新 ----

    /// <summary>发现的新版本（右键菜单/设置页据此显示更新入口）。</summary>
    public ReleaseInfo? PendingUpdate { get; private set; }
    private bool _updating;

    /// <summary>检查更新，返回状态文案；有更新时写入 PendingUpdate。</summary>
    public async Task<string> CheckForUpdateAsync()
    {
        try
        {
            var (latest, hasUpdate) = await Updater.CheckLatestAsync();
            if (latest == null) return "检查失败，请稍后重试";
            PendingUpdate = hasUpdate ? latest : null;
            return hasUpdate ? $"发现新版本 {latest.Tag}" : $"已是最新版本（v{Updater.CurrentVersion}）";
        }
        catch
        {
            return "检查失败，请稍后重试";
        }
    }

    /// <summary>下载并接力替换更新：成功后本进程退出、新版自动启动。</summary>
    public async Task<string> DownloadAndApplyAsync()
    {
        if (PendingUpdate == null || _updating) return "";
        _updating = true;
        try
        {
            var path = await Updater.DownloadAsync(PendingUpdate.AssetUrl);
            Updater.StartApplyAndExit(path, Quit); // 里面会退出本进程
            return "";
        }
        catch
        {
            _updating = false;
            return "下载失败，请稍后重试";
        }
    }

    public void SetAutostart(bool enabled)
    {
        try { Autostart.SetEnabled(enabled); }
        catch (SystemException) { /* 注册表写失败不致命 */ }
    }

    /// <summary>设置窗口应用后：外观/布局即时生效；必要时重抓歌词。</summary>
    public void ApplySettings(bool refetchLyrics)
    {
        if (_overlay == null) return; // 仅设置模式（--settings）：配置已保存，无界面可刷新
        if (refetchLyrics)
            _songKey = ""; // 强制重新抓歌词（第二行/逐字内容变化）
        Overlay.Dock();
        Overlay.UpdateFullscreen();
        _forceLine = true; // 用新外观重建当前行
        _forceMedia = true; // 同步重建歌曲信息层（字体/颜色/对齐可能变了）
        OnLyricsTick();
    }

    public void Control(string action) => SmtcListener.Control(action);

    public string CurrentSourceId() => _state?.SourceId ?? "";

    public string BlockCurrentLabel()
    {
        var sid = CurrentSourceId();
        if (sid.Length == 0) return "屏蔽当前播放器（无会话）";
        var blocked = Cfg.PlayerBlocklist.Any(b => sid.ToLowerInvariant().Contains(b));
        return blocked ? $"取消屏蔽『{sid}』" : $"屏蔽『{sid}』";
    }

    public void ToggleBlockCurrent()
    {
        var sid = CurrentSourceId();
        if (sid.Length == 0) return;
        var existing = Cfg.PlayerBlocklist.FirstOrDefault(b => sid.ToLowerInvariant().Contains(b));
        if (existing != null) Cfg.PlayerBlocklist.Remove(existing);
        else Cfg.PlayerBlocklist.Add(sid.ToLowerInvariant());
        SaveCfg();
    }

    public void OpenSettings()
    {
        var win = new SettingsWindow(this);
        win.ShowDialog();
    }

    public void ShowContextMenu() => _tray?.ShowMenuAtCursor();

    public void Quit()
    {
        _quit = true;
        _cts.Cancel();
        _tray?.Dispose();
        _overlay?.Close();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _quit = true;
        _cts.Cancel();
        _lyricsTimer?.Stop();
        _dockTimer?.Stop();
        _tray?.Dispose();
        _cts.Dispose();
    }
}
