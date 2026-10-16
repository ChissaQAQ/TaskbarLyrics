// 听歌统计的汇总计算：把一堆听歌片段算成统计窗口、年度报告要显示的全部数字。
//
// 纯计算、不碰界面，在后台线程里跑（十万条片段约一两百毫秒）。
// 时间一律按「片段发生时的当地钟点」算：片段里存了当时的时区偏移，出差换了时区，
// 以前那些深夜听的歌也不会被挪到下午去。
//
// 遍历片段、按整点拆时长的循环只有一处（Accumulate）：各时间范围、年度报告都是给它一组
// 日期窗口（RangeAcc），一趟下来各窗口各收各的。以前首页、年报各写一遍拆分逻辑，
// 跨午夜、跨年的片段很容易两边算得不一样。
namespace TaskbarLyrics;

internal enum StatsRange { Week, Month, Year, All }

/// <summary>柱状图的一根柱子。Label 为空的柱子底下不写字（30 根柱子挤不下 30 个日期）。</summary>
internal sealed record StatsBar(string Label, double Value, string Tip, bool Highlight);

/// <summary>排行榜的一行。Ratio 是相对榜首（或占总量）的比例，0~1。
/// Art 是这一行配图在 ArtCache 里的键，Kind 决定配图的形状和没图时的占位；播放器榜不配图。</summary>
internal sealed record StatsRankRow(int Rank, string Title, string Sub, string Right, double Ratio,
    StatsArtKind Kind = StatsArtKind.None, string? Art = null);

internal enum StatsArtKind { None, Song, Album, Artist }

/// <summary>同一份榜单的两种排法：按播放次数、按听的时长。界面上一个开关切换。</summary>
internal sealed record StatsRankPair(IReadOnlyList<StatsRankRow> ByPlays, IReadOnlyList<StatsRankRow> ByTime)
{
    public static readonly StatsRankPair Empty = new([], []);
}

/// <summary>热力图的一格（周一 0 点起，按行排 7×24 格）。</summary>
internal sealed record StatsHeatCell(double Value, string Tip);

/// <summary>一张小卡片：标题、大号数值、一行补充说明。</summary>
internal sealed record StatsFact(string Title, string Value, string Detail);

internal sealed record StatsRangeView(
    string Summary,
    StatsRankPair Songs,
    StatsRankPair Artists,
    StatsRankPair Albums,
    string AlbumNote,
    StatsRankPair NewSongs,
    string NewSongsNote,
    IReadOnlyList<StatsBar> Hours,
    string PeakText,
    IReadOnlyList<StatsHeatCell> Heat,
    string HeatText,
    IReadOnlyList<StatsRankRow> Players);

internal sealed record StatsSnapshot(
    int SegmentCount,
    DateTime FirstDay,
    double TotalS,
    double DailyAvgS,
    IReadOnlyList<StatsFact> Overview,
    IReadOnlyList<StatsBar> Days,
    IReadOnlyList<StatsBar> Weeks,
    IReadOnlyList<StatsBar> Months,
    IReadOnlyDictionary<StatsRange, StatsRangeView> Ranges,
    IReadOnlyList<StatsFact> Facts,
    string? Player,                        // 正在筛选的播放器（显示名），null 为全部
    IReadOnlyList<string> PlayerOptions)   // 全部记录里出现过的播放器，按听的时长排；不受筛选影响
{
    public bool Empty => SegmentCount == 0;
}

/// <summary>某一年的年度报告。不受首页的播放器筛选影响。</summary>
internal sealed record YearReport(
    int Year,
    double TotalS,
    int Plays,
    int Songs,
    int Artists,
    int Albums,
    int ActiveDays,
    int? NewSongs,                         // 记录就是这年开始的话每首都是新歌，这个数没意义，为 null
    IReadOnlyList<StatsRankRow> TopSongs,
    IReadOnlyList<StatsRankRow> TopArtists,
    IReadOnlyList<StatsRankRow> TopAlbums,
    string AlbumNote,
    IReadOnlyList<StatsBar> Months,
    string PeakText,
    string HeatText,
    IReadOnlyList<StatsFact> Highlights)
{
    public bool Empty => TotalS <= 0 && Plays == 0;
}

internal static class StatsReport
{
    private const int TopN = 10;
    private const int YearTopN = 5;
    private const int MaxPlayers = 6;
    private const double StreakMinS = 60;       // 一天累计听满这么久才算「这天听过歌」
    private const double AlbumNoteRatio = 0.05; // 没专辑信息的时长占到这么多，才在专辑榜下注明

    private static readonly string[] WeekdayNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    // 播放器、曲库给不出专辑时填的占位名（按 Norm 归一化后比较），当作没有专辑
    private static readonly HashSet<string> AlbumPlaceholders = new(StringComparer.Ordinal)
    {
        "未知专辑", "未知", "unknown", "unknownalbum", "noalbum", "无专辑",
    };

    /// <summary>一首歌的身份：同一首歌在不同播放器里常有繁简、全角、歌手顺序的差别
    /// （「告白氣球 - 周杰倫」与「告白气球 - 周杰伦」），按歌词匹配那套归一化合并成一首。</summary>
    private sealed record SongId(string Key, string Title, string Artist, (string Key, string Name)[] Artists);

