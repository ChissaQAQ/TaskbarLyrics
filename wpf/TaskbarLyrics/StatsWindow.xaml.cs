// 听歌统计窗口：单页仪表盘，数字全部来自 StatsReport.Build，这里只管摆出来。
// 非模态单例（MainController.OpenStats），开着它也能继续用托盘菜单。
// 打开时算一次，之后点「刷新」才重算：正在放的歌每秒都在涨，自动刷新会让数字在眼前跳。
// 读到的记录留在内存里：切播放器筛选只重算、不再读盘，年度报告也用这一份。
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics;

internal partial class StatsWindow : Window
{
    private const double ChartHeight = 120; // 与 StatsResources.xaml 里 BarTemplate 柱区的行高一致
    private const double ShareWidth = 880;  // 分享图里内容区的宽度，约等于默认窗口宽

    private readonly MainController? _app; // 自测截图时为 null：只渲染给定的数据，不读盘
    private IReadOnlyList<ListenSegment>? _segs; // 最近一次读到的全部记录（未经筛选）
    private DateTime _builtAt;                   // 算 _snap 时的「现在」
    private string? _player;                     // 选中的播放器（显示名），null 为全部
    private StatsSnapshot? _snap;
    private int _version;         // 刷新、切筛选可能叠着来，只认最后一次的结果
    private bool _syncingPlayers; // 程序自己往下拉框里填选项时也会触发 SelectionChanged，不当成用户在选
    private YearReportWindow? _yearWindow;

    /// <summary>一根柱子。高度预先算好直接绑，省一个 Converter。</summary>
    internal sealed record BarView(string Label, double Height, string Tip, double Opacity);

    /// <summary>榜单一行。占比条是两段星号宽度的列，Fill : Rest 就是比例。
    /// Image 是本地缓存里的配图，没有为 null，此时显示占位：歌曲音符、专辑唱片、歌手名的头一个字。</summary>
    internal sealed record RankView(int Rank, string Title, string Sub, string Right, GridLength Fill, GridLength Rest,
        StatsArtKind Kind, ImageSource? Image)
    {
        public bool Top => Rank <= 3; // 前三名的名次用强调色
        public string Glyph => Kind switch { StatsArtKind.Song => "", StatsArtKind.Album => "", _ => "" };
        public string Initial => Kind == StatsArtKind.Artist && Title.Length > 0
            ? StringInfo.GetNextTextElement(Title).ToUpperInvariant() : "";
    }

    private const int ArtPx = 64; // 榜单配图 32px，按 2 倍解码，高分屏和 2 倍的分享图都不糊

    /// <summary>首页全部榜单要用的配图（各时间范围、两种排法），从本地缓存读好、冻结，后台线程调用。</summary>
    private static IReadOnlyDictionary<string, ImageSource> LoadArt(StatsSnapshot snap)
        => LoadArt(snap.Ranges.Values.SelectMany(v => new[] { v.Songs, v.Artists, v.Albums, v.NewSongs })
            .SelectMany(p => p.ByPlays.Concat(p.ByTime)));

    internal static IReadOnlyDictionary<string, ImageSource> LoadArt(IEnumerable<StatsRankRow> rows, int px = ArtPx)
    {
        var map = new Dictionary<string, ImageSource>();
        foreach (var key in rows.Select(r => r.Art).OfType<string>().Distinct())
            if (ArtCache.Load(key, px) is { } img) map[key] = img;
        return map;
    }

    private IReadOnlyDictionary<string, ImageSource> _art = new Dictionary<string, ImageSource>();

    /// <summary>热力图一格：强调色那层的不透明度，没听过的格子为 0。</summary>
    internal sealed record HeatView(double Opacity, string Tip);

    internal StatsWindow(MainController? app)
    {
        _app = app;
        InitializeComponent();
        DataPathText.Text = StatsStore.DefaultDir;
        HeatHours.ItemsSource = Enumerable.Range(0, 24).Select(h => h % 3 == 0 ? h.ToString() : "").ToList();
        SetBusy(true);
        if (app != null) Loaded += (_, _) => Refresh();
        // 在设置窗里开关了记录，切回来时顶上的提示条跟着变
        Activated += (_, _) => UpdateDisabledCard();
    }

