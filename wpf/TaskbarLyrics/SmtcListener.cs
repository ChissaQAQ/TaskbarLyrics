// 监听 Windows SMTC，获取网易云音乐（或其他播放器）的播放状态（移植自 smtc_listener.py）。
//
// 后台任务中运行 PollAsync：订阅会话的播放信息/媒体属性事件（暂停、恢复、
// 切歌立即推送），并以 0.5s 轮询兜底。通过回调把 PlaybackState 推给主程序。
// SMTC 的进度只在切歌/暂停/拖动时刷新，播放中由 CurrentPositionS() 本地插值。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Control;
using Windows.Storage.Streams;
// 类型名太长，且要在元组签名里出现
using MediaProps = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties;

namespace TaskbarLyrics;

/// <summary>单调时钟（对应 Python time.monotonic），不含系统睡眠/休眠的时长。</summary>
public static class Clock
{
    // 播放进度是「基准 + 流逝时间」推算出来的，时钟必须跟着音频一起停。
    // 原先用的 Stopwatch 底层是 QPC，微软文档写明它把睡眠、休眠、connected standby
    // 的时间都算在内：合盖一晚醒来，推算进度凭空多出一整夜，歌词直接跳到末尾，
    // 主程序还会据此误判成单曲循环重播去归位。
    // 没在轮询里靠「两次轮询间隔过长」去猜睡眠，是因为猜不准也来不及：界面节拍
    // 醒来后往往先于轮询跑，已经拿着多出一夜的进度去做重播归位了；而轮询本身因为
    // 跨进程调用慢、进程被挂起等原因拖长时，音乐其实一直在放，冻结进度反而会落后。
    // unbiased interrupt time 只在系统处于工作状态时走，从根上把睡眠排除掉；
    // Precise 版直接读计时硬件，精度与 QPC 相当，逐字进度不会因此变粗。
    // Precise 版不在 kernel32.dll 里（实测 EntryPointNotFoundException），是 KernelBase
    // 经 api-ms-win-core-realtime-l1-1-1 导出的；取不到（Windows 10 之前）再退到 kernel32
    // 的非 Precise 版（Win7 起就有，精度随时钟中断，进程已 timeBeginPeriod(1)，约 1ms），
    // 两个都取不到才退回 Stopwatch。
    [DllImport("api-ms-win-core-realtime-l1-1-1.dll", EntryPoint = "QueryUnbiasedInterruptTimePrecise")]
    private static extern void QueryUnbiasedInterruptTimePrecise(out ulong unbiasedTime);