    /// <summary>一张专辑的身份：归一化的专辑名 + 归一化的第一位歌手。
    /// 只按名字合并的话，不同歌手的同名专辑（「Greatest Hits」）会被并成一张。</summary>
    private sealed record AlbumId(string Key, string Name, string Artist);

    private sealed class Agg
    {
        public string Key = "", Name = "", Sub = "";
        public double S;
        public int P;
        public HashSet<string>? Songs; // 歌手榜、专辑榜用：被听过几首歌
        public string? Album;          // 歌曲榜用：最近一次听时所属专辑的键，配图用专辑封面
    }

    /// <summary>一个日期窗口 [From, To] 的累加结果，null 为不设限。
    /// 时长按每一小段实际落在哪天归窗口，播放次数按片段开始的那天归。</summary>
    private sealed class RangeAcc(DateTime? from, DateTime? to)
    {
        public readonly DateTime? From = from, To = to;
        public double TotalS, NoAlbumS;
        public int Plays;
        public readonly double[] Hours = new double[24];
        public readonly double[,] Week = new double[7, 24]; // [周一起算的星期, 钟点]
        public readonly Dictionary<string, Agg> Songs = new();
        public readonly Dictionary<string, Agg> Artists = new();
        public readonly Dictionary<string, Agg> Albums = new();
        public readonly Dictionary<string, Agg> Players = new();
        public readonly Dictionary<DateTime, double> Days = new();
        public readonly Dictionary<DateTime, int> DayPlays = new();
        public readonly Dictionary<(DateTime Day, string Song), int> Loops = new();
        public (DateTime Day, TimeSpan At, string Title)? Owl; // 凌晨 0~5 点里听到最晚的那一刻
        public (DateTime Day, SongId Song)? First;             // 窗口里第一首歌

        public bool Includes(DateTime day) => (From == null || day >= From) && (To == null || day <= To);
    }

    /// <summary>一趟汇总里共用的缓存：同一首歌、同一个播放器会出现成千上万次，归一化只做一遍。</summary>
    private sealed class Ctx
    {
        private readonly Dictionary<(string, string), SongId> _ids = new();
        private readonly Dictionary<string, string> _players = new();
        private readonly Dictionary<(string, string), AlbumId?> _albums = new();
        public readonly Dictionary<string, DateTime> SongFirstDay = new(); // 每首歌第一次听是哪天

        public SongId Song(ListenSegment s)
        {
            if (!_ids.TryGetValue((s.Ti, s.Ar), out var id))
                _ids[(s.Ti, s.Ar)] = id = Identify(s.Ti, s.Ar);
            return id;
        }

        public string Player(string src)
        {
            if (!_players.TryGetValue(src, out var name))
                _players[src] = name = PlayerName(src);
            return name;
        }

        /// <summary>专辑名先用播放器报的，没有再用歌词库登记的；歌手先用专辑歌手，没有再用歌曲歌手。</summary>
        public AlbumId? Album(ListenSegment s)
        {
            var name = AlbumText(s.Al) ?? AlbumText(s.Lal);
            if (name == null) return null;
            var artist = string.IsNullOrWhiteSpace(s.Aa) ? s.Ar : s.Aa;
            if (!_albums.TryGetValue((name, artist), out var id))
                _albums[(name, artist)] = id = IdentifyAlbum(name, artist);
            return id;
        }
    }

    /// <summary>只取第一位歌手：「周杰伦/费玉清」合唱的那首和「周杰伦」的其他歌属于同一张专辑。</summary>
    private static AlbumId IdentifyAlbum(string name, string artist)
    {
        var first = Lyrics.ArtistSeparatorRegex().Split(artist)
            .Select(a => a.Trim()).FirstOrDefault(a => a.Length > 0) ?? "";
        return new AlbumId(Norm(name) + "\u0001" + Norm(first), name, first);
    }

    private static string? AlbumText(string? al)
    {
        var t = al?.Trim();
        return string.IsNullOrEmpty(t) || AlbumPlaceholders.Contains(Norm(t)) ? null : t;
    }

    // ---- 配图的键：存图（ArtCollector）和取图（榜单行）都从这里拿，两边才对得上 ----

    private const string AlbumArtPrefix = "al|", SongArtPrefix = "so|", ArtistArtPrefix = "ar|";

    /// <summary>专辑封面的键，取法同统计里认专辑：专辑名先用播放器报的、没有再用歌词库登记的。没有专辑为 null。</summary>
    internal static string? AlbumArtKey(string? album, string? lyricsAlbum, string? albumArtist, string artist)
    {
        var name = AlbumText(album) ?? AlbumText(lyricsAlbum);
        if (name == null) return null;
        return AlbumArtPrefix + IdentifyAlbum(name, string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist).Key;
    }

    /// <summary>没有专辑的歌，封面按歌存。</summary>
    internal static string SongArtKey(string title, string artist) => SongArtPrefix + Identify(title, artist).Key;

    internal static string ArtistArtKey(string name) => ArtistArtPrefix + Norm(name.Trim());

