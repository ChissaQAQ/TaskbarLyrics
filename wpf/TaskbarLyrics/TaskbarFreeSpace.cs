// 任务栏空档计算（自动避让功能）：
// 用 UIA 枚举任务栏上的元素矩形（开始/搜索/任务视图/应用图标/时钟等），
// 合并成占用区间后求水平空档；选择能放下窗口且离当前位置最近的空档。
// 查询结果缓存 1s，避免 Dock 周期里重复跨进程 UIA 调用。
using System.Windows.Automation;

namespace TaskbarLyrics;

public static class TaskbarFreeSpace
{
    private static DateTime _cacheAt = DateTime.MinValue;
    private static IntPtr _cacheTray;
    private static List<(int L, int R)> _cacheOcc = new();

    /// <summary>在任务栏上找最合适的空档（client 像素坐标）。
    /// wantWidth：窗口期望宽度（自然宽度，非收缩后）；currentX：窗口当前 x；
    /// preferSide：left | right，放得下的空档里优先选指定半边（空间恢复时自动"回家"）。
    /// 找不到（UIA 失败/无占用信息）返回 null，调用方回退默认摆放。</summary>
    public static (int L, int R)? FindBestGap(IntPtr trayHwnd, IntPtr excludeHwnd,
        int wantWidth, int currentX, string preferSide)
    {
        if (!NativeMethods.GetClientRect(trayHwnd, out var rc) || rc.Right <= 0) return null;
        var occ = OccupiedIntervals(trayHwnd, excludeHwnd);
        if (occ.Count == 0) return null;

        var gaps = new List<(int L, int R)>();
        var cur = 0;
        foreach (var (l, r) in occ)
        {
            if (l > cur) gaps.Add((cur, l));
            cur = Math.Max(cur, r);
        }
        if (rc.Right > cur) gaps.Add((cur, rc.Right));

        var half = rc.Right / 2.0;
        var halfCenter = preferSide == "left" ? rc.Right / 4.0 : rc.Right * 3.0 / 4.0;
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

    /// <summary>任务栏上被其他元素占用的水平区间（client 坐标，排序合并，带 1s 缓存）。</summary>
    private static List<(int L, int R)> OccupiedIntervals(IntPtr trayHwnd, IntPtr excludeHwnd)
    {
        if (trayHwnd == _cacheTray && (DateTime.UtcNow - _cacheAt).TotalSeconds < 1)
            return _cacheOcc;

        var raw = new List<(int L, int R)>();
        try
        {
            NativeMethods.GetClientRect(trayHwnd, out var rc);
            NativeMethods.GetWindowRect(trayHwnd, out var wrc);
            var root = AutomationElement.FromHandle(trayHwnd);
            Collect(root, rc.Right, wrc.Left, excludeHwnd, raw);
        }
        catch
        {
            raw.Clear(); // UIA 失败时视为无占用信息，回退默认摆放
        }

        raw.Sort((a, b) => a.L.CompareTo(b.L));
        var merged = new List<(int L, int R)>();
        foreach (var (l, r) in raw)
        {
            if (merged.Count > 0 && l <= merged[^1].R + 2)
                merged[^1] = (merged[^1].L, Math.Max(merged[^1].R, r));
            else
                merged.Add((l, r));
        }
        _cacheTray = trayHwnd;
        _cacheAt = DateTime.UtcNow;
        _cacheOcc = merged;
        return merged;
    }

    /// <summary>递归收集占用矩形：接近全宽的容器继续拆，其余元素计入占用；
    /// 本程序的覆盖窗口（按句柄排除）不算占用。</summary>
    private static void Collect(AutomationElement el, int trayWidth, int trayScreenLeft,
        IntPtr excludeHwnd, List<(int L, int R)> acc)
    {
        AutomationElementCollection kids;
        try { kids = el.FindAll(TreeScope.Children, Condition.TrueCondition); }
        catch { return; }
        foreach (AutomationElement k in kids)
        {
            System.Windows.Rect r;
            try { r = k.Current.BoundingRectangle; }
            catch { continue; }
            if (r.Width <= 0 || r.Height <= 0) continue;
            if (r.Width >= trayWidth * 0.9)
            {
                Collect(k, trayWidth, trayScreenLeft, excludeHwnd, acc); // 全宽容器继续拆
                continue;
            }
            if (k.Current.NativeWindowHandle == excludeHwnd.ToInt32()) continue; // 跳过自己
            acc.Add(((int)r.Left - trayScreenLeft, (int)r.Right - trayScreenLeft));
        }
    }
}
