// 年度听歌报告：一张竖版海报，数字来自 StatsReport.BuildYear，可以存成图片、复制到剪贴板发给朋友。
// 由统计窗口打开（非模态单例，Owner 是统计窗口），用的是统计窗口读到的那份全部记录，
// 不受统计窗口里的播放器筛选影响。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics;

internal partial class YearReportWindow : Window
{
    private IReadOnlyList<ListenSegment> _segs = [];
    private DateTime _now;
    private YearReport? _report;
    private int _version;       // 连着切年份时只认最后一次的结果
    private bool _syncingYears; // 程序自己填年份选项时也会触发 SelectionChanged，不当成用户在选

    private const int CoverPx = 176; // 年度之歌封面 88px，按 2 倍解码

    /// <summary>配不配图（设置里的「统计页显示封面和头像」），统计窗口每次送记录前按当前设置给。</summary>
    internal bool WithArt { get; set; } = true;

    /// <summary>一份报告连同它要的配图，在后台线程一起备好。</summary>
    private sealed record Built(YearReport Report, IReadOnlyDictionary<string, ImageSource> Art, ImageSource? Cover);

    internal YearReportWindow()
    {
        InitializeComponent();
        SetReady(false);
        // 模糊底比卡片大一圈（免得边缘模糊出透明），按卡片的圆角裁掉
        SongOfYear.SizeChanged += (_, e) =>
            SongOfYearBackdrop.Clip = new RectangleGeometry(new Rect(e.NewSize), 8, 8);
    }

    private static Built Build(IReadOnlyList<ListenSegment> segs, int year, DateTime now, bool withArt)
    {
        var r = StatsReport.BuildYear(segs, year, now);
        if (!withArt) return new Built(r, new Dictionary<string, ImageSource>(), null);
        var art = StatsWindow.LoadArt(r.TopSongs.Concat(r.TopArtists).Concat(r.TopAlbums));
        var cover = r.TopSongs.Count > 0 && r.TopSongs[0].Art is { } key ? ArtCache.Load(key, CoverPx) : null;
        return new Built(r, art, cover);
    }