    /// <summary>歌手名按统计的口径拆开（去空、去重），收集器给每位歌手存头像用。</summary>
    internal static IReadOnlyList<string> SplitArtists(string artist)
        => Identify("", artist).Artists.Select(a => a.Name).ToList();

    /// <summary>算首页的全部数字。player 是播放器的显示名：给了就只算这个播放器的记录
    /// （QQMusic.exe 和商店版 QQ 音乐显示名相同，一起算），认不出的名字当没筛选。</summary>
    public static StatsSnapshot Build(IReadOnlyList<ListenSegment> segments, DateTime now, string? player = null)
    {
        var ctx = new Ctx();
        // 播放器选项按全部记录算：筛选之后下拉框里的其他播放器不能消失
        var options = segments.GroupBy(s => ctx.Player(s.Src))
            .Select(g => (Name: g.Key, S: g.Sum(s => s.S)))
            .OrderByDescending(x => x.S).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name).ToList();
        if (player != null && !options.Contains(player)) player = null;

        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // 周一起算
        var monthStart = new DateTime(today.Year, today.Month, 1);
        // 「全部」不设上限；其余范围不收未来的日期（系统时间曾被调到将来时留下的记录）
        var ranges = new Dictionary<StatsRange, RangeAcc>
        {
            [StatsRange.Week] = new(today.AddDays(-6), today),
            [StatsRange.Month] = new(today.AddDays(-29), today),
            [StatsRange.Year] = new(new DateTime(today.Year, 1, 1), today),
            [StatsRange.All] = new(null, null),
        };

        var ordered = (player == null ? segments : segments.Where(s => ctx.Player(s.Src) == player))
            .OrderBy(s => s.T).ToList();
        Accumulate(ordered, ranges.Values.ToList(), ctx);

        var all = ranges[StatsRange.All];
        var days = all.Days;
        var firstDay = days.Count > 0 ? days.Keys.Min() : today;
        double SumDays(DateTime from, DateTime to)
            => days.Where(kv => kv.Key >= from && kv.Key <= to).Sum(kv => kv.Value);

        // 日均按「近 30 天」算，但刚开始记录的人只按记录了几天来除，不被没装软件那些天拉低
        var avgDays = Math.Clamp((today - firstDay).Days + 1, 1, 30);
        var dailyAvg = SumDays(today.AddDays(-29), today) / avgDays;
        var overview = new List<StatsFact>
        {
            new("累计听歌", FormatDuration(all.TotalS), $"{all.Plays} 次播放 · {all.Songs.Count} 首歌"),
            new("今天", FormatDuration(days.GetValueOrDefault(today)), $"{all.DayPlays.GetValueOrDefault(today)} 次播放"),
            new("本周", FormatDuration(SumDays(weekStart, today)), "从周一算起"),
            new("本月", FormatDuration(SumDays(monthStart, today)), $"{today.Month} 月"),
            new("日均", FormatDuration(dailyAvg), avgDays < 30 ? $"按已记录的 {avgDays} 天算" : "近 30 天"),
        };

        var views = ranges.ToDictionary(kv => kv.Key, kv => BuildRange(kv.Key, kv.Value, today, firstDay, ctx));

