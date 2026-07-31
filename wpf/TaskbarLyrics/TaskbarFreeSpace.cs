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

    private const int RefreshMs = 1000;  // 空档刷新周期（任务栏元素变化不快）

    private static volatile Snapshot? _snap;
    private static volatile bool _stop;
    private static int _started;
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
        // IsBackground：进程退出时直接丢弃，不必等一次可能挂住的 UIA 调用返回
        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "TaskbarFreeSpace" };
        // UIA 客户端在 MTA 上工作（线程池默认也是 MTA），显式声明避免隐式 STA 带来的封送开销
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
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
        if (changed) Wake.Set();
    }

    private static void WorkerLoop()
    {
        while (!_stop)
        {
            Wake.Reset(); // 先清再测：测量期间到来的变化会让下面的 Wait 立刻返回
            var tray = _wantTray;
            if (tray != IntPtr.Zero)
            {
                try { Measure(tray, _wantExclude); }
                catch { /* UIA 整体失败：保留上一份快照，调用方沿用旧空档不跳位 */ }
            }
            Wake.Wait(RefreshMs);
        }
    }

    /// <summary>枚举一遍任务栏，算出占用区间并发布快照。</summary>
    private static void Measure(IntPtr tray, IntPtr exclude)
    {
        if (!NativeMethods.GetClientRect(tray, out var rc) || rc.Right <= 0) return;
        NativeMethods.GetWindowRect(tray, out var wrc);
        var raw = new List<(int L, int R)>();
        Collect(AutomationElement.FromHandle(tray), rc.Right, wrc.Left, exclude, raw);
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
        _snap = new Snapshot { Tray = tray, TrayWidth = rc.Right, Occupied = merged };
    }

    /// <summary>递归收集占用矩形：接近全宽的容器继续拆，其余元素计入占用；
    /// 本程序的覆盖窗口（按句柄排除）不算占用。
    /// 单个元素失效（任务栏更新时元素瞬断很常见）只跳过，不拖垮整个查询。</summary>
    private static void Collect(AutomationElement el, int trayWidth, int trayScreenLeft,
        IntPtr excludeHwnd, List<(int L, int R)> acc)
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
                if (r.Width >= trayWidth * 0.9)
                {
                    Collect(k, trayWidth, trayScreenLeft, excludeHwnd, acc); // 全宽容器继续拆
                    continue;
                }
                if (k.Current.NativeWindowHandle == excludeHwnd.ToInt32()) continue; // 跳过自己
                acc.Add(((int)r.Left - trayScreenLeft, (int)r.Right - trayScreenLeft));
            }
            catch
            {
                // 该元素刚好被销毁/不可用，跳过即可
            }
        }
    }

    /// <summary>在任务栏上找最合适的空档（client 像素坐标）。纯计算，只读后台快照。
    /// wantWidth：窗口期望宽度（自然宽度，非收缩后）；currentX：窗口当前 x；
    /// preferSide：left | right，放得下的空档里优先选指定半边（空间恢复时自动"回家"）。
    /// 快照还没就绪（启动头一秒）或任务栏已换（explorer 重启）时返回 null，
    /// 调用方回退默认摆放。</summary>
    public static (int L, int R)? FindBestGap(IntPtr trayHwnd, IntPtr excludeHwnd,
        int wantWidth, int currentX, string preferSide)
    {
        SetTargets(trayHwnd, excludeHwnd);
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
        // 没有放得下的：优先指定半边里最宽的，其次全局最宽（调用方负责收缩窗口）
        var pool = gaps.Where(InSide).ToList();
        if (pool.Count == 0) pool = gaps;
        (int L, int R)? best = null;
        var bestW = 60;
        foreach (var g in pool)
        {
            if (g.R - g.L > bestW) { bestW = g.R - g.L; best = g; }
        }
        return best;
    }
}
