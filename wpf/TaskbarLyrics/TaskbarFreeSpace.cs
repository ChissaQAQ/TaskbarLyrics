// 任务栏空档计算（自动避让功能）：
// 用 UIA 枚举任务栏上的元素矩形（开始/搜索/任务视图/应用图标/时钟等），
// 合并成占用区间后求水平空档；选择能放下窗口且离当前位置最近的空档。
//
// UIA 枚举跑在专用后台线程上，UI 线程只读它算好的快照。
// 这一条是硬性要求，不是优化：AutomationElement 的每次属性读取都是同步跨进程
// 调用 explorer，实测枚举一遍任务栏（约 30 个元素）中位 72ms、最差 170ms——
// 而这是 explorer 空闲时的数字。放在 UI 线程上就是每秒白丢 70ms（50ms 歌词节拍
// 直接停摆），更要命的是我们的窗口是 Shell_TrayWnd 的子窗口：我们等 explorer 回应
// UIA，explorer 又可能正在向我们的子窗口发同步消息，双向等待就是整个任务栏卡死。
// 微软的 UI Automation 线程指南也明确要求客户端不要在 UI 线程调用 UIA。
using System.Threading;
using System.Windows.Automation;

namespace TaskbarLyrics;

public static class TaskbarFreeSpace
{
    /// <summary>后台线程算好的一次快照（不可变，整体替换发布给 UI 线程）。</summary>
    private sealed class Snapshot
    {
        public IntPtr Tray;
        public int TrayWidth;
        public List<(int L, int R)> Occupied = new();
    }

    // 枚举频率必须做退避，这是内存问题不是性能问题：
    // UIA 客户端每跑一遍任务栏都会在 uiautomationcore.dll 里漏掉约 10.8 KB 原生内存
    // （隔离实验：1500 轮涨 32MB，GC.Collect + WaitForPendingFinalizers 一个字节都收不回，
    //  漏点在系统组件内部，调用方没有任何可 Dispose 的东西）。
    // 原先固定 1 次/秒 = 38MB/小时 = 910MB/天，连续挂机一天就把自己撑到 OOM 退出。
    // 任务栏布局其实几乎不变（开关应用、托盘图标增减才变），所以「结果和上一轮一样」
    // 就把周期翻倍，一路退到 60s；真有变化时由 Nudge()/SetTargets() 立刻拉回 1s。
    private const int FastRefreshMs = 1000;   // 刚发生变化后的节奏
    private const int MaxRefreshMs = 60_000;  // 长期静止时的兜底心跳（约 15MB/天）
    // 工作线程超过这么久没开始新一轮，就是卡在某次 UIA 调用里出不来了。
    // UIA 是同步跨进程调用 explorer 且没有超时：explorer 的 UI 线程一被别的东西堵住，
    // 这个调用能挂上几分钟甚至再也不返回，而快照就此冻结在挂住的那一刻。
    // 判据要留足余量——静止期本来就要 MaxRefreshMs 才醒一次，不能把正常心跳当成卡死
    private const int StuckAfterMs = MaxRefreshMs + 30_000;
    // 最多让新线程接手几次。挂住的那次 UIA 调用不可取消（同步跨进程 COM，
    // .NET Core 也没有 Thread.Abort），旧线程只能挂着自生自灭，每条都占着资源，
    // 所以得封顶：真到了这一步已经是 explorer 侧的问题，再堆线程也换不回结果
    private const int MaxRevives = 3;
    // 判定卡死前要连续确认几轮（Watchdog 由 Dock 驱动，1.5s 一轮）。
    // 唯一目的是排掉睡眠唤醒这类假象，不必等太久
    private const int StuckConfirms = 3;

