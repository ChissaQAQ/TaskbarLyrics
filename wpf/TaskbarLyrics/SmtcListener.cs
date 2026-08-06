// 监听 Windows SMTC，获取网易云音乐（或其他播放器）的播放状态（移植自 smtc_listener.py）。
//
// 后台任务中运行 PollAsync：订阅会话的播放信息/媒体属性事件（暂停、恢复、
// 切歌立即推送），并以 0.5s 轮询兜底。通过回调把 PlaybackState 推给主程序。
// SMTC 的进度只在切歌/暂停/拖动时刷新，播放中由 CurrentPositionS() 本地插值。
using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;
// 类型名太长，且要在元组签名里出现
using MediaProps = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties;

namespace TaskbarLyrics;

/// <summary>单调时钟（对应 Python time.monotonic）。</summary>
public static class Clock
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static double Now => Sw.Elapsed.TotalSeconds;
}

public sealed class PlaybackState
{
    public string Title = "";
    public string Artist = "";
    public double DurationS;
    public bool Playing;
    public string SourceId = "";        // SMTC 来源（如 cloudmusic.exe）
    public byte[]? CoverBytes;          // 专辑封面（JPEG/PNG 字节）
    // 进度基准：BasePositionS 是 BaseTime 时刻的播放进度
    public double BasePositionS;
    public double BaseTime;
    // SMTC 上报的原始进度（网易云恒为 0，用于判断是否有真实进度更新）
    public double RawPositionS;

    public string Key => $"{Title}｜{Artist}";

    public double CurrentPositionS()
    {
        var pos = Playing ? BasePositionS + (Clock.Now - BaseTime) : BasePositionS;
        if (DurationS > 0) pos = Math.Min(pos, DurationS);
        return Math.Max(pos, 0.0);
    }

    /// <summary>未按时长截断的插值进度（单曲循环重播检测用：
    /// 截断后的进度永不超过 DurationS，无法区分「长尾奏」与「真的重播了」）。</summary>
    public double CurrentPositionUnclampedS()
    {
        var pos = Playing ? BasePositionS + (Clock.Now - BaseTime) : BasePositionS;
        return Math.Max(pos, 0.0);
    }

    /// <summary>同一首歌且 SMTC 进度没变化（网易云不上报进度）时，沿用本地计时。</summary>
    public void MergeFrom(PlaybackState? prev)
    {
        if (prev != null && prev.Key == Key && RawPositionS == prev.RawPositionS)
        {
            BasePositionS = prev.CurrentPositionS();
            BaseTime = Clock.Now;
        }
        // 否则视为切歌/拖动进度条：从 raw 进度重新计时（base 即 raw）
    }
}

public static class SmtcListener
{
    public const double PollIntervalS = 0.5;   // 兜底轮询；事件到达会立即刷新
    public const double SmtcLatencyS = 0.45;   // 网易云上报暂停/恢复的固有延迟（实测均值 0.42~0.56s）
    private const int MaxThumbTries = 8;       // 每首歌读封面的次数上限（约覆盖切歌后 4s）

    // ---- 播放控制 ----

    private static readonly ConcurrentQueue<string> ControlQueue = new();

    /// <summary>向当前会话发送播放控制：prev | play_pause | next（任意线程可调用）。</summary>
    public static void Control(string action) => ControlQueue.Enqueue(action);

    // ---- 会话选择 ----