    /// <summary>同设置窗：Win11 22H2+ 开 Mica，不支持时保留 XAML 里的不透明底色。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.TryEnableMica(hwnd))
            Background = Brushes.Transparent;
    }

    /// <summary>重读记录、重算全部统计。</summary>
    internal void Refresh() => Recompute(reload: true);

    /// <summary>reload 时重读记录，否则用上次读到的那份（切播放器筛选）。读盘和汇总放到线程池：
    /// 一年的记录有好几 MB、要算一两百毫秒，不能卡住窗口。</summary>
    private async void Recompute(bool reload)
    {
        if (_app is not { } app) return;
        var version = ++_version;
        var cached = reload ? null : _segs;
        var player = _player;
        SetBusy(true);
        StatusText.Text = "";
        if (cached == null) SinceText.Text = "正在统计…";
        try
        {
            // 「今天」按系统当前的时区算：换过时区的话进程里缓存的还是旧的（见 ListenRecorder.LocalNow）
            TimeZoneInfo.ClearCachedData();
            var now = DateTime.Now;
            var withArt = app.Cfg.StatsImages;
            var (segs, snap, art) = await Task.Run(() =>
            {
                IReadOnlyList<ListenSegment> s = cached ?? app.LoadStatsSegments();
                var built = StatsReport.Build(s, now, player);
                return (s, built, withArt ? LoadArt(built) : new Dictionary<string, ImageSource>());
            });
            if (version != _version) return;
            _segs = segs;
            _builtAt = now;
            _art = art;
            Render(snap);
            // 年度报告开着的话也换成新读到的记录；记录被清空了就关掉：没有年份可选，开着只剩一句空话
            if (cached == null)
            {
                if (segs.Count == 0) _yearWindow?.Close();
                else if (_yearWindow != null)
                {
                    _yearWindow.WithArt = withArt;
                    _yearWindow.SetData(segs, now);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("stats-build", ex);
            if (version == _version) SinceText.Text = "统计出错了，详情见 error.log";
        }
        finally
        {
            if (version == _version) SetBusy(false);
        }
    }

    /// <summary>自测截图用：同步算好、摆出来，不读盘。</summary>
    internal void Load(IReadOnlyList<ListenSegment> segs, DateTime now, string? player = null)
    {
        _segs = segs;
        _builtAt = now;
        var snap = StatsReport.Build(segs, now, player);
        _art = LoadArt(snap);
        Render(snap);
        SetBusy(false);
    }

    /// <summary>算着的时候工具栏不能点：切筛选、存图片、年度报告都要用算好的结果。</summary>
    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = PlayerBox.IsEnabled = !busy;
        ShareButton.IsEnabled = !busy && _snap is { Empty: false };
        YearButton.IsEnabled = !busy && _segs is { Count: > 0 };
    }

    private void Render(StatsSnapshot snap)
    {
        _snap = snap;
        _player = snap.Player; // 认不出的播放器名在 Build 里已退回「全部」
        UpdateDisabledCard();
        EmptyCard.Visibility = snap.Empty ? Visibility.Visible : Visibility.Collapsed;
        ContentPanel.Visibility = snap.Empty ? Visibility.Collapsed : Visibility.Visible;
        var d = snap.FirstDay;
        SinceText.Text = snap.Empty ? "听过的歌会记在这里"
            : snap.Player == null ? $"从 {d.Year} 年 {d.Month} 月 {d.Day} 日开始记录"
            : $"只看「{snap.Player}」，它的记录从 {d.Year} 年 {d.Month} 月 {d.Day} 日开始";
        SyncPlayers(snap);
        OverviewList.ItemsSource = snap.Overview;
        FactsList.ItemsSource = snap.Facts;
        RenderTrend();
        RenderRange();
    }

    /// <summary>下拉框第一项是「全部播放器」，其余按听的时长排。只听过一个播放器、又没在筛选时收起：没什么可选的。</summary>
    private void SyncPlayers(StatsSnapshot snap)
    {
        _syncingPlayers = true;
        try
        {
            var options = snap.PlayerOptions.ToList();
            PlayerBox.ItemsSource = options.Prepend("全部播放器").ToList();
            PlayerBox.SelectedIndex = snap.Player == null ? 0 : options.IndexOf(snap.Player) + 1;
        }
        finally
        {
            _syncingPlayers = false;
        }
        PlayerBox.Visibility = snap.PlayerOptions.Count > 1 || snap.Player != null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>提示条和空状态的文案跟着「记录开没开、有没有数据、正在放的那段拿不拿得到」变。</summary>
    private void UpdateDisabledCard()
    {
        var off = _app is { Cfg.StatsEnabled: false };
        DisabledCard.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        DisabledDesc.Text = _snap is { Empty: false }
            ? "现在放的歌不会被记下来，下面显示的是关闭之前的数据。"
            : "现在放的歌不会被记下来。";
        // --settings 单开时正在放的那段还在主程序手里没落盘，点「刷新」也算不进来
        var live = _app?.RecordsLive == true;
        EmptyDesc.Text = off ? "开启记录后，放过的歌才会出现在这里。"
            : live ? "放几首歌再回来看看吧，正在播放的歌点「刷新」就能算进来。"
            : "放几首歌再回来看看吧。";
        RefreshButton.ToolTip = live ? "重新读取记录（正在播放的歌也会算进去）" : "重新读取记录";
    }

    private void Player_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPlayers || _snap == null || PlayerBox.SelectedIndex < 0) return;
        var i = PlayerBox.SelectedIndex;
        var player = i == 0 ? null : _snap.PlayerOptions[i - 1];
        if (player == _snap.Player) return;
        _player = player;
        Recompute(reload: false);
    }

    // 几组切换按钮在 InitializeComponent 里就会触发一次 Checked，那时还没有数据，_snap 为 null 直接跳过
    private void Trend_Checked(object sender, RoutedEventArgs e) => RenderTrend();

    private void Range_Checked(object sender, RoutedEventArgs e) => RenderRange();

    private void Sort_Checked(object sender, RoutedEventArgs e) => RenderRange();

    private void RenderTrend()
    {
        if (_snap == null) return;
        var bars = TrendWeek.IsChecked == true ? _snap.Weeks
            : TrendMonth.IsChecked == true ? _snap.Months
            : _snap.Days;
        TrendChart.ItemsSource = ToBars(bars);
    }

    private void RenderRange()
    {
        if (_snap == null) return;
        var range = RangeWeek.IsChecked == true ? StatsRange.Week
            : RangeYear.IsChecked == true ? StatsRange.Year
            : RangeAll.IsChecked == true ? StatsRange.All
            : StatsRange.Month;
        var v = _snap.Ranges[range];
        var byTime = SortTime.IsChecked == true;
        List<RankView> Ranks(StatsRankPair p) => ToRanks(byTime ? p.ByTime : p.ByPlays, _art);

        SummaryText.Text = v.Summary;
        // 这段时间一首没听：只留那句小结，下面一排空榜单和平地柱状图没什么可看的
        RangeDetail.Visibility = v.Songs.ByPlays.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SongList.ItemsSource = Ranks(v.Songs);
        ArtistList.ItemsSource = Ranks(v.Artists);
        AlbumList.ItemsSource = Ranks(v.Albums);
        AlbumNote.Text = v.AlbumNote;
        NewSongList.ItemsSource = Ranks(v.NewSongs);
        NewSongNote.Text = v.NewSongsNote;
        HourChart.ItemsSource = ToBars(v.Hours);
        PeakText.Text = v.PeakText;
        HeatChart.ItemsSource = ToHeat(v.Heat);
        HeatText.Text = v.HeatText;
        PlayerList.ItemsSource = ToRanks(v.Players);
        // 只看一个播放器时它的占比恒为 100%，这张卡没意义
        PlayerPanel.Visibility = _snap.Player == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按本组最大值折算柱高。有数据的柱子至少 2 像素：听了几十秒的那天也该看得出来不是零。</summary>
    internal static List<BarView> ToBars(IReadOnlyList<StatsBar> bars)
    {
        var max = bars.Count > 0 ? bars.Max(b => b.Value) : 0;
        return bars.Select(b => new BarView(
            b.Label,
            b.Value > 0 && max > 0 ? Math.Max(2, b.Value / max * ChartHeight) : 0,
            b.Tip,
            b.Highlight ? 1 : 0.5)).ToList();
    }

    internal static List<RankView> ToRanks(IReadOnlyList<StatsRankRow> rows,
        IReadOnlyDictionary<string, ImageSource>? art = null) => rows.Select(r =>
    {
        var ratio = double.IsFinite(r.Ratio) ? Math.Clamp(r.Ratio, 0, 1) : 0; // GridLength 不收 NaN
        return new RankView(r.Rank, r.Title, r.Sub, r.Right,
            new GridLength(ratio, GridUnitType.Star), new GridLength(1 - ratio, GridUnitType.Star),
            r.Kind, r.Art != null ? art?.GetValueOrDefault(r.Art) : null);
    }).ToList();

    /// <summary>按本图最大值折算深浅，开平方拉开低处的差别：时长集中在一两格时，其余格子也看得出深浅。
    /// 有数据的格子至少 0.15，和没听过的格子分得开。</summary>
    internal static List<HeatView> ToHeat(IReadOnlyList<StatsHeatCell> cells)
    {
        var max = cells.Count > 0 ? cells.Max(c => c.Value) : 0;
        return cells.Select(c => new HeatView(
            c.Value > 0 && max > 0 ? 0.15 + 0.85 * Math.Sqrt(c.Value / max) : 0, c.Tip)).ToList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        _app?.EnableStats();
        UpdateDisabledCard();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        // 还没记过歌时目录还不存在，先建出来，免得资源管理器报找不到
        var dir = StatsStore.DefaultDir;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { Log.Error("stats-dir", ex); }
        SettingsWindow.OpenExternal(dir);
    }

    /// <summary>年度报告用全部记录，不受这里的播放器筛选影响。已经开着就换成最新的记录、拉到前面。</summary>
    private void Year_Click(object sender, RoutedEventArgs e)
    {
        if (_segs is not { Count: > 0 } segs) return;
        if (_yearWindow == null)
        {
            var win = new YearReportWindow { Owner = this, WithArt = _app?.Cfg.StatsImages != false };
            win.Closed += (_, _) => { if (ReferenceEquals(_yearWindow, win)) _yearWindow = null; };
            _yearWindow = win;
            win.SetData(segs, _builtAt);
            win.Show();
            return;
        }
        if (_yearWindow.WindowState == WindowState.Minimized) _yearWindow.WindowState = WindowState.Normal;
        _yearWindow.WithArt = _app?.Cfg.StatsImages != false;
        _yearWindow.SetData(segs, _builtAt);
        _yearWindow.Activate();
    }

    // 左键也弹出菜单：「存成图片」和「复制」都常用，没有哪个适合当默认动作
    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (ShareButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ShareButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ShareShot() is not { } img) return;
            var path = ShareImage.SaveWithDialog(this, img, $"听歌统计-{_builtAt:yyyy-MM-dd}.png");
            if (path != null) StatusText.Text = $"已保存为 {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log.Error("share-save", ex);
            StatusText.Text = "保存图片失败，详情见 error.log";
        }
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ShareShot() is not { } img) return;
            StatusText.Text = ShareImage.CopyToClipboard(img)
                ? "图片已复制，可以直接粘贴到聊天窗里"
                : "剪贴板正被别的程序占用，稍后再试一次";
        }
        catch (Exception ex)
        {
            Log.Error("share-copy", ex);
            StatusText.Text = "生成图片失败，详情见 error.log";
        }
    }

    /// <summary>分享图只画 ContentPanel（概览到趣味发现）：页头、提示条、页脚的数据目录都不在里面，
    /// 目录路径里有 Windows 用户名。顶上补一行标题和统计的日期范围。</summary>
    private BitmapSource? ShareShot()
    {
        if (_snap is not { Empty: false } snap) return null;
        var (d, n) = (snap.FirstDay, _builtAt);
        var sub = $"{d.Year} 年 {d.Month} 月 {d.Day} 日 – {n.Year} 年 {n.Month} 月 {n.Day} 日";
        if (snap.Player != null) sub += $" · 只看「{snap.Player}」";
        // 分享图固定宽度：窗口拉得再宽，图也不跟着变宽，发到聊天里缩小了字还看得清。
        // 压窄、出图、还原在同一次调度里做完，屏幕上不会闪
        var oldW = ContentPanel.ReadLocalValue(WidthProperty);
        ContentPanel.Width = Math.Min(ContentPanel.ActualWidth, ShareWidth);
        try { return ShareImage.Render(ContentPanel, 2, 24, "听歌统计", sub); }
        finally
        {
            if (oldW == DependencyProperty.UnsetValue) ContentPanel.ClearValue(WidthProperty);
            else ContentPanel.Width = (double)oldW;
        }
    }

    /// <summary>把整张仪表盘（含滚动区外的部分）渲染成 PNG，给 --stats-test shot 检查布局用。</summary>
    internal void SaveShot(string path) => ShareImage.SaveTo(path, ShareImage.Render(DashboardRoot));

    /// <summary>「分享」生成的那张图，给 --stats-test shot 检查用。没有记录时不出图。</summary>
    internal void SaveShareShot(string path)
    {
        if (ShareShot() is { } img) ShareImage.SaveTo(path, img);
    }
}