    private static volatile Snapshot? _snap;
    private static volatile bool _stop;
    private static volatile int _idleMs = FastRefreshMs;
    private static int _started;
    // 工作线程的代号。卡死换线程时递增：挂住的旧线程哪天返回了，一看代号变了就自行退场，
    // 不许再发布快照（它手上那份结果已经是几分钟前的现场了），也不许再碰 Wake
    private static volatile int _gen;
    private static int _revives;
    // 这两个时间戳是这套东西唯一的可观测性来源，用 TickCount64（单调、不受改系统时间影响）：
    // _beatMs 是每轮循环开始的时刻（不动 = 线程卡在 UIA 里），
    // _lastOkMs 是最近一次真的拿到结果的时刻（不动 = 枚举还在跑但从此不产出结果）。
    // 分两个才能把「卡死」和「持续失败」区分开——前者能靠换线程自愈，后者不能
    private static long _beatMs;
    private static long _lastOkMs;
    private static bool _staleLogged;
    // 卡死确认用（只由 UI 线程在 Watchdog 里读写，无并发）
    private static long _stuckBeat;
    private static int _stuckConfirms;
    // 目标句柄变化时立刻重测，不等下一个周期：启动首帧和 explorer 重启后
    // 如果还捧着旧快照（或没有快照），窗口会先按默认位置摆出来再跳到空档里
    private static readonly ManualResetEventSlim Wake = new(false);
    // UI 侧写入、后台线程读取的目标句柄（IntPtr 的读写在 64 位上是原子的）
    private static volatile IntPtr _wantTray;
    private static volatile IntPtr _wantExclude;