    /// <summary>会话当前是否正在播放（跨进程读取，失败按「没在播」算）。</summary>
    private static bool IsPlayingNow(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo().PlaybackStatus
                   == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return false; }
    }

    /// <summary>source: auto（优先正在播放的，同为播放态时偏向网易云）| netease（仅网易云）| others（排除网易云）。
    /// blocklist: 不跟踪的来源关键词列表（匹配 SourceAppUserModelId 小写子串）。</summary>
    private static GlobalSystemMediaTransportControlsSession? PickSession(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
        string source, IReadOnlyList<string>? blocklist)
    {
        if (sessions.Count == 0) return null;
        var blocked = (blocklist ?? Array.Empty<string>()).Select(b => b.ToLowerInvariant()).ToList();

        bool Usable(GlobalSystemMediaTransportControlsSession s)
        {
            var sid = (s.SourceAppUserModelId ?? "").ToLowerInvariant();
            return !blocked.Any(b => sid.Contains(b));
        }

        static bool IsNetease(GlobalSystemMediaTransportControlsSession s) =>
            (s.SourceAppUserModelId ?? "").ToLowerInvariant().Contains("cloudmusic");

        // 显式指定「仅网易云」时不过黑名单：用户已经点名要这个源，
        // 再让黑名单否决就成了「怎么设都不工作」的死局
        if (source == "netease")
            return sessions.FirstOrDefault(IsNetease);
        if (source == "others")
        {
            var others = sessions.Where(s => !IsNetease(s) && Usable(s)).ToList();
            return others.FirstOrDefault(IsPlayingNow) ?? others.FirstOrDefault();
        }
        // auto：正在播放的优先，同为播放态时偏向网易云（歌词源最全）；都没在播时才看谁在前。
        // 网易云必须一并过 Usable——它原先跳过检查，导致菜单里「屏蔽 cloudmusic.exe」
        // 点了毫无反应。也不能让它无条件夺魁：网易云暂停着、别的播放器正放歌时，
        // 任务栏显示的会是网易云那首停着的旧歌。
        var usable = sessions.Where(Usable).ToList();
        var playing = usable.Where(IsPlayingNow).ToList();
        return playing.FirstOrDefault(IsNetease)
               ?? playing.FirstOrDefault()
               ?? usable.FirstOrDefault(IsNetease)
               ?? usable.FirstOrDefault();
    }

    /// <summary>读一次会话快照。props 一并返回给调用方复用：
    /// 缩略图也在 props 上，重新取一遍等于白花一次跨进程 WinRT 调用。</summary>
    private static async Task<(PlaybackState State, MediaProps Props)> ReadFromSessionAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var info = session.GetPlaybackInfo();
        var positionS = timeline.Position.TotalSeconds;
        var state = new PlaybackState
        {
            Title = props.Title ?? "",
            Artist = props.Artist ?? "",
            DurationS = timeline.EndTime.TotalSeconds,
            Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            SourceId = session.SourceAppUserModelId ?? "",
            BasePositionS = positionS,
            BaseTime = Clock.Now,
            RawPositionS = positionS,
        };
        return (state, props);
    }

    private static async Task<byte[]?> ReadThumbnailAsync(MediaProps props)
    {
        try
        {
            if (props.Thumbnail == null) return null;
            using var stream = await props.Thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 16 * 1024 * 1024) return null;
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var buf = new byte[stream.Size];
            reader.ReadBytes(buf);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    private static async Task DrainControlAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        bool? playing = null; // 首条 play_pause 时现读，同一批后续命令按本地翻转
        while (session != null && ControlQueue.TryDequeue(out var action))
        {
            try
            {
                switch (action)
                {
                    case "prev": await session.TrySkipPreviousAsync(); break;
                    case "next": await session.TrySkipNextAsync(); break;
                    case "play_pause":
                        // 播放态必须现读，不能用上一轮轮询的值：那个值最多滞后 0.5s，
                        // 用户在这期间从播放器窗口或键盘媒体键改过状态，就会发出反向命令
                        // （明明在放却又发一次 Play）。
                        // 同一批队列里的后续命令仍按本地翻转——播放器响应命令有延迟，
                        // 紧接着再读一次拿到的还是旧状态，连按两次会退化成两条相同命令。
                        playing ??= IsPlayingNow(session);
                        if (playing.Value) await session.TryPauseAsync();
                        else await session.TryPlayAsync();
                        playing = !playing.Value;
                        break;
                }
            }
            catch
            {
                // 播放器不支持该操作时忽略
            }
        }
    }

    /// <summary>事件驱动 + 兜底轮询，回调 onState(PlaybackState?)。
    /// getSource/getBlocklist 每次轮询时取值（设置改动即时生效）。</summary>
    public static async Task PollAsync(Action<PlaybackState?> onState, CancellationToken stop,
        Func<string> getSource, Func<List<string>> getBlocklist)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        GlobalSystemMediaTransportControlsSession? session = null;
        string sessionKey = "";   // 已订阅事件的会话标识（AUMID）
        TaskCompletionSource? wake = null;

        void Wake() => wake?.TrySetResult();

        void Unsubscribe()
        {
            if (session == null) return;
            try
            {
                session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }
            catch { /* 会话已失效 */ }
        }

        void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => Wake();
        void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) => Wake();

        PlaybackState? prev = null;
        string thumbKey = "";      // 已读封面的歌曲
        byte[]? thumbBytes = null; // 当前歌曲的封面字节
        double thumbRefreshUntil = 0;  // 切歌后的封面重读窗口（SMTC 缩略图常滞后于标题）
        var thumbTries = 0;            // 本首歌已尝试读封面的次数（上限见 MaxThumbTries）
        try
        {
            while (!stop.IsCancellationRequested)
            {
                PlaybackState? state;
                try
                {
                    var newSession = PickSession(manager.GetSessions(), getSource(), getBlocklist());
                    // RCW 身份每次枚举都可能变，按 AUMID 判断是否真的换了会话
                    var newKey = newSession?.SourceAppUserModelId ?? "";
                    if (newSession == null || session == null || newKey != sessionKey)
                    {
                        Unsubscribe();
                        session = newSession;
                        sessionKey = newKey;
                        if (session != null)
                        {
                            // 暂停/恢复 → PlaybackInfoChanged；切歌 → MediaPropertiesChanged
                            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                        }
                    }
                    await DrainControlAsync(session);
                    MediaProps? props = null;
                    state = null;
                    if (session != null)
                    {
                        var read = await ReadFromSessionAsync(session);
                        state = read.State;
                        props = read.Props;
                    }
                    // 封面：切歌后 1.5s 内每轮重读（SMTC 缩略图常滞后于标题更新，
                    // 只读一次会拿到旧图或空图）；窗口外仍允许有限次重试
                    if (state != null && props != null)
                    {
                        if (state.Key != thumbKey)
                        {
                            thumbKey = state.Key;
                            thumbBytes = null;
                            thumbRefreshUntil = Clock.Now + 1.5;
                            thumbTries = 0;
                        }
                        // 重试要封顶：有的播放器给了缩略图引用但读出来是空流，
                        // 「为空就重试」等于每 0.5s 白开一次跨进程流、一直开到这首歌结束
                        if (Clock.Now < thumbRefreshUntil || (thumbBytes == null && thumbTries < MaxThumbTries))
                        {
                            thumbTries++;
                            var b = await ReadThumbnailAsync(props);
                            // 内容没变（长度一致）就不换引用：主程序按引用去重上屏，
                            // 每次轮询都换新数组会让封面在切歌后 1.5s 内重复解码重绘（闪烁）
                            if (b != null && (thumbBytes == null || b.Length != thumbBytes.Length))
                                thumbBytes = b;
                        }
                        state.CoverBytes = thumbBytes;
                    }
                }
                catch
                {
                    state = null; // SMTC 偶发异常不应杀死监听循环
                }

                if (state != null)
                {
                    state.MergeFrom(prev);
                    // 暂停/恢复检测有固有延迟：对称补偿，消除逐次累积的进度漂移。
                    // 恢复时把计时起点提前 L；暂停时回退多算的 L。
                    // 无论真实音频何时停/起，同歌内正负相抵，不再累积误差。
                    if (prev != null && prev.Key == state.Key && prev.Playing != state.Playing)
                    {
                        if (state.Playing) state.BaseTime -= SmtcLatencyS;
                        else state.BasePositionS = Math.Max(0.0, state.BasePositionS - SmtcLatencyS);
                    }
                }
                prev = state;
                onState(state);

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = tcs;
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(PollIntervalS)));
                wake = null;
            }
        }
        finally
        {
            Unsubscribe();
        }
    }
}
