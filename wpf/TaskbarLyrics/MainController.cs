// 主装配（移植自 main.py 的 App 类）：
// SMTC 后台监听 + 歌词抓取（切歌触发、首选源失败 5s 自愈重试）+
// 50ms 歌词节拍（本地插值 → 逐字 Seek）+ 1.5s 贴合/全屏检查 + 菜单动作。
using System.IO;
using System.Net.Http;
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
    // 曲库登记的歌曲时长（毫秒，未知为 0）。网易云客户端的 SMTC 完全不上报 timeline
    // （实测 Position/EndTime 恒为 0、LastUpdatedTime 停在 1601 年），单曲循环检测
    // 只能靠这个补位。volatile int 而非 double：C# 不允许 volatile double
    private volatile int _fetchedDurMs;
    private double? _retryAt;
    private int _retryCount;
    // 这首歌当前上屏结果的排名（见 RankOf），-1 表示这首歌还没有抓取落地过。
    // 切歌（含改设置触发的重抓）时复位：首次抓取无条件接受，之后的自愈重试只接受严格更好的
    private int _shownRank = -1;
    // 切歌复位（OnState，SMTC 线程）与抓取结果落地（FetchLyrics 回调，线程池）互斥。
    // 不锁的话回调刚过完版本检查、切歌恰好在这时复位，上一首的歌词和排名就会写到新歌头上——
    // 排名一旦被写成「网易云完整结果」，新歌自己的抓取反倒会被当成「不够好」拒掉
    private readonly object _fetchLock = new();
    private double? _lastReplayAt; // 上次单曲循环归零的时刻（节流，理由见 UpdateLine）
    private string _coverSong = ""; // 已处理封面的歌曲
    private byte[]? _shownCoverBytes; // 已上屏的封面字节（引用比较，切歌即清、新字节即换）
    private double? _pausedSince;  // 暂停起始时刻（超时后改显歌曲信息）
    private double? _nullSince;    // SMTC 会话消失起始时刻（防抖：瞬断不隐藏窗口）
    private int _fetchVersion;   // 防止过期歌词回写

    // 当前正在显示的行（用于变化检测与设置即时生效重建）
    private string _shownOriginal = "\u0001"; // 初值保证首帧必刷新
    private string _shownTranslation = "";
    private bool _shownHasWords;

    // 无逐字数据时「合成匀速进度」的结果缓存（键：行文本 + 行时长）。
    // UpdateLine 每 50ms 就跑一轮，而合成结果只取决于这两样，不缓存的话同一句歌词
    // 显示几秒就要白切几十遍字，且绝大多数结果紧接着就在下面的变化检测里被丢掉。
    // 不必在切歌时失效：两首歌里凑巧同文本同时长的行，合成出来本来就该一样。
    private string _synthText = "";
    private int _synthDurMs;
    private IReadOnlyList<KaraokeWord>? _synthWords;
    private bool _lastPlaying;
    private bool _forceLine;
    private bool _forceMedia;    // 设置变更后强制重建歌曲信息层

    private volatile bool _quit;

    public MainController()
    {
        Cfg = AppConfig.Load();
    }

    private OverlayWindow Overlay => _overlay!;

    /// <summary>还活着的覆盖层（重建间隙返回 null，调用方跳过本轮刷新）。</summary>
    private OverlayWindow? Live => _overlay is { IsAlive: true } w ? w : null;

    /// <summary>窗口意外消失时立刻排一次重建（不等 1.5s 周期兜底）。</summary>
    private void HookOverlay(OverlayWindow w)
    {
        w.Closed += (_, _) =>
        {
            if (_quit) return; // 菜单退出走的也是 Close，别把自己救回来
            // Closed 处理中窗口还在拆，重建推到下一个消息循环轮次
            Application.Current?.Dispatcher.BeginInvoke(RebuildOverlay);
        };
    }

    /// <summary>重建覆盖层窗口并挂回任务栏。
    ///
    /// 覆盖层是 Shell_TrayWnd 的子窗口，explorer 重启（崩溃自恢复、系统更新、
    /// 手动重启资源管理器——都是常规事件）会销毁任务栏，我们的子窗口被连带销毁。
    /// 托盘图标是 WinForms NotifyIcon，它自己处理 TaskbarCreated 广播能自愈；
    /// WPF 窗口不能——而 TaskbarCreated 只发给顶层窗口，我们既收不到、
    /// 收到时窗口也已经不存在了。所以自愈路径是「窗口没了 → 等任务栏就绪 → 重建」。
    /// 配合 App.xaml 的 ShutdownMode=OnExplicitShutdown（否则窗口一关进程就退）。</summary>
    private void RebuildOverlay()
    {
        if (_quit || Live != null) return; // 已经被上一条路径救回来了
        // 任务栏还没起好（explorer 正在启动）：这轮不建，留给 1.5s 周期再试。
        // 没有宿主可挂的窗口会以普通顶层窗形式闪在屏幕中间
        if (Cfg.Mode == "taskbar" && NativeMethods.ResolveTaskbar(Cfg.Monitor).Tray == IntPtr.Zero)
            return;

        _overlay = new OverlayWindow(this);
        HookOverlay(_overlay);
        _overlay.Show();
        _overlay.Dock();
        _overlay.UpdateFullscreen();
        _overlay.SetPlaying(_lastPlaying);
        // 状态重新灌进新窗口：封面按字节引用去重，清掉记录让监听侧下一轮重新推送；
        // _forceLine/_forceMedia 让下面这次节拍用当前歌词重建两层视觉
        _coverSong = "";
        _shownCoverBytes = null;
        _shownOriginal = "";
        _forceLine = true;
        _forceMedia = true;
        OnLyricsTick();
    }

    public void Run()
    {
        TaskbarFreeSpace.Start(); // 任务栏空档的 UIA 枚举跑在后台线程，UI 线程只读快照
        _overlay = new OverlayWindow(this);
        HookOverlay(_overlay);
        _overlay.Show();
        _tray = new TrayIcon(this);
        // 自启路径自愈：exe 挪位置后注册表里的旧路径会静默失效，启动时幂等刷新
        try { if (Autostart.IsEnabled()) Autostart.SetEnabled(true); }
        catch (SystemException) { /* 注册表写失败不致命 */ }

        Updater.Cleanup(); // 清掉上次更新留下的临时新 exe
        // 启动时自动检查更新（发现新版本 → 右键菜单出现更新入口）。
        // 距上次成功检查不足 6 小时就跳过：新版一天也发不了几个，而 GitHub 匿名接口
        // 每小时只有 60 次配额、还是按出口 IP 共享的，一天开关十几次机器就能耗掉一截；
        // 真撞上限流反而查不到更新。用户手动点「检查更新」不受这里限制
        if (Cfg.UpdateCheck
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Cfg.LastUpdateCheck > 6 * 3600)
            _ = Task.Run(async () =>
            {
                try { await CheckForUpdateAsync(); }
                catch { /* 启动检查失败静默（失败原因已在 CheckForUpdateAsync 里落日志） */ }
            });

        _lyricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _lyricsTimer.Tick += (_, _) => OnLyricsTick();
        _lyricsTimer.Start();

        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _dockTimer.Tick += (_, _) =>
        {
            // 覆盖层没了（explorer 重启把宿主任务栏连带我们的子窗口一起销毁）：
            // 先把它造回来，本轮不做贴合
            if (_overlay == null || !_overlay.IsAlive)
            {
                RebuildOverlay();
                return;
            }
            Overlay.Dock();
            Overlay.UpdateFullscreen();
            // 顺带盯一眼系统主题：Windows 换深浅色（含日落自动切换）时不给普通窗口
            // 发任何我们收得到的通知，只能轮询。读一个注册表值的开销远小于这轮已经做的
            // 任务栏贴合，1.5 秒的延迟用户也察觉不到
            if (Theme.Refresh()) ApplyTheme();
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
                Application.Current?.Dispatcher.BeginInvoke(() => Live?.SetCover(null));
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
            lock (_fetchLock)
            {
                if (state.Key != _songKey)
                {
                    _retryCount = 0;
                    _shownRank = -1; // 新歌的首次抓取无论好坏都要接受
                    _lastReplayAt = null; // 切歌后重置单曲循环检测
                    // 切歌立即清掉旧歌词：新歌词抓到前若继续用旧表定位新歌进度，
                    // 会定位不到行而把窗口当成“无内容”隐藏（消失好几句才回来）
                    _lines = null;
                    _karaoke = new Dictionary<int, List<KaraokeWord>>();
                    _fetchedDurMs = 0; // 上一首的时长不能用来判新歌的重播
                }
                _songKey = state.Key;
                _retryAt = null;
                FetchLyrics(state);
            }
        }
    }

    // 抓取结果的好坏排名：无歌词 0 < 歌手对不上的备胎（可能是同名翻唱）1 < 备选源（QQ/LRCLIB，没有译文）2
    // < 网易云但打了折扣（首选候选请求失败落到次选、或逐字请求失败）3 < 网易云完整结果 4。
    // 备胎排在备选源之下：宁可没有译文，也要原唱的时间轴——重试拿到 QQ 的原唱就该顶掉网易云的翻唱
    private const int RankBest = 4;

    private static int RankOf(Lyrics.FetchResult r) =>
        r.Lines is not { Count: > 0 } ? 0
        : r.ArtistMismatch ? 1
        : r.Source != "_fetch_netease" ? 2
        : r.Degraded ? 3
        : RankBest;

    private void FetchLyrics(PlaybackState state)
    {
        var version = Interlocked.Increment(ref _fetchVersion);
        var (title, artist, durationS) = (state.Title, state.Artist, state.DurationS);
        var withKaraoke = Cfg.Karaoke;
        var secondLine = Cfg.SecondLine;
        Task.Run(async () =>
        {
            Lyrics.FetchResult r;
            try
            {
                r = await Lyrics.FetchAsync(title, artist, durationS, withKaraoke, secondLine);
            }
            catch
            {
                // 网络异常时退化为显示歌名
                r = new Lyrics.FetchResult(null, new Dictionary<int, List<KaraokeWord>>(), "", 0);
            }
            lock (_fetchLock)
            {
                if (version != _fetchVersion || _quit) return; // 已有更新的抓取
                // 重试的结果未必更好：首选源依旧失败时它拿回的是 null 或同样的备选源结果，
                // 原先无条件覆盖，会把已经在显示的 QQ 歌词顶成「只显示歌名」。
                // 所以一首歌的首次抓取总是接受，之后只接受严格更好的
                var rank = RankOf(r);
                if (_shownRank < 0 || rank > _shownRank)
                {
                    _shownRank = rank;
                    _lines = r.Lines;
                    _karaoke = r.Karaoke;
                    _fetchedDurMs = (int)(r.SongDurationS * 1000);
                }
                // 还没拿到网易云的完整结果就再试。例外是网易云正常答复了「没有这首歌」、
                // 而备选源已经有歌词在显示（比如只在 QQ 上架的歌）：再试也只会拿回同样的结果，
                // 原先每次播放都要白跑满 3 轮、十几个请求
                var primaryHopeless = r.PrimaryNotFound && _shownRank > 0;
                if (_shownRank < RankBest && !primaryHopeless && _retryCount < 2)
                {
                    _retryCount++;
                    _retryAt = Clock.Now + 5;
                }
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
            // 只过滤「近纯白、几乎无细节、且没有颜色」的占位图（网易云无封面时返回的白图）；
            // 低对比度的真实封面（深色/素色）保留显示。色度这道闸是后加的：
            // 实测ヘクとパスカル「fish in the pool」的封面是一张淡蓝色插画，缩到 8x8
            // 后灰度均值 192、标准差 7.2，前两道闸全中，被当成占位图扔了；而白占位图是
            // 纯灰的（色度 2.9），这张有 59.9
            var (stddev, mean, chroma) = CoverStats(img);
            if (!(stddev < 8 && mean > 180 && chroma < 12)) cover = img;
        }
        catch
        {
            cover = null;
        }
        Live?.SetCover(cover); // null 时显示音符占位，不再留空白
    }

    /// <summary>缩放到 8x8 算灰度的 (标准差, 均值)（对应 PIL 的 ImageStat），
    /// 外加色度：逐像素 RGB 最大分量减最小分量的平均，纯灰图为 0。</summary>
    private static (double Stddev, double Mean, double Chroma) CoverStats(BitmapSource img)
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
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var color = new byte[64 * 4];
        bgra.CopyPixels(color, 8 * 4, 0);
        var chroma = 0.0;
        for (var p = 0; p < color.Length; p += 4)
        {
            var (b, g, r) = (color[p], color[p + 1], color[p + 2]);
            chroma += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        }
        return (Math.Sqrt(varSum / pixels.Length), mean, chroma / 64);
    }

    // ---- 歌词节拍（UI 线程 50ms）----

    private void OnLyricsTick()
    {
        if (Live == null) return; // 窗口正在重建（explorer 重启），本轮没有可刷的界面
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
            // 单曲循环检测：插值进度超过歌曲时长 → 判定为重播，计时归位。
            // 必须用未截断进度：截断进度永不超过 DurationS，
            // 长尾奏（>20s）歌曲会在第一次播放的尾奏里被误判成重播
            var unclampedMs = (int)(state.CurrentPositionUnclampedS() * 1000);
            // 歌曲时长：SMTC 优先，网易云那种压根不上报 timeline 的用曲库登记的时长补。
            // 时长来自曲库时容差放宽些——匹配到的可能是同一首歌的另一版本，差几秒很常见
            var songDurS = state.DurationS > 0 ? state.DurationS : _fetchedDurMs / 1000.0;
            var tolMs = state.DurationS > 0 ? 2000 : 5000;
            // 一无所知时才退回启发式。余量原先是 20s，尾奏长过它的歌（长器乐尾奏、
            // Live 版）一进尾奏就被判成重播、进度归零、显示回开头那句——主人反馈的
            // 「快结束时又显示开头歌词」就是这条。放宽到 45s：宁可漏判一次重播
            // （歌词多停在末句几秒，之后靠下面的余数归位自动追上），也不能错判
            var assumedEndMs = songDurS > 0 ? (int)(songDurS * 1000) : lines[^1].Ms + 45000;
            // 归零必须节流，但不能只允许一次：
            // 播放器把 DurationS 报小（或尾奏长于上报时长）时，归位后下一轮读到的真实
            // 进度依旧超时，不节流就会每 0.5s 归位一次、歌词永远卡在第一行；可只归位一次
            // 又会让单曲循环从第三遍起彻底失效——标记一旦置位就只有切歌才复位。
            // 闸门取半首歌长：真正的重播得走完整首才会再次触发，绝不会被这道闸挡住。
            var replayGuardS = songDurS > 0 ? Math.Max(20.0, songDurS * 0.5) : 20.0;
            if ((_lastReplayAt == null || Clock.Now - _lastReplayAt.Value > replayGuardS)
                && unclampedMs > assumedEndMs + tolMs)
            {
                _lastReplayAt = Clock.Now;
                // 归位到「已经进入这一遍多少」，而不是一律归零：判定天生滞后
                // （得等插值越过歌曲末尾才知道），归零会把这段滞后量当成永久错位
                // 摊到整首歌上——第二遍从头到尾都比人声慢一截。
                // 必须对歌长取模而不是只减一遍：超出不止一遍时（比如歌词晚到，插值早已
                // 循环过好几遍），只减一遍的进度仍在末尾之后，歌词会一直卡在最后一句，
                // 要等闸门放行（半首歌之后）才再往回减一遍
                var overMs = assumedEndMs > 0 ? unclampedMs % assumedEndMs : 0;
                state.BasePositionS = overMs / 1000.0;
                state.BaseTime = Clock.Now;
                posMs = Math.Max(0, overMs + Cfg.OffsetMs);
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
                        if (_synthWords == null || _synthDurMs != lineDur || _synthText != orig)
                        {
                            _synthWords = Lyrics.SynthesizeWords(orig, lineDur);
                            _synthText = orig;
                            _synthDurMs = lineDur;
                        }
                        words = _synthWords;
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

    private bool _cfgSaveWarned;

    /// <summary>存配置。连退路（%AppData%）都写不进去时提示一次：
    /// 不提示的话用户只会发现「设置每次重启都变回去」，完全无从下手。
    /// 只提示一次——拖动窗口每次松手都会走到这里。</summary>
    public void SaveCfg()
    {
        if (Cfg.Save() || _cfgSaveWarned) return;
        _cfgSaveWarned = true;
        MessageBox.Show(
            $"设置无法保存到：\n{AppConfig.ConfigPath}\n\n"
            + "当前修改在本次运行内有效，但重启后会丢失。\n"
            + "请把程序放到有写入权限的目录（如桌面或用户目录）再试（详情见 error.log）。",
            "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

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

    // 歌词偏移的快捷调整范围与设置页保持一致（±3s）：
    // 菜单能调到设置页校验不接受的值，会变成「一打开设置就报错、不改都存不了」
    private const int MaxOffsetMs = 3000;

    /// <summary>当前偏移的菜单文案。正值＝歌词提前（posMs 加得多，查到更靠后的行）。</summary>
    public string OffsetLabel() =>
        Cfg.OffsetMs == 0 ? "当前无偏移" : $"当前 {Cfg.OffsetMs / 1000.0:+0.0;-0.0}s";

    /// <summary>菜单快调歌词偏移（deltaMs 正数＝更提前）。到边界就停在边界。</summary>
    public void NudgeOffset(int deltaMs) => SetOffset(Cfg.OffsetMs + deltaMs);

    public void SetOffset(int ms)
    {
        var clamped = Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);
        if (clamped == Cfg.OffsetMs) return;
        Cfg.OffsetMs = clamped;
        SaveCfg();
        // 偏移一变，当前该显示的可能已经是另一句了，立刻重算而不是等下一拍
        _forceLine = true;
        OnLyricsTick();
    }

    // ---- 检查更新 ----

    /// <summary>发现的新版本（右键菜单/设置页据此显示更新入口）。</summary>
    public ReleaseInfo? PendingUpdate { get; private set; }
    private bool _updating;

    /// <summary>正在下载更新（托盘菜单据此把更新入口置灰）。</summary>
    public bool IsUpdating => _updating;

    /// <summary>检查更新，返回状态文案；有更新时写入 PendingUpdate。</summary>
    public async Task<string> CheckForUpdateAsync()
    {
        try
        {
            var (latest, hasUpdate) = await Updater.CheckLatestAsync();
            // 记下成功时刻：启动检查据此节流（见构造里的启动检查处）
            Cfg.LastUpdateCheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Cfg.Save(); // 存不进去只会让下次启动照旧检查一遍，无需理会失败
            if (latest == null) return "发布版本里没有可用的安装包";
            PendingUpdate = hasUpdate ? latest : null;
            return hasUpdate ? $"发现新版本 {latest.Tag}" : $"已是最新版本（v{Updater.CurrentVersion}）";
        }
        catch (UpdateCheckException ex)
        {
            // 原因明确（限流 / 404 / 5xx），直接把话讲给用户，不必记日志
            return ex.Message;
        }
        catch (Exception ex)
        {
            // 原先这里空吞：用户看到「检查失败」，error.log 里一个字都没有，无从下手
            Log.Error("update-check", ex);
            return ex is HttpRequestException or TaskCanceledException
                ? "连不上 GitHub（网络不通或被拦截），请稍后重试"
                : "检查失败，请稍后重试";
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
        catch (Exception ex)
        {
            _updating = false;
            // 用户主动点的更新失败了，是罕见且需要追查的事，记一条现场
            // （检查更新那边同样会记，但原因讲得清的失败——限流、404——只回文案不记）
            Log.Error("update-download", ex);
            return ex is InvalidDataException
                ? "下载内容异常（更新源可能返回了错误页）"
                : "下载失败，请稍后重试";
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
        if (Live == null) return; // 仅设置模式（--settings）或窗口重建中：配置已保存，无界面可刷新
        if (refetchLyrics)
            _songKey = ""; // 强制重新抓歌词（第二行/逐字内容变化）
        Overlay.Dock();
        Overlay.UpdateFullscreen();
        Overlay.ApplyThemeChrome();
        _forceLine = true; // 用新外观重建当前行
        _forceMedia = true; // 同步重建歌曲信息层（字体/颜色/对齐可能变了）
        OnLyricsTick();
    }

    /// <summary>让当前歌曲重新联网抓一遍歌词（设置页清空歌词缓存后用）。
    /// 走的是切歌那条路：_songKey 一清空，下一轮 SMTC 回调（≤0.5s）就把它当新歌——
    /// 清掉旧歌词、复位重试计数与排名，再发起抓取；缓存已经清了，这次必然走网络。
    /// 返回是否真有一首歌在重抓，设置页据此换个说法。false 有两种：眼下没有播放会话；
    /// 或本进程根本没在听歌（--settings 模式，没跑 Run）——正在放歌的主实例是另一个进程，
    /// 它手里这首的歌词已在内存里，要到切歌才会重新抓。</summary>
    public bool RefetchLyrics()
    {
        if (_overlay == null) return false;
        _songKey = "";
        return _state != null;
    }

    /// <summary>系统深浅色变了：重刷装饰件配色并重建行视觉。
    ///
    /// 必须重建行：颜色是建行时冻结进 Brush 的（冻结是为了让渲染层共享资源），
    /// 不重建的话画面上一个字都不会变。手动指定了颜色的用户只需要刷装饰件——
    /// 文字用他自己挑的颜色，不该被系统主题改掉。</summary>
    private void ApplyTheme()
    {
        if (Live == null) return;
        Overlay.ApplyThemeChrome();
        if (Cfg.TextColorMode == "custom") return;
        _forceLine = true;
        _forceMedia = true;
        OnLyricsTick();
    }

    public void Control(string action) => SmtcListener.Control(action);

    public string CurrentSourceId() => _state?.SourceId ?? "";

    /// <summary>设置页「屏蔽当前播放器」按钮：返回 (播放器 id, 按钮文字)，无会话时 id 为空。
    /// 调用方要把 id 跟文字一起记下、点击时原样交给 <see cref="ToggleBlock"/>：
    /// 点击时再取「当前播放器」的话，文字一过期（窗口开着时换了播放器，或刚屏蔽的那个
    /// 已被换下去）就会屏蔽掉另一个播放器，跟按钮上写的对不上。</summary>
    public (string SourceId, string Label) CurrentBlockToggle()
    {
        var sid = CurrentSourceId();
        if (sid.Length == 0) return ("", "屏蔽当前播放器（无会话）");
        return (sid, IsBlocked(sid) ? $"取消屏蔽『{sid}』" : $"屏蔽『{sid}』");
    }

    /// <summary>与 SmtcListener.PickSession 同一套规则：名单里的关键词是来源 id 的子串（均按小写比）即算屏蔽。</summary>
    private bool IsBlocked(string sid)
    {
        var s = sid.ToLowerInvariant();
        return Cfg.PlayerBlocklist.Any(b => s.Contains(b.ToLowerInvariant()));
    }

    /// <summary>切换某个播放器的屏蔽状态。解除时去掉所有命中它的关键词——
    /// 只去第一条的话，名单里恰好还有另一条也命中它时，点了等于没解除。</summary>
    public void ToggleBlock(string sourceId)
    {
        if (sourceId.Length == 0) return;
        var s = sourceId.ToLowerInvariant();
        SetBlocklist(IsBlocked(sourceId)
            ? Cfg.PlayerBlocklist.Where(b => !s.Contains(b.ToLowerInvariant())).ToList()
            : Cfg.PlayerBlocklist.Append(s).ToList());
    }

    /// <summary>设置页「已屏蔽的播放器」逐条解除。被屏蔽的播放器永远当不上「当前播放器」，
    /// 上面那个按钮对它无能为力（浏览器默认就在名单里），只能从这里放出来。</summary>
    public void Unblock(string keyword) =>
        SetBlocklist(Cfg.PlayerBlocklist.Where(b => b != keyword).ToList());

    /// <summary>整份换新而不是原地增删：SMTC 监听线程每轮都要枚举这份名单，
    /// 启动时检查更新写时间戳也会在后台线程序列化它，原地改会撞上「集合已修改」。
    /// 监听侧每轮现取名单，下一轮轮询（≤0.5s）即生效，不必等「应用」。</summary>
    private void SetBlocklist(List<string> list)
    {
        Cfg.PlayerBlocklist = list;
        SaveCfg();
    }

    private SettingsWindow? _settingsWin;

    /// <summary>打开设置窗口（单例）。ShowDialog 的模态只挡 WPF 窗口，
    /// 托盘图标的宿主是 WinForms 的隐藏窗口、照样响应右键，于是「打开设置…」
    /// 能在已有窗口之上再开一个：两个窗口各改各的配置、各自保存，后关的那个覆盖先关的。
    /// 已经开着就把它激活到前台，而不是再造一个。</summary>
    public void OpenSettings()
    {
        if (_settingsWin is { IsLoaded: true } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var win = new SettingsWindow(this);
        _settingsWin = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_settingsWin, win)) _settingsWin = null; };
        win.ShowDialog();
    }

    public void ShowContextMenu() => _tray?.ShowMenuAtCursor();

    public void Quit()
    {
        _quit = true; // 必须先置：Closed 处理里靠它区分「主动退出」与「被 explorer 干掉」
        _cts.Cancel();
        TaskbarFreeSpace.Stop();
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
        TaskbarFreeSpace.Stop(); // 只置标志不 join：不能卡在一次可能挂住的 UIA 调用上
        _tray?.Dispose();
        _cts.Dispose();
    }
}