    /// <summary>启动后台枚举线程（幂等）。</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        StartWorker(_gen);
    }

    private static void StartWorker(int gen)
    {
        // IsBackground：进程退出时直接丢弃，不必等一次可能挂住的 UIA 调用返回
        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"TaskbarFreeSpace#{gen}" };
        // UIA 客户端在 MTA 上工作（线程池默认也是 MTA），显式声明避免隐式 STA 带来的封送开销
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start(gen);
    }

    public static void Stop()
    {
        _stop = true;
        Wake.Set();
    }

    /// <summary>告知后台线程当前要测量的任务栏与需排除的自身窗口（每次 Dock 调用，极轻）。</summary>
    public static void SetTargets(IntPtr trayHwnd, IntPtr excludeHwnd)
    {
        var changed = trayHwnd != _wantTray || excludeHwnd != _wantExclude;
        _wantTray = trayHwnd;
        _wantExclude = excludeHwnd;
        if (!changed) return;
        // 目标换了（含关掉自动避让时置零、再打开时置回来）：之前那份「最近产出时刻」
        // 是对旧目标的，留着会让看护逻辑把刚开始的空转误判成停产
        Volatile.Write(ref _lastOkMs, Environment.TickCount64);
        Nudge();
    }

    /// <summary>外部信号「任务栏可能变了」：恢复快节奏并立刻重测。
    /// 前台窗口切换时调用——开关/最小化应用是任务栏按钮增减的绝大多数来源，
    /// 有了这个信号，静止期就可以放心退避到 60s 心跳。</summary>
    public static void Nudge()
    {
        _idleMs = FastRefreshMs;
        Wake.Set();
    }

    private static void WorkerLoop(object? state)
    {
        var myGen = (int)state!;
        while (!_stop)
        {
            // 被接手过就立刻退场：这条线程手上的结果早已过期，而且它一旦去 Reset(Wake)
            // 就会把接手线程等着的唤醒信号吞掉
            if (myGen != _gen) return;
            Volatile.Write(ref _beatMs, Environment.TickCount64);
            Wake.Reset(); // 先清再测：测量期间到来的变化会让下面的 Wait 立刻返回
            var tray = _wantTray;
            if (tray != IntPtr.Zero)
            {
                try { Measure(tray, _wantExclude, myGen); }
                catch { /* UIA 整体失败：保留上一份快照，调用方沿用旧空档不跳位 */ }
            }
            Wake.Wait(_idleMs);
        }
    }

    /// <summary>枚举一遍任务栏，算出占用区间并发布快照。</summary>
    private static void Measure(IntPtr tray, IntPtr exclude, int myGen)
    {
        if (!NativeMethods.GetClientRect(tray, out var rc) || rc.Right <= 0) return;
        NativeMethods.GetWindowRect(tray, out var wrc);
        var raw = new List<(int L, int R)>();
        // UIA 的 NativeWindowHandle 是 int：64 位下句柄本就是截断后塞进去的，
        // 这里也必须按截断比较。原先在循环里用 IntPtr.ToInt32()，句柄一旦超出 int 范围
        // 就抛 OverflowException，被外层 catch 吞掉后自己的窗口反倒被算成占用区、把自己挤走
        Collect(AutomationElement.FromHandle(tray), rc.Right, wrc.Left,
            unchecked((int)exclude.ToInt64()), raw);
        if (raw.Count == 0) return; // 查询没结果时不覆盖上一份成功的快照

        raw.Sort((a, b) => a.L.CompareTo(b.L));
        var merged = new List<(int L, int R)>();
        foreach (var (l, r) in raw)
        {
            if (merged.Count > 0 && l <= merged[^1].R + 2)
                merged[^1] = (merged[^1].L, Math.Max(merged[^1].R, r));
            else
                merged.Add((l, r));
        }

        // 结果和上一轮完全一致 → 任务栏静止，周期翻倍（详见上面 FastRefreshMs 处的说明）。
        // 快照本身不必重新发布，省掉调用方那边无意义的引用变更
        var prev = _snap;
        if (prev != null && prev.Tray == tray && prev.TrayWidth == rc.Right
            && Same(prev.Occupied, merged))
        {
            _idleMs = Math.Min(MaxRefreshMs, _idleMs * 2);
            MarkOk(myGen);
            return;
        }
        if (myGen != _gen) return; // 挂了很久才返回的旧线程，不许拿过期结果覆盖接手线程的快照
        _idleMs = FastRefreshMs; // 有变化：回到快节奏，紧跟后续的连续变化
        _snap = new Snapshot { Tray = tray, TrayWidth = rc.Right, Occupied = merged };
        MarkOk(myGen);
    }

    /// <summary>记下「这一轮真的拿到了结果」。静止期不发布新快照，所以快照自身的新鲜度
    /// 说明不了问题，得单独记——否则没法把「任务栏没变」和「枚举从此不产出」区分开。</summary>
    private static void MarkOk(int myGen)
    {
        if (myGen != _gen) return;
        Volatile.Write(ref _lastOkMs, Environment.TickCount64);
        _staleLogged = false; // 恢复了：下次再停产还要留一条日志
    }

    private static bool Same(List<(int L, int R)> a, List<(int L, int R)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].L != b[i].L || a[i].R != b[i].R) return false;
        return true;
    }

    // Win10 时代的任务栏容器窗口类。Win11 把任务栏整个换成了 XAML，这些窗口还在，
    // 但报的矩形是过时的（主屏 ReBarWindow32，副屏 WorkerW + 它里面的 MSTaskListWClass，
    // 实测这三个在副屏上报的都是同一个 385..830——那是主屏 Win10 布局的老矩形）
    private static readonly HashSet<string> LegacyShellClasses =
        new() { "ReBarWindow32", "WorkerW", "MSTaskListWClass" };
    // Win11 的第一个版本是 build 22000
    private static readonly bool IsWin11 = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>递归收集占用矩形：接近全宽的容器继续拆，其余元素计入占用；
    /// 本程序的覆盖窗口（按句柄排除）不算占用。
    /// 单个元素失效（任务栏更新时元素瞬断很常见）只跳过，不拖垮整个查询。</summary>
    private static void Collect(AutomationElement el, int trayWidth, int trayScreenLeft,
        int excludeId, List<(int L, int R)> acc)
    {
        AutomationElementCollection kids;
        try { kids = el.FindAll(TreeScope.Children, Condition.TrueCondition); }
        catch { return; }
        foreach (AutomationElement k in kids)
        {
            if (_stop) return; // 退出时不必跑完整棵树
            try
            {
                var r = k.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) continue;
                // 判一下非零：Win11 任务栏是 XAML 画的，里面每个元素的 NativeWindowHandle
                // 都是 0（它们不是窗口）。excludeId 万一是 0，这一行就会把整条任务栏
                // 全当成「自己」跳过，占用区只剩下几个遗留窗口类，算出来的空档几乎是整屏，
                // 窗口就直接摆到图标上去了——这种失效方式还特别难看出是从哪来的
                if (excludeId != 0 && k.Current.NativeWindowHandle == excludeId) continue; // 跳过自己
                // Win11 上的 Win10 遗留任务栏残骸：新任务栏是 XAML 画的，但旧的容器窗口
                // 还挂在树上，报着一个跟实际布局毫无关系的旧矩形，而且
                // IsOffscreen=False、IsWindowVisible=True——两种可见性判据都过滤不掉它。
                // Win11 图标居中，图标一少真图标区就往中间收缩，这块不动的旧矩形
                // 会凭空吃掉几百像素可用区（主屏是 ReBarWindow32，副屏任务栏是 WorkerW）。
                // 按系统版本判而不是光看类名：这两个类在 Win10 上是包着整个任务列表的
                // 真实容器，跳掉的话连里面的按钮一起漏掉，窗口就会压在图标上。
                //
                // 这两道检查必须排在下面的「全宽容器」分支之前。遗留窗口报的矩形本来就
                // 跟现实无关，它完全可以报出接近全宽的宽度——实测副屏 WorkerW 的 UIA 矩形
                // 是 48..1920（w=1872，稳稳越过全宽阈值），而它 GetWindowRect 的真实矩形
                // 只有 385..830。先命中容器分支就等于绕开了这道过滤，白留一个隐患
                if (IsWin11 && LegacyShellClasses.Contains(k.Current.ClassName)) continue;
                if (r.Width >= trayWidth * 0.9)
                {
                    Collect(k, trayWidth, trayScreenLeft, excludeId, acc); // 全宽容器继续拆
                    continue;
                }
                acc.Add(((int)r.Left - trayScreenLeft, (int)r.Right - trayScreenLeft));
            }
            catch
            {
                // 该元素刚好被销毁/不可用，跳过即可
            }
        }
    }

    /// <summary>UI 侧顺手做的看护（每次 Dock 调一遍，纯读时间戳，极轻）。
    ///
    /// 要看护的是一类静默故障：后台枚举从此不再产出新结果，而 UI 侧「拿不到新快照就
    /// 沿用旧的」——这个沿用本身是对的（UIA 抖一下就让窗口跳位更难看），但原先它没有
    /// 任何年龄上限，于是窗口能无限期停在一个几天前的布局算出来的位置上，
    /// 既不自愈也不留一个字的痕迹，用户看到的只是「位置不对，中间空一块」。
    ///
    /// 分两种情形，能自愈的程度不一样：
    /// 卡在 UIA 调用里出不来 → 换条线程接手，真能救回来；
    /// 线程还在转但枚举持续失败/返回空树 → UIA 就是不给结果，救不回来，
    /// 那就拉回快节奏死等它恢复，并留一条日志让事后查得到。</summary>
    private static void Watchdog()
    {
        var beat = Volatile.Read(ref _beatMs);
        if (beat == 0) return; // 线程还没跑起第一轮
        var now = Environment.TickCount64;
        if (now - beat > StuckAfterMs)
        {
            // 先叫一声再下结论：睡眠/休眠期间 TickCount64 照走（它算的是开机时长，
            // 不是运行时长），唤醒后心跳看起来也像「很久没动」，而线程其实好得很。
            // 真卡在 UIA 调用里的线程是叫不动的，几轮之内心跳都不会挪一下
            if (_stuckBeat != beat) { _stuckBeat = beat; _stuckConfirms = 0; Nudge(); return; }
            if (++_stuckConfirms < StuckConfirms) return;
            if (_revives >= MaxRevives)
            {
                MarkStale($"任务栏枚举已卡死 {(now - beat) / 1000}s，接手线程已用尽"
                    + $"（{MaxRevives} 次），窗口只能沿用旧空档");
                return;
            }
            _revives++;
            // 先把心跳记到当下：接手线程要过一会儿才写第一笔，
            // 不然下一轮（1.5s 后）会拿同一个陈旧的 beat 再判一次卡死，一路把配额烧光
            Volatile.Write(ref _beatMs, now);
            _stuckBeat = 0;
            _stuckConfirms = 0;
            var gen = Interlocked.Increment(ref _gen);
            Log.Note("freespace", $"任务栏枚举卡死 {(now - beat) / 1000}s，"
                + $"第 {_revives} 次换线程接手（旧线程挂在 UIA 调用里，无法取消，只能弃置）");
            StartWorker(gen);
            return;
        }
        _stuckBeat = 0;
        _stuckConfirms = 0;
        var ok = Volatile.Read(ref _lastOkMs);
        if (ok != 0 && now - ok > StuckAfterMs)
            MarkStale($"任务栏枚举已 {(now - ok) / 1000}s 没有产出结果，窗口正沿用旧空档");
    }

    private static void MarkStale(string msg)
    {
        _idleMs = FastRefreshMs; // 死等恢复：一旦能测出来就立刻纠正位置，别再退避到 60s
        if (_staleLogged) return;
        _staleLogged = true;     // 每次停产只记一条，否则 1.5 秒一条能把 error.log 刷满
        Log.Note("freespace", msg);
    }

    // 空档窄到这个数以下（client 像素）摆什么都是一团糊，宁可不认这个空档，
    // 让调用方沿用上次的位置
    private const int MinGapWidth = 60;    // 对面半边比指定半边宽出这么多才值得跨过去（约 3 个 12pt 汉字）。
    // 不设门槛的话两边宽度稍有变化窗口就来回搬家
    private const int CrossSideMargin = 40;

    /// <summary>在任务栏上找最合适的空档（client 像素坐标）。纯计算，只读后台快照。
    /// wantWidth：窗口期望宽度（自然宽度，非收缩后）；minWidth：还能好好显示的最小宽度；
    /// currentX：窗口当前 x；preferSide：left | right，优先待在哪半边。
    /// 快照还没就绪（启动头一秒）或任务栏已换（explorer 重启）时返回 null，
    /// 调用方回退默认摆放。</summary>
    public static (int L, int R)? FindBestGap(IntPtr trayHwnd, IntPtr excludeHwnd,
        int wantWidth, int minWidth, int currentX, string preferSide)
    {
        SetTargets(trayHwnd, excludeHwnd);
        Watchdog();
        var snap = _snap;
        if (snap == null || snap.Tray != trayHwnd || snap.TrayWidth <= 0) return null;
        var occ = snap.Occupied;
        if (occ.Count == 0) return null;

        var trayRight = snap.TrayWidth;
        var gaps = new List<(int L, int R)>();
        var cur = 0;
        foreach (var (l, r) in occ)
        {
            if (l > cur) gaps.Add((cur, l));
            cur = Math.Max(cur, r);
        }
        if (trayRight > cur) gaps.Add((cur, trayRight));

        var half = trayRight / 2.0;
        var halfCenter = preferSide == "left" ? trayRight / 4.0 : trayRight * 3.0 / 4.0;
        bool InSide((int L, int R) g) =>
            preferSide == "left" ? (g.L + g.R) / 2.0 < half : (g.L + g.R) / 2.0 >= half;

        // 放得下的空档：优先指定半边（取离该半边中心最近的），都没有再全局就近
        var fitting = gaps.Where(g => g.R - g.L >= wantWidth).ToList();
        var inSide = fitting.Where(InSide).ToList();
        if (inSide.Count > 0)
            return inSide.OrderBy(g => Math.Abs((g.L + g.R) / 2.0 - halfCenter)).First();
        if (fitting.Count > 0)
        {
            var center = currentX + wantWidth / 2.0;
            return fitting.OrderBy(g => Math.Abs((g.L + g.R) / 2.0 - center)).First();
        }

        // 装不满：退一步找装得下「最小可读宽度」的，仍然优先指定半边。
        // 这一档取最宽而不是取就近——反正要缩，能多一个字是一个字
        var usable = gaps.Where(g => g.R - g.L >= minWidth).ToList();
        var usableInSide = Widest(usable.Where(InSide));
        if (usableInSide != null) return usableInSide;
        var usableAny = Widest(usable);
        if (usableAny != null) return usableAny;

        // 连最小可读都装不下：这时候侧向偏好是软的。原先它是硬约束——
        // 指定半边只要有一条缝就往里挤，哪怕对面宽出一大截。实测过一次
        // 右半边只有 255px、左半边有 337px，窗口硬挤在右边缩到 247px，
        // 长歌名当场被切掉，看着像被旁边的图标压住了
        var sideBest = Widest(gaps.Where(InSide));
        var anyBest = Widest(gaps);
        if (sideBest == null) return anyBest;
        if (anyBest == null) return sideBest;
        return anyBest.Value.R - anyBest.Value.L >= sideBest.Value.R - sideBest.Value.L + CrossSideMargin
            ? anyBest : sideBest;

        static (int L, int R)? Widest(IEnumerable<(int L, int R)> pool)
        {
            (int L, int R)? best = null;
            var bestW = MinGapWidth;
            foreach (var g in pool)
                if (g.R - g.L > bestW) { bestW = g.R - g.L; best = g; }
            return best;
        }
    }
}