    /// <summary>同设置窗：Win11 22H2+ 开 Mica，不支持时保留 XAML 里的不透明底色。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.TryEnableMica(hwnd))
            Background = Brushes.Transparent;
    }

    /// <summary>换一份记录重新生成。原来选着的年份还有记录就留着，否则按 DefaultYear 挑。</summary>
    internal void SetData(IReadOnlyList<ListenSegment> segs, DateTime now) => LoadYear(SyncYears(segs, now, null));

    /// <summary>自测截图用：同步算好、摆出来。</summary>
    internal void Load(IReadOnlyList<ListenSegment> segs, DateTime now, int year)
    {
        SyncYears(segs, now, year);
        Render(Build(segs, year, now, WithArt));
    }

    private int SyncYears(IReadOnlyList<ListenSegment> segs, DateTime now, int? want)
    {
        _segs = segs;
        _now = now;
        var years = StatsReport.Years(segs, now);
        var year = want ?? (YearBox.SelectedItem is int cur && years.Contains(cur) ? cur : DefaultYear(years, now));
        _syncingYears = true;
        try
        {
            YearBox.ItemsSource = years;
            YearBox.SelectedItem = year;
        }
        finally
        {
            _syncingYears = false;
        }
        return year;
    }

    /// <summary>默认看哪年：一月里多半是想看刚过完的那年；否则看今年；今年还没记录就看最近有记录的一年。</summary>
    internal static int DefaultYear(IReadOnlyList<int> years, DateTime now)
    {
        if (now.Month == 1 && years.Contains(now.Year - 1)) return now.Year - 1;
        if (years.Count == 0 || years.Contains(now.Year)) return now.Year;
        return years[0];
    }

    private void Year_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingYears || YearBox.SelectedItem is not int year) return;
        LoadYear(year);
    }

    /// <summary>汇总要把全部记录过一遍（「今年的新歌」得知道以前听没听过），放到线程池去算。</summary>
    private async void LoadYear(int year)
    {
        var version = ++_version;
        var (segs, now, withArt) = (_segs, _now, WithArt);
        SetReady(false);
        StatusText.Text = "正在生成…";
        try
        {
            var built = await Task.Run(() => Build(segs, year, now, withArt));
            if (version != _version) return;
            StatusText.Text = "";
            Render(built);
        }
        catch (Exception ex)
        {
            Log.Error("stats-year", ex);
            if (version == _version) StatusText.Text = "生成报告出错了，详情见 error.log";
        }
    }

    private void Render(Built b)
    {
        var r = b.Report;
        _report = r;
        Poster.Visibility = r.Empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = r.Empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = $"{r.Year} 年还没有听歌记录";
        SetReady(!r.Empty);
        if (r.Empty) return;

        YearText.Text = r.Year.ToString();
        RangeText.Text = r.Year == _now.Year ? $"1 月 1 日 – {_now.Month} 月 {_now.Day} 日" : "1 月 1 日 – 12 月 31 日";
        TotalText.Text = StatsReport.FormatDuration(r.TotalS);
        var counts = new List<string> { $"播放 {r.Plays} 次", $"{r.Songs} 首歌", $"{r.Artists} 位歌手" };
        if (r.Albums > 0) counts.Add($"{r.Albums} 张专辑");
        counts.Add($"听歌 {r.ActiveDays} 天");
        CountsText.Text = string.Join(" · ", counts);

        if (r.TopSongs.Count > 0)
        {
            var top = r.TopSongs[0];
            SongOfYearTitle.Text = top.Title;
            SongOfYearSub.Text = top.Sub.Length > 0 ? $"{top.Sub} · {top.Right}" : top.Right;
        }
        SongOfYear.Visibility = r.TopSongs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSongOfYearCover(b.Cover);
        SongList.ItemsSource = StatsWindow.ToRanks(r.TopSongs, b.Art);
        ArtistList.ItemsSource = StatsWindow.ToRanks(r.TopArtists, b.Art);
        AlbumSection.Visibility = r.TopAlbums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlbumList.ItemsSource = StatsWindow.ToRanks(r.TopAlbums, b.Art);
        AlbumNote.Text = r.AlbumNote;
        MonthChart.ItemsSource = StatsWindow.ToBars(r.Months);
        HabitSection.Visibility = r.PeakText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeakText.Text = r.PeakText;
        HeatText.Text = r.HeatText;
        HighlightList.ItemsSource = r.Highlights;
    }

    /// <summary>有封面：封面 + 模糊底 + 白字；没有：强调色底，字色回到主题资源。</summary>
    private void SetSongOfYearCover(ImageSource? cover)
    {
        var has = cover != null;
        SongOfYearCoverBrush.ImageSource = cover;
        SongOfYearBlur.ImageSource = cover;
        SongOfYearCover.Visibility = SongOfYearBackdrop.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in new[] { SongOfYearLabel, SongOfYearTitle, SongOfYearSub })
        {
            if (has) t.Foreground = Brushes.White;
            else t.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        }
    }

    private void SetReady(bool ready) => SaveButton.IsEnabled = CopyButton.IsEnabled = ready;

    private BitmapSource PosterShot() => ShareImage.Render(Poster, 2);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { Empty: false } r) return;
        try
        {
            var path = ShareImage.SaveWithDialog(this, PosterShot(), $"听歌年度报告-{r.Year}.png");
            if (path != null) StatusText.Text = $"已保存为 {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log.Error("share-save", ex);
            StatusText.Text = "保存图片失败，详情见 error.log";
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { Empty: false }) return;
        try
        {
            StatusText.Text = ShareImage.CopyToClipboard(PosterShot())
                ? "图片已复制，可以直接粘贴到聊天窗里"
                : "剪贴板正被别的程序占用，稍后再试一次";
        }
        catch (Exception ex)
        {
            Log.Error("share-copy", ex);
            StatusText.Text = "生成图片失败，详情见 error.log";
        }
    }

    /// <summary>「保存为图片」存下的那张海报，给 --stats-test shot 检查用。</summary>
    internal void SaveShot(string path) => ShareImage.SaveTo(path, PosterShot());

    /// <summary>整个窗口（工具栏加海报可见的部分），给 --stats-test shot 检查工具栏布局用。</summary>
    internal void SaveWindowShot(string path) => ShareImage.SaveTo(path, ShareImage.Render(WindowRoot, 1.25, 0));
}