        return new StatsSnapshot(
            ordered.Count, firstDay, all.TotalS, dailyAvg, overview,
            DayBars(days, today), WeekBars(days, weekStart, today), MonthBars(days, monthStart, today),
            views, Facts(all, today, firstDay), player, options);
    }

    /// <summary>有记录的年份，新的在前；不含将来的年份（系统时间曾被调到将来时留下的记录）。</summary>
    public static IReadOnlyList<int> Years(IReadOnlyList<ListenSegment> segments, DateTime now)
        => segments.Select(s => StatsStore.LocalStart(s).Year)
            .Where(y => y <= now.Year).Distinct().OrderDescending().ToList();

    /// <summary>算某一年的年度报告：1 月 1 日到年底，今年的话算到今天为止。</summary>
    public static YearReport BuildYear(IReadOnlyList<ListenSegment> segments, int year, DateTime now)
    {
        var ctx = new Ctx();
        var from = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var acc = new RangeAcc(from, end < now.Date ? end : now.Date);
        // 全部记录都要过一遍：「今年的新歌」得知道每首歌以前听没听过
        var ordered = segments.OrderBy(s => s.T).ToList();
        Accumulate(ordered, [acc], ctx);

        var firstEver = ctx.SongFirstDay.Count > 0 ? ctx.SongFirstDay.Values.Min() : from;
        int? fresh = firstEver < from ? acc.Songs.Keys.Count(k => ctx.SongFirstDay[k] >= from) : null;
        var songs = RankPair(acc.Songs.Values, a => a.Sub, YearTopN, StatsArtKind.Song);
        var artists = RankPair(acc.Artists.Values, a => $"{a.Songs?.Count ?? 0} 首歌", YearTopN, StatsArtKind.Artist);
        var albums = RankPair(acc.Albums.Values, AlbumSub, YearTopN, StatsArtKind.Album);
        var peak = Peak(acc.Hours);
        var (_, heatText) = Heat(acc.Week, acc.TotalS);

        // 年报里的日期不写年份：标题上已经有了
        var reference = from;
        var highlights = new List<StatsFact>();
        if (BestDayFact(acc, reference) is { } best) highlights.Add(best);
        if (LoopFact(acc, reference) is { } loop) highlights.Add(loop);
        if (OwlFact(acc, reference) is { } owl) highlights.Add(owl);
        if (LongestStreakFact(acc, reference) is { } streak) highlights.Add(streak);
        if (acc.First is { } first)
            // 歌名放说明里：说明会换行，值那一格是缩放的，长歌名会被缩到看不清
            highlights.Add(new("这一年的第一首歌", DateText(first.Day, reference), $"《{first.Song.Title}》 · {first.Song.Artist}"));
        if (acc.Players.Values.MaxBy(a => a.S) is { } main && acc.Players.Count > 1)
            highlights.Add(new("主力播放器", main.Name, $"占了 {Percent(main.S, acc.TotalS)} 的时长"));
        // 一首新歌都没有时不放「0 首」这一格：海报上看着像在数落人
        if (fresh is { } n and > 0)
            highlights.Add(new("新发现", $"{n} 首", "这一年第一次听到的歌"));

        return new YearReport(year, acc.TotalS, acc.Plays, acc.Songs.Count, acc.Artists.Count, acc.Albums.Count,
            acc.Days.Count(kv => kv.Value >= StreakMinS), fresh,
            songs.ByPlays, artists.ByPlays, albums.ByPlays, AlbumNote(acc),
            YearMonthBars(acc.Days, year), PeakText(acc, peak), heatText, highlights);
    }

    /// <summary>唯一一处遍历片段的循环：每个片段的次数、按整点拆开的时长，分给落在其日期窗口里的累加器。</summary>
    private static void Accumulate(List<ListenSegment> ordered, IReadOnlyList<RangeAcc> accs, Ctx ctx)
    {
        foreach (var seg in ordered)
        {
            var id = ctx.Song(seg);
            var player = ctx.Player(seg.Src);
            var album = ctx.Album(seg);
            var start = StatsStore.LocalStart(seg);
            var startDay = start.Date;
            ctx.SongFirstDay.TryAdd(id.Key, startDay);

            foreach (var acc in accs)
            {
                if (!acc.Includes(startDay)) continue;
                // 播放次数不拆，整段记在开始的那天：一次播放只能属于一天
                if (seg.P <= 0) continue;
                // 第一首按「算过一次播放」的片段取：开头几秒就切走、开机自动续播的碎片不算
                acc.First ??= (startDay, id);
                acc.Plays += seg.P;
                acc.DayPlays[startDay] = acc.DayPlays.GetValueOrDefault(startDay) + seg.P;
                acc.Loops[(startDay, id.Key)] = acc.Loops.GetValueOrDefault((startDay, id.Key)) + seg.P;
                Song(acc, id, album).P += seg.P;
                foreach (var a in id.Artists) Get(acc.Artists, a.Key, a.Name, "").P += seg.P;
                if (album != null) Get(acc.Albums, album.Key, album.Name, album.Artist).P += seg.P;
            }

            // 时长按整点拆开：跨午夜的片段前后两天各记各的，24 小时分布也按实际钟点落位
            var cur = start;
            var left = seg.S;
            while (left > 0.001)
            {
                var hourEnd = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0).AddHours(1);
                var room = (hourEnd - cur).TotalSeconds;
                var piece = Math.Min(left, room);
                var day = cur.Date;
                var weekday = ((int)day.DayOfWeek + 6) % 7;
                foreach (var acc in accs)
                {
                    if (!acc.Includes(day)) continue;
                    acc.TotalS += piece;
                    acc.Days[day] = acc.Days.GetValueOrDefault(day) + piece;
                    acc.Hours[cur.Hour] += piece;
                    acc.Week[weekday, cur.Hour] += piece;
                    Song(acc, id, album).S += piece;
                    foreach (var a in id.Artists)
                    {
                        var ag = Get(acc.Artists, a.Key, a.Name, "");
                        ag.S += piece;
                        (ag.Songs ??= new()).Add(id.Key);
                    }
                    if (album != null)
                    {
                        var al = Get(acc.Albums, album.Key, album.Name, album.Artist);
                        al.S += piece;
                        (al.Songs ??= new()).Add(id.Key);
                    }
                    else acc.NoAlbumS += piece;
                    Get(acc.Players, player, player, "").S += piece;
                    if (cur.Hour < 5)
                    {
                        var end = piece >= room ? hourEnd.AddSeconds(-1) : cur.AddSeconds(piece);
                        if (acc.Owl == null || end.TimeOfDay > acc.Owl.Value.At) acc.Owl = (day, end.TimeOfDay, id.Title);
                    }
                }
                left -= piece;
                cur = piece >= room ? hourEnd : cur.AddSeconds(piece);
            }
        }
    }

    private static Agg Get(Dictionary<string, Agg> map, string key, string name, string sub)
    {
        if (!map.TryGetValue(key, out var a)) map[key] = a = new Agg { Key = key };
        // 显示名取最近一次的写法：片段按时间顺序进来，后来的覆盖先前的
        a.Name = name;
        a.Sub = sub;
        return a;
    }

    /// <summary>歌曲榜的一项。配图跟着最近一次知道专辑的那次播放走，没专辑的那几次不擦掉它。</summary>
    private static Agg Song(RangeAcc acc, SongId id, AlbumId? album)
    {
        var a = Get(acc.Songs, id.Key, id.Title, id.Artist);
        if (album != null) a.Album = album.Key;
        return a;
    }

    private static SongId Identify(string title, string artist)
    {
        var artists = Lyrics.ArtistSeparatorRegex().Split(artist)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Select(a => (Key: Norm(a), Name: a))
            .DistinctBy(a => a.Key)
            .ToArray();
        var key = Norm(title) + "\u0001" + string.Join("\u0001", artists.Select(a => a.Key).Order(StringComparer.Ordinal));
        return new SongId(key, title, artist, artists);
    }

    /// <summary>归一化后为空（歌名全是标点、如「...」）时退回原文，免得这类歌全被并成一首。</summary>
    private static string Norm(string s)
    {
        var n = Lyrics.NormalizeForMatch(s);
        return n.Length > 0 ? n : s.Trim().ToLowerInvariant();
    }

    private static StatsRangeView BuildRange(StatsRange r, RangeAcc acc, DateTime today, DateTime firstDay, Ctx ctx)
    {
        var (heat, heatText) = Heat(acc.Week, acc.TotalS);
        if (acc.TotalS <= 0 && acc.Plays == 0)
            return new StatsRangeView("这段时间还没有听歌记录。", StatsRankPair.Empty, StatsRankPair.Empty,
                StatsRankPair.Empty, "", StatsRankPair.Empty, "", HourBars(acc.Hours, -1), "", heat, "", []);

        var head = r switch
        {
            StatsRange.Week => "这 7 天你听了",
            StatsRange.Month => "这 30 天你听了",
            StatsRange.Year => "今年你听了",
            _ => $"从 {firstDay.Year} 年 {firstDay.Month} 月 {firstDay.Day} 日开始，你一共听了",
        };
        var summary = $"{head} {FormatDuration(acc.TotalS)}，播放 {acc.Plays} 次，共 {acc.Songs.Count} 首歌";

        // 新歌：记录是在这段时间里才开始的话，每首歌都是「第一次」，列出来就没意义了
        var newSongs = StatsRankPair.Empty;
        string newNote;
        if (acc.From is not { } from)
        {
            summary += $"、{acc.Artists.Count} 位歌手";
            newNote = "「全部」里的每首歌都是第一次听，换个时间范围看看这段时间新发现了哪些歌。";
        }
        else if (firstDay >= from)
            newNote = $"记录是从 {firstDay.Month} 月 {firstDay.Day} 日才开始的，这段时间里每首歌都是第一次听。";
        else
        {
            var fresh = acc.Songs.Values.Where(a => ctx.SongFirstDay[a.Key] >= from).ToList();
            summary += fresh.Count > 0 ? $"，其中 {fresh.Count} 首是第一次听" : "，都是听过的老朋友";
            newSongs = RankPair(fresh, a => $"{a.Sub} · {ShortDate(ctx.SongFirstDay[a.Key], today)} 第一次听", TopN, StatsArtKind.Song);
            newNote = fresh.Count > 0 ? "" : "这段时间听的都是以前听过的歌。";
        }
        summary += "。";

        var players = acc.Players.Values.OrderByDescending(a => a.S).Take(MaxPlayers).ToList();
        var playerRows = players.Select((a, i) => new StatsRankRow(i + 1, a.Name, "",
            $"{Percent(a.S, acc.TotalS)} · {FormatDuration(a.S)}", Ratio(a.S, acc.TotalS))).ToList();

        var peak = Peak(acc.Hours);
        return new StatsRangeView(summary,
            RankPair(acc.Songs.Values, a => a.Sub, TopN, StatsArtKind.Song),
            RankPair(acc.Artists.Values, a => $"{a.Songs?.Count ?? 0} 首歌", TopN, StatsArtKind.Artist),
            RankPair(acc.Albums.Values, AlbumSub, TopN, StatsArtKind.Album), AlbumNote(acc),
            newSongs, newNote,
            HourBars(acc.Hours, peak.Hour), PeakText(acc, peak), heat, heatText, playerRows);
    }

    private static string AlbumSub(Agg a)
        => a.Sub.Length > 0 ? $"{a.Sub} · {a.Songs?.Count ?? 0} 首歌" : $"{a.Songs?.Count ?? 0} 首歌";

    /// <summary>专辑榜底下的说明：旧版本不记专辑、有的播放器也不报，缺得多时要说清楚榜单不全。</summary>
    private static string AlbumNote(RangeAcc acc)
    {
        if (acc.TotalS <= 0) return "";
        if (acc.Albums.Count == 0) return "这段时间的记录里没有专辑信息：旧版本不记专辑，有的播放器也不报。";
        return acc.NoAlbumS / acc.TotalS >= AlbumNoteRatio
            ? $"有 {Percent(acc.NoAlbumS, acc.TotalS)} 的时长不知道是哪张专辑，没算进来。"
            : "";
    }

    /// <summary>两种排法各取前 n 名。同分时按名字排，免得每次刷新顺序乱跳。</summary>
    private static StatsRankPair RankPair(IEnumerable<Agg> items, Func<Agg, string> sub, int n, StatsArtKind kind)
    {
        var list = items as IReadOnlyCollection<Agg> ?? items.ToList();
        var byPlays = list.OrderByDescending(a => a.P).ThenByDescending(a => a.S)
            .ThenBy(a => a.Name, StringComparer.Ordinal).Take(n).ToList();
        var byTime = list.OrderByDescending(a => a.S).ThenByDescending(a => a.P)
            .ThenBy(a => a.Name, StringComparer.Ordinal).Take(n).ToList();
        var topP = byPlays.Count > 0 ? byPlays[0].P : 0;
        var topS = byTime.Count > 0 ? byTime[0].S : 0;
        string art(Agg a) => kind switch
        {
            StatsArtKind.Song => a.Album != null ? AlbumArtPrefix + a.Album : SongArtPrefix + a.Key,
            StatsArtKind.Album => AlbumArtPrefix + a.Key,
            _ => ArtistArtPrefix + a.Key,
        };
        // 右侧文字把当前排序依据放在前面
        return new StatsRankPair(
            byPlays.Select((a, i) => new StatsRankRow(i + 1, a.Name, sub(a),
                a.P > 0 ? $"{a.P} 次 · {FormatDuration(a.S)}" : FormatDuration(a.S),
                topP > 0 ? (double)a.P / topP : Ratio(a.S, topS), kind, art(a))).ToList(),
            byTime.Select((a, i) => new StatsRankRow(i + 1, a.Name, sub(a),
                a.P > 0 ? $"{FormatDuration(a.S)} · {a.P} 次" : FormatDuration(a.S),
                Ratio(a.S, topS), kind, art(a))).ToList());
    }

    /// <summary>高峰时段：连续 3 小时听得最多的那一段，允许跨午夜（23 点到次日 1 点）。
    /// 一样多时取中间那个钟点听得多的：只在 19 点听过歌时，17、18、19 起头的三段一样多，
    /// 先到先得会取 17:00–20:00，按中间的 18 点叫成「傍晚」，高亮的三根柱子也有两根是空的。</summary>
    private static (int Hour, double S) Peak(double[] hours)
    {
        var peak = -1;
        var peakS = 0.0;
        for (var h = 0; h < 24; h++)
        {
            var s = hours[h] + hours[(h + 1) % 24] + hours[(h + 2) % 24];
            if (s > peakS || s == peakS && peak >= 0 && hours[(h + 1) % 24] > hours[(peak + 1) % 24])
            {
                peakS = s;
                peak = h;
            }
        }
        return (peak, peakS);
    }

    private static string PeakText(RangeAcc acc, (int Hour, double S) peak) => peak.Hour < 0 ? "" :
        $"你最常在{PeriodName((peak.Hour + 1) % 24)}听歌：{peak.Hour:00}:00–{(peak.Hour + 3) % 24:00}:00 这三个小时占了 {Percent(peak.S, acc.TotalS)}。";

    /// <summary>按高峰三小时的中间那个钟点起名。</summary>
    private static string PeriodName(int hour) => hour switch
    {
        < 5 => "深夜",
        < 8 => "清晨",
        < 12 => "上午",
        < 17 => "午后",
        < 19 => "傍晚",
        _ => "夜晚",
    };

    /// <summary>星期 × 钟点热力图的 168 格，外加一句话说出最集中的那一格。</summary>
    private static (List<StatsHeatCell> Cells, string Text) Heat(double[,] week, double total)
    {
        var cells = new List<StatsHeatCell>(7 * 24);
        int bestDay = -1, bestHour = -1;
        var best = 0.0;
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
            {
                var v = week[d, h];
                cells.Add(new StatsHeatCell(v,
                    $"{WeekdayNames[(d + 1) % 7]} {h:00}:00–{(h + 1) % 24:00}:00 · {FormatDuration(v)}（{Percent(v, total)}）"));
                if (v > best) (best, bestDay, bestHour) = (v, d, h);
            }
        var text = bestDay < 0 ? "" :
            $"最集中的一格是{WeekdayNames[(bestDay + 1) % 7]} {bestHour:00}:00–{(bestHour + 1) % 24:00}:00，占了 {Percent(best, total)}。";
        return (cells, text);
    }

    private static List<StatsBar> HourBars(double[] hours, int peak)
    {
        var total = hours.Sum();
        bool InPeak(int h) => peak >= 0 && (h - peak + 24) % 24 < 3;
        return Enumerable.Range(0, 24).Select(h => new StatsBar(
            h % 3 == 0 ? h.ToString() : "",
            hours[h],
            $"{h:00}:00–{(h + 1) % 24:00}:00 · {FormatDuration(hours[h])}（{Percent(hours[h], total)}）",
            InPeak(h))).ToList();
    }

    private static List<StatsBar> DayBars(Dictionary<DateTime, double> days, DateTime today)
    {
        var bars = new List<StatsBar>();
        for (var i = 29; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            var v = days.GetValueOrDefault(d);
            bars.Add(new StatsBar(i % 5 == 0 ? $"{d.Month}/{d.Day}" : "", v,
                $"{d.Month} 月 {d.Day} 日 {WeekdayNames[(int)d.DayOfWeek]}{(i == 0 ? "（今天）" : "")} · {FormatDuration(v)}",
                i == 0));
        }
        return bars;
    }

    // 柱子都截到今天，和概览一致：系统时间曾被调到将来时留下的记录不算进本周、本月
    private static List<StatsBar> WeekBars(Dictionary<DateTime, double> days, DateTime weekStart, DateTime today)
    {
        var bars = new List<StatsBar>();
        for (var i = 11; i >= 0; i--)
        {
            var from = weekStart.AddDays(-7 * i);
            var to = from.AddDays(6);
            var hi = to < today ? to : today;
            var v = days.Where(kv => kv.Key >= from && kv.Key <= hi).Sum(kv => kv.Value);
            bars.Add(new StatsBar($"{from.Month}/{from.Day}", v,
                $"{from.Month}/{from.Day} – {to.Month}/{to.Day}{(i == 0 ? "（本周）" : "")} · {FormatDuration(v)}",
                i == 0));
        }
        return bars;
    }

    private static List<StatsBar> MonthBars(Dictionary<DateTime, double> days, DateTime monthStart, DateTime today)
    {
        var bars = new List<StatsBar>();
        for (var i = 11; i >= 0; i--)
        {
            var m = monthStart.AddMonths(-i);
            var v = days.Where(kv => kv.Key.Year == m.Year && kv.Key.Month == m.Month && kv.Key <= today).Sum(kv => kv.Value);
            // 一月写上年份，一年的分界一眼能看出来
            bars.Add(new StatsBar(m.Month == 1 ? $"{m.Year}" : $"{m.Month} 月", v,
                $"{m.Year} 年 {m.Month} 月{(i == 0 ? "（本月）" : "")} · {FormatDuration(v)}",
                i == 0));
        }
        return bars;
    }

    /// <summary>年报的 12 个月，听得最多的那个月高亮。</summary>
    private static List<StatsBar> YearMonthBars(Dictionary<DateTime, double> days, int year)
    {
        var values = new double[12];
        foreach (var (d, v) in days)
            if (d.Year == year) values[d.Month - 1] += v;
        var max = values.Max();
        return values.Select((v, i) => new StatsBar($"{i + 1}", v,
            $"{i + 1} 月 · {FormatDuration(v)}", max > 0 && v == max)).ToList();
    }

    private static List<StatsFact> Facts(RangeAcc all, DateTime today, DateTime firstDay)
    {
        var facts = new List<StatsFact>();
        if (all.Days.Count == 0) return facts;

        facts.Add(BestDayFact(all, today)!);
        if (LoopFact(all, today) is { } loop) facts.Add(loop);
        if (OwlFact(all, today) is { } owl) facts.Add(owl);
        if (LongestStreakFact(all, today) is { } longest) facts.Add(longest);

        // 当前连续：今天还没听够时，从昨天往前数——今天还没过完，不算断
        var set = all.Days.Where(kv => kv.Value >= StreakMinS).Select(kv => kv.Key).ToHashSet();
        var streakEnd = set.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        for (var d = streakEnd; set.Contains(d); d = d.AddDays(-1)) streak++;
        facts.Add(new("当前连续听歌", $"{streak} 天",
            streak == 0 ? "今天听满 1 分钟就能开始新的连续纪录"
            : set.Contains(today) ? "今天也听过了" : "今天再听一会儿就能续上"));

        var nth = $"今天是第 {Math.Max(1, (today - firstDay).Days + 1)} 天";
        facts.Add(new("一起听歌的第一天", ShortDate(firstDay, DateTime.MinValue),
            all.First is { } first ? $"第一首是《{first.Song.Title}》，{nth}" : nth));
        return facts;
    }

    // ---- 趣味发现：首页（全部记录）和年报（某一年）共用，日期按 reference 那年决定写不写年份 ----

    private static StatsFact? BestDayFact(RangeAcc acc, DateTime reference)
    {
        if (acc.Days.Count == 0) return null;
        var best = acc.Days.MaxBy(kv => kv.Value);
        return new("听歌最多的一天", FormatDuration(best.Value), DateText(best.Key, reference));
    }

    private static StatsFact? LoopFact(RangeAcc acc, DateTime reference)
    {
        if (acc.Loops.Count == 0) return null;
        var loop = acc.Loops.MaxBy(kv => kv.Value);
        return loop.Value >= 3 && acc.Songs.TryGetValue(loop.Key.Song, out var song)
            ? new("单日循环纪录", $"{loop.Value} 遍", $"{DateText(loop.Key.Day, reference)}，单曲循环《{song.Name}》")
            : null;
    }

    private static StatsFact? OwlFact(RangeAcc acc, DateTime reference)
        => acc.Owl is { } o
            ? new("最晚的一次夜猫子", $"凌晨 {o.At.Hours}:{o.At.Minutes:00}", $"{DateText(o.Day, reference)}，还在听《{o.Title}》")
            : null;

    /// <summary>连续听歌：一天累计满 1 分钟才算，免得开机时自动续播的几秒钟也被算成打卡。</summary>
    private static StatsFact? LongestStreakFact(RangeAcc acc, DateTime reference)
    {
        var heard = acc.Days.Where(kv => kv.Value >= StreakMinS).Select(kv => kv.Key).Order().ToList();
        if (heard.Count == 0) return null;
        int bestLen = 1, len = 1;
        var bestEnd = heard[0];
        for (var i = 1; i < heard.Count; i++)
        {
            len = heard[i] == heard[i - 1].AddDays(1) ? len + 1 : 1;
            if (len > bestLen) { bestLen = len; bestEnd = heard[i]; }
        }
        var bestStart = bestEnd.AddDays(1 - bestLen);
        return new("最长连续听歌", $"{bestLen} 天", bestLen == 1
            ? DateText(bestEnd, reference)
            : $"{ShortDate(bestStart, reference)} – {ShortDate(bestEnd, reference)}");
    }

    /// <summary>「10 月 3 日 周五」，不是今年的带上年份。</summary>
    private static string DateText(DateTime d, DateTime today)
        => (d.Year == today.Year ? "" : $"{d.Year} 年 ") + $"{d.Month} 月 {d.Day} 日 {WeekdayNames[(int)d.DayOfWeek]}";

    private static string ShortDate(DateTime d, DateTime today)
        => d.Year == today.Year ? $"{d.Month}/{d.Day}" : $"{d.Year}/{d.Month}/{d.Day}";

    private static double Ratio(double v, double max) => max > 0 ? Math.Clamp(v / max, 0, 1) : 0;

    private static string Percent(double v, double total)
    {
        if (total <= 0) return "0%";
        var p = v / total * 100;
        return p >= 10 || p == 0 ? $"{p:0}%" : $"{p:0.#}%";
    }

    /// <summary>「3 小时 12 分」「45 分」「不到 1 分钟」。</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds < 1) return "0 分";
        if (seconds < 60) return "不到 1 分钟";
        var minutes = (long)(seconds / 60);
        long h = minutes / 60, m = minutes % 60;
        if (h == 0) return $"{m} 分";
        // 上百小时还精确到分钟没有意义，也挤不下
        return m == 0 || h >= 100 ? $"{h} 小时" : $"{h} 小时 {m} 分";
    }

    // 已知播放器的 AUMID 片段（小写）→ 显示名；认不出的由 PlayerName 从 AUMID 里截个名字
    private static readonly (string Needle, string Name)[] KnownPlayers =
    {
        ("cloudmusic", "网易云音乐"),
        ("qqmusic", "QQ 音乐"),
        ("kugou", "酷狗音乐"),
        ("kwmusic", "酷我音乐"),
        ("kuwo", "酷我音乐"),
        ("lx-music", "洛雪音乐"),
        ("spotify", "Spotify"),
        ("applemusic", "Apple Music"),
        ("zunemusic", "媒体播放器"),
        ("foobar2000", "foobar2000"),
        ("musicbee", "MusicBee"),
        ("aimp", "AIMP"),
        ("potplayer", "PotPlayer"),
        ("msedge", "Edge"),
        ("chrome", "Chrome"),
        ("firefox", "Firefox"),
    };

    /// <summary>SMTC 来源 → 给人看的播放器名。</summary>
    internal static string PlayerName(string src)
    {
        var s = src.Trim();
        if (s.Length == 0) return "未知播放器";
        var lower = s.ToLowerInvariant();
        foreach (var (needle, name) in KnownPlayers)
            if (lower.Contains(needle)) return name;
        if (IsFirefoxId(s)) return "Firefox";
        // 商店应用是「发布者.应用名_哈希!入口」，取应用名；桌面程序是 exe 名
        var bang = s.IndexOf('!');
        if (bang > 0)
        {
            s = s[..bang];
            var us = s.IndexOf('_');
            if (us > 0) s = s[..us];
            var dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        }
        else if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];
        return s;
    }

    /// <summary>Firefox 报的不是 exe 名，而是按安装目录算出的 16 位十六进制哈希（默认装法是 308046B0AF4A39CB），
    /// 装的位置、版本不同哈希也不同，只能按形状认。</summary>
    private static bool IsFirefoxId(string src) => src.Length == 16 && src.All(char.IsAsciiHexDigit);

    // 浏览器和内嵌网页的宿主（微信的小程序、公众号、视频号都跑在 WeChatAppEx 里）。
    // 比的是子串：msedge 连带 Edge 内核的 WebView2 宿主，chrome 连带 360 极速这类改名的 Chromium
    private static readonly string[] WebHosts =
        ["chrome", "msedge", "firefox", "wechatappex", "brave", "vivaldi", "opera", "qqbrowser", "360se", "sogouexplorer"];

    /// <summary>这一播是不是歌，不是的不进统计。播放器标明是视频、图片的不算；
    /// 浏览器报给 SMTC 的类型一律是 music，只能看歌手：网页播放器放歌会填歌手，
    /// 小红书这类视频的歌手一栏是空的（SMTC 里只有「标题 - 小红书」）。
    /// 专门的音乐软件不看歌手：本地文件没打标签时歌手也是空的，那照样是歌。</summary>
    internal static bool IsSong(string src, string artist, string? type)
    {
        if (type is "video" or "image") return false;
        if (artist.Trim().Length > 0) return true;
        var s = src.Trim();
        return !IsFirefoxId(s) && !WebHosts.Any(h => s.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