    [DllImport("kernel32.dll", EntryPoint = "QueryUnbiasedInterruptTime")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static readonly int Mode; // 0 = Stopwatch，1 = Precise，2 = 非 Precise
    private static readonly ulong Origin; // 让读数和原来一样从进程启动时的 0 起算

    static Clock()
    {
        // 这里一律吞掉——静态构造抛出去会变成 TypeInitializationException，
        // 所有用到 Clock 的地方都跟着挂
        try
        {
            QueryUnbiasedInterruptTimePrecise(out Origin);
            Mode = 1;
            return;
        }
        catch { }
        try
        {
            if (QueryUnbiasedInterruptTime(out Origin)) { Mode = 2; return; }
        }
        catch { }
        try { Log.Note("clock", "取不到 unbiased interrupt time，退回 Stopwatch（会计入系统睡眠时长）"); }
        catch { }
    }

    public static double Now
    {
        get
        {
            ulong t;
            if (Mode == 1) QueryUnbiasedInterruptTimePrecise(out t);
            else if (Mode == 2) QueryUnbiasedInterruptTime(out t);
            else return Sw.Elapsed.TotalSeconds;
            return (t - Origin) / 1e7; // 单位是 100ns
        }
    }
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
    // 进度基准按来源保存的有效期（见 PollAsync 里的 bases）。在播的基准离开越久越不可信
    // （期间可能暂停过而没人看见），只留一小会儿；停着的基准不随时间漂移，可以留得久些
    private const double PlayingBaseKeepS = 30;
    private const double PausedBaseKeepS = 600;
    private const double RetryMinS = 1;        // 获取 SMTC manager 失败后的退避：1s 起步、逐次翻倍
    private const double RetryMaxS = 30;       // 退避上限
    private const int ManagerMaxFails = 3;     // manager 连续调用失败几次就丢掉重新获取

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
        // manager 在循环里获取：原先放在 try 外面，RequestAsync 抛一次异常（开机自启时
        // 系统媒体服务还没就绪之类），整个监听任务就此结束，再也不会自愈
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        var acquireFails = 0;     // 连续没能拿到可用 manager 的次数（决定退避时长）
        var managerFails = 0;     // manager.GetSessions 连续失败次数
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

        // 进度基准按来源（AUMID）分别保存，值是 (该来源最近一次的状态, 读到它的时刻)。
        // 原先只有一个 prev 且每轮无条件覆盖：SMTC 读失败一次（state 为 null）、会话瞬断、
        // auto 模式临时切去别的播放器，基准都会丢，网易云这种 raw 恒为 0 的就从头算起。
        // 存的就是发给主程序的那个对象：主程序判出单曲循环重播时会直接改它的基准，
        // 下一轮得接着改过的值推算。条目数不超过见过的播放器个数，过期的不必清理，
        // 查的时候当它不存在、同源下次写入时覆盖即可
        var bases = new Dictionary<string, (PlaybackState State, double SeenAt)>();

        // 换走的会话若确实停着，把它的基准冻结在此刻。auto 模式只有网易云不在播时才会
        // 换去别的播放器，而换走那一轮读的是新会话，旧会话「停了」这件事没人看见——
        // 不冻结的话它的基准一直按在播推算，等它恢复播放切回来时，停了多久进度就超前多久。
        // 状态读不出来（会话已销毁）不冻结：多半是瞬断，接着推算更接近实情
        void FreezeIfPaused(GlobalSystemMediaTransportControlsSession old, string sid)
        {
            if (!bases.TryGetValue(sid, out var entry) || !entry.State.Playing) return;
            // 已过期的在播基准读取处本就不再合并：这里也不冻结、不续命，免得按旧基准推算出虚高进度
            if (Clock.Now - entry.SeenAt > PlayingBaseKeepS) return;
            try
            {
                if (old.GetPlaybackInfo().PlaybackStatus
                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return;
            }
            catch { return; }
            var s = entry.State;
            bases[sid] = (new PlaybackState
            {
                Title = s.Title,
                Artist = s.Artist,
                DurationS = s.DurationS,
                SourceId = s.SourceId,
                RawPositionS = s.RawPositionS,
                // 与正常观察到暂停时一样回退固有延迟，切回来恢复播放时会对称补回
                BasePositionS = Math.Max(0.0, s.CurrentPositionS() - SmtcLatencyS),
                BaseTime = Clock.Now,
                Playing = false,
            }, Clock.Now);
        }

        string thumbKey = "";      // 已读封面的歌曲
        byte[]? thumbBytes = null; // 当前歌曲的封面字节
        double thumbRefreshUntil = 0;  // 切歌后的封面重读窗口（SMTC 缩略图常滞后于标题）
        var thumbTries = 0;            // 本首歌已尝试读封面的次数（上限见 MaxThumbTries）
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (manager == null)
                {
                    // 上次没拿到、或拿到的用不了：先退避（1s 起步、逐次翻倍、封顶 30s）。
                    // 等待挂在 stop 上，退出时不必等满
                    if (acquireFails > 0)
                    {
                        var delayS = Math.Min(RetryMaxS, RetryMinS * Math.Pow(2, acquireFails - 1));
                        try { await Task.Delay(TimeSpan.FromSeconds(delayS), stop); }
                        catch (OperationCanceledException) { break; }
                    }
                    try
                    {
                        manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    }
                    catch (Exception ex)
                    {
                        // 一段连续失败只记第一次：退避到上限后每 30s 记一条只是噪音
                        if (acquireFails == 0) Log.Error("smtc", ex);
                        acquireFails++;
                        continue;
                    }
                }

                PlaybackState? state;
                try
                {
                    IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
                    try
                    {
                        sessions = manager.GetSessions();
                        managerFails = 0;
                        acquireFails = 0; // manager 真能用了才算恢复，退避从头计
                    }
                    catch (Exception ex)
                    {
                        // manager 本身失效后（系统媒体服务重启等）每次调用都会抛，光靠外层兜底
                        // 只会原地空转、永远 state = null。连续失败几次就连同从它拿到的会话
                        // 一起丢掉，下一轮重新获取；同样计入退避，免得新拿到的照样不能用时
                        // 每隔一两秒就重来一遍
                        if (++managerFails >= ManagerMaxFails)
                        {
                            if (acquireFails == 0) Log.Error("smtc", ex);
                            acquireFails++;
                            managerFails = 0;
                            manager = null;
                            Unsubscribe();
                            session = null;
                            sessionKey = "";
                        }
                        throw;
                    }
                    var newSession = PickSession(sessions, getSource(), getBlocklist());
                    // RCW 身份每次枚举都可能变，按 AUMID 判断是否真的换了会话
                    var newKey = newSession?.SourceAppUserModelId ?? "";
                    if (newSession == null || session == null || newKey != sessionKey)
                    {
                        if (session != null) FreezeIfPaused(session, sessionKey);
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
                    // 同一来源上次留下的基准；过期的当作没有，换了歌的由 MergeFrom 按 Key 丢弃
                    PlaybackState? prev = null;
                    if (bases.TryGetValue(state.SourceId, out var saved)
                        && Clock.Now - saved.SeenAt <= (saved.State.Playing ? PlayingBaseKeepS : PausedBaseKeepS))
                        prev = saved.State;
                    state.MergeFrom(prev);
                    // 暂停/恢复检测有固有延迟：对称补偿，消除逐次累积的进度漂移。
                    // 恢复时把计时起点提前 L；暂停时回退多算的 L。
                    // 无论真实音频何时停/起，同歌内正负相抵，不再累积误差。
                    if (prev != null && prev.Key == state.Key && prev.Playing != state.Playing)
                    {
                        if (state.Playing) state.BaseTime -= SmtcLatencyS;
                        else state.BasePositionS = Math.Max(0.0, state.BasePositionS - SmtcLatencyS);
                    }
                    // 标题为空（切歌过渡、会话半初始化）不覆盖基准：主程序把它当「没有会话」，
                    // 基准也不该因此丢掉，否则同一首歌再读回来又从头算起
                    if (state.Title.Length > 0) bases[state.SourceId] = (state, Clock.Now);
                }
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
