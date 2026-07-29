// 监听 Windows SMTC，获取网易云音乐（或其他播放器）的播放状态（移植自 smtc_listener.py）。
//
// 后台任务中运行 PollAsync：订阅会话的播放信息/媒体属性事件（暂停、恢复、
// 切歌立即推送），并以 0.5s 轮询兜底。通过回调把 PlaybackState 推给主程序。
// SMTC 的进度只在切歌/暂停/拖动时刷新，播放中由 CurrentPositionS() 本地插值。
using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;

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

    // ---- 播放控制 ----

    private static readonly ConcurrentQueue<string> ControlQueue = new();

    /// <summary>向当前会话发送播放控制：prev | play_pause | next（任意线程可调用）。</summary>
    public static void Control(string action) => ControlQueue.Enqueue(action);

    // ---- 会话选择 ----

    /// <summary>source: auto（优先网易云，兜底任意播放器）| netease（仅网易云）| others（排除网易云）。
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

        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus ==
                         GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        if (source == "netease")
            return sessions.FirstOrDefault(IsNetease);
        if (source == "others")
        {
            var others = sessions.Where(s => !IsNetease(s) && Usable(s)).ToList();
            return others.FirstOrDefault(IsPlaying) ?? others.FirstOrDefault();
        }
        // auto
        return sessions.FirstOrDefault(IsNetease)
               ?? sessions.FirstOrDefault(s => Usable(s) && IsPlaying(s))
               ?? sessions.FirstOrDefault(Usable);
    }

    private static async Task<PlaybackState> ReadFromSessionAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var info = session.GetPlaybackInfo();
        var positionS = timeline.Position.TotalSeconds;
        return new PlaybackState
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
    }

    private static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
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

    private static async Task DrainControlAsync(
        GlobalSystemMediaTransportControlsSession? session, bool lastPlaying)
    {
        while (session != null && ControlQueue.TryDequeue(out var action))
        {
            try
            {
                switch (action)
                {
                    case "prev": await session.TrySkipPreviousAsync(); break;
                    case "next": await session.TrySkipNextAsync(); break;
                    case "play_pause":
                        // 每条命令后翻转本地状态：连按两次应是 暂停→播放，而不是两次相同命令
                        if (lastPlaying) await session.TryPauseAsync();
                        else await session.TryPlayAsync();
                        lastPlaying = !lastPlaying;
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
                    await DrainControlAsync(session, prev?.Playing ?? false);
                    state = session != null ? await ReadFromSessionAsync(session) : null;
                    // 封面：切歌后 1.5s 内每轮重读（SMTC 缩略图常滞后于标题更新，
                    // 只读一次会拿到旧图或空图）；窗口外为空才重试，避免重复读流
                    if (state != null && session != null)
                    {
                        if (state.Key != thumbKey)
                        {
                            thumbKey = state.Key;
                            thumbBytes = null;
                            thumbRefreshUntil = Clock.Now + 1.5;
                        }
                        if (thumbBytes == null || Clock.Now < thumbRefreshUntil)
                        {
                            var b = await ReadThumbnailAsync(session);
                            if (b != null) thumbBytes = b;
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
