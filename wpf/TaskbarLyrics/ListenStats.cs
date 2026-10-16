// 听歌记录：把 SMTC 轮询到的播放状态折算成一段段「听歌片段」，追加进本地 JSONL，供统计窗口汇总。
//
// 记的是片段而不是「每首歌一条」：同一首歌暂停、续播、单曲循环都会产生多段，汇总时再按歌合并。
// 片段在结束的那一刻落盘（暂停、切歌、换播放器），另外每满 5 分钟强制切一段——
// 程序被强杀或断电时最多丢最近这 5 分钟，而不是一整首循环了一下午的歌。
//
// 数据只存在本机（config.json 旁边的 stats 目录），不上传任何地方。
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarLyrics;

/// <summary>一段连续播放：同一首歌、同一个播放器、中间没暂停。
/// 字段名刻意取短，一年下来能省下近一半的体积。必须是带 setter 的 class，
/// 理由见 LyricsCache 存储格式那段注释（位置参数 record 反序列化会静默得到空壳）。</summary>
internal sealed class ListenSegment
{
    public long T { get; set; }        // 片段开始时刻（UTC unix 秒）
    public int O { get; set; }         // 当时的本地时区偏移（分钟）：「几点听的」要按当时当地的钟点算
    public double S { get; set; }      // 片段内实际听的秒数
    public string Ti { get; set; } = "";
    public string Ar { get; set; } = "";
    public string Src { get; set; } = ""; // 播放器（SMTC 的 SourceAppUserModelId）
    public int P { get; set; }         // 本片段新增的播放次数

    // ---- 以下是 v2 起多记的，v1 的行读出来全是默认值。暂时没全用上，先记下来，
    // 以后加新的统计维度时旧数据也能算：这类信息事后是补不回来的 ----
    public int V { get; set; }         // 格式版本：v1 没有这个字段（读出来是 0），本版写 2
    public string? Al { get; set; }    // 专辑（SMTC 报的）
    public string? Aa { get; set; }    // 专辑歌手
    public int Tn { get; set; }        // 曲目号
    public string[]? Ge { get; set; }  // 流派（最多 5 个）
    public string? Ty { get; set; }    // 播放类型：music | video | image
    public bool? Sh { get; set; }      // 随机播放开着没有，播放器不报时为 null
    public string? Rp { get; set; }    // 循环模式：none | track | list
    public double D { get; set; }      // 已知时长（秒），0 为未知
    public bool Dl { get; set; }       // 时长来自歌词库而非播放器（此时进度是本地估算的）
    public double Ps { get; set; }     // 片段开始时的播放进度（秒）
    public double Pe { get; set; }     // 片段结束时的播放进度；单曲循环转回开头时会比 Ps 小
    public string? E { get; set; }     // 片段为什么结束：pause/song/src/none/cap/tz/jump/exit，正在累计的那段为 null
    public string? Lid { get; set; }   // 歌词匹配到的曲目：ne:<id> / qq:<mid> / lrc:<id>
    public string? Lal { get; set; }   // 歌词库登记的专辑（播放器不报专辑时的备用）
}

/// <summary>歌词接口对当前这首歌的匹配结果。Key 对得上正在放的歌才会被采用：
/// 切歌后旧歌的结果可能还挂着，不能记到新歌头上。
/// Cover / Artists 只给 ArtCollector 存图用，不写进片段记录。</summary>
internal sealed record LyricsMatch(string Key, double DurS, string? Id, string? Album,
    string? Cover = null, IReadOnlyList<Lyrics.ArtRef>? Artists = null);

/// <summary>片段文件的读写。追加用的实例带一个失败重试队列；读、清空与实例无关，做成静态的，
/// --settings 单开的设置进程没有记录器，也要能看统计、清数据。</summary>
internal sealed class StatsStore
{
    private const int MaxQueued = 200;

    private readonly Func<string> _dir;
    private readonly List<ListenSegment> _queue = new(); // 写盘失败的片段，下次一起再试
    private bool _errorLogged;

    public StatsStore(Func<string> dir) => _dir = dir;

    /// <summary>写入目录，跟着配置文件走（与 LyricsCache.Dir 同理：配置转存 %AppData% 时一起过去）。
    /// 每次取值都重算：ConfigPath 会在首次存盘失败后切换。</summary>
    public static string DefaultDir => DirOf(AppConfig.ConfigPath);

    /// <summary>读和清空要看的全部目录：正在写的这个，加上配置可能待过的两处（exe 旁边、%AppData%）。
    /// 配置一旦转存到退路，新片段就写到另一处去了，原处攒下的历史不能就此从统计里消失，清空也得两处都删。
    /// 每个片段只写进其中一处，合起来读不会重复；exe 本身就放在 %AppData%\TaskbarLyrics 时两处是同一个目录，要去重。</summary>
    public static IReadOnlyList<string> AllDirs =>
        new[] { DefaultDir, DirOf(AppConfig.PrimaryPath), DirOf(AppConfig.FallbackPath) }
            .DistinctBy(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)), StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string DirOf(string configPath) =>
        Path.Combine(Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory, "stats");

    public string Dir => _dir();

    public IReadOnlyList<ListenSegment> Queued => _queue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, // P=0、O=0 这类不写出
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,          // 中文原样写，文件能直接看懂
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public void Append(ListenSegment seg)
    {
        _queue.Add(seg);
        // 写盘长期失败（目录只读、磁盘满）时只留最近这些，内存不能跟着无限涨
        if (_queue.Count > MaxQueued) _queue.RemoveRange(0, _queue.Count - MaxQueued);
        Flush();
    }

    /// <summary>把队列里的片段写盘，写成功的出队。</summary>
    public void Flush()
    {
        if (_queue.Count == 0) return;
        try
        {
            var dir = _dir();
            Directory.CreateDirectory(dir);
            foreach (var group in _queue.GroupBy(FileNameOf).ToList())
            {
                AppendLines(Path.Combine(dir, group.Key), group);
                var done = group.ToHashSet(ReferenceEqualityComparer.Instance);
                _queue.RemoveAll(done.Contains);
            }
            _errorLogged = false;
        }
        catch (Exception ex)
        {
            // 只记第一次：每次暂停、切歌都会再试，失败时会把 error.log 刷满
            if (!_errorLogged)
            {
                _errorLogged = true;
                Log.Error("stats-write", ex);
            }
        }
    }

    public void DropQueue() => _queue.Clear();

    /// <summary>按片段开始时的当地月份分文件：一个月一个，删旧数据时按文件名就能挑。</summary>
    private static string FileNameOf(ListenSegment s)
        => LocalStart(s).ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>片段开始时刻按当时当地的钟点还原。</summary>
    public static DateTime LocalStart(ListenSegment s)
        => DateTimeOffset.FromUnixTimeSeconds(s.T).ToOffset(TimeSpan.FromMinutes(s.O)).DateTime;

    private static void AppendLines(string path, IEnumerable<ListenSegment> segs)
    {
        var sb = new StringBuilder();
        foreach (var s in segs) sb.Append(JsonSerializer.Serialize(s, JsonOptions)).Append('\n');
        // 允许别人同时读（统计窗口在后台线程读）和删（清空数据）
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        RepairTail(fs);
        fs.Seek(0, SeekOrigin.End);
        // 一次写完整批：中途失败最多留下半行，下次追加前由 RepairTail 补上换行隔开，读时当坏行跳过
        fs.Write(Utf8NoBom.GetBytes(sb.ToString()));
        fs.Flush(); // 交给系统，到这里这批就算写进去了
        // 再请系统刷到磁盘，断电也不丢。有的网络盘、虚拟盘不支持这一步会报错，可数据其实已经写进去了，
        // 不能当成写失败：那样这批会留在重试队列里，下次再追加一遍，统计成倍虚高
        try { fs.Flush(flushToDisk: true); }
        catch (IOException) { }
    }

    /// <summary>追加前收拾文件尾：断电时 NTFS 可能已把文件长度记上、数据却没落盘，
    /// 尾巴是一串 \0；上次写到一半则缺个换行。不收拾的话新的一行会接在坏尾巴后面，一起被当坏行丢掉。</summary>
    private static void RepairTail(FileStream fs)
    {
        var len = fs.Length;
        if (len == 0) return;
        var buf = new byte[4096];
        var end = len;
        byte last = 0;
        while (end > 0)
        {
            var n = (int)Math.Min(buf.Length, end);
            fs.Seek(end - n, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, n);
            var i = n - 1;
            while (i >= 0 && buf[i] == 0) i--;
            if (i >= 0)
            {
                end = end - n + i + 1;
                last = buf[i];
                break;
            }
            end -= n;
        }
        if (end < len) fs.SetLength(end);
        if (end > 0 && last != (byte)'\n')
        {
            fs.Seek(end, SeekOrigin.Begin);
            fs.WriteByte((byte)'\n');
        }
    }

    // 片段开始时刻的合理范围（2000 年至 2200 年）。越界的 T 会让还原当地时间时抛异常
    private const long MinT = 946684800, MaxT = 7258118400;

    /// <summary>读出几个目录里的全部片段。</summary>
    public static List<ListenSegment> ReadAll(IEnumerable<string> dirs) => dirs.SelectMany(ReadAll).ToList();

    /// <summary>读出目录里的全部片段。坏行逐行跳过：一行写坏不该毁掉整份统计。</summary>
    public static List<ListenSegment> ReadAll(string dir)
    {
        var list = new List<ListenSegment>();
        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return list;
            files = Directory.GetFiles(dir, "*.jsonl");
        }
        catch (Exception ex)
        {
            Log.Error("stats-read", ex);
            return list;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                while (sr.ReadLine() is { } raw)
                {
                    var line = raw.Trim('\0', ' ', '\t', '\r');
                    if (line.Length == 0) continue;
                    ListenSegment? s;
                    try { s = JsonSerializer.Deserialize<ListenSegment>(line, JsonOptions); }
                    catch (JsonException) { continue; }
                    // 手改过或写坏的行可能带 null、离谱的时长，或者越界的时刻、时区（还原当地时间时会抛）
                    if (s == null || s.T < MinT || s.T > MaxT || s.S <= 0 || s.S > 86400 || Math.Abs(s.O) > 14 * 60
                        || string.IsNullOrEmpty(s.Ti) || s.P < 0)
                        continue;
                    s.Ar ??= "";
                    s.Src ??= "";
                    // 早先的版本没筛，刷过的视频已经记在盘上了：读的时候滤掉，文件不动，判断改了也不丢数据
                    if (!StatsReport.IsSong(s.Src, s.Ar, s.Ty)) continue;
                    Sanitize(s);
                    list.Add(s);
                }
            }
            catch (Exception ex)
            {
                Log.Error("stats-read", ex);
            }
        }
        return list;
    }

    /// <summary>v2 字段坏了只清掉那个字段、不丢整行：时长和歌名还是好的，统计照算。</summary>
    private static void Sanitize(ListenSegment s)
    {
        static double Num(double v, double max) => double.IsFinite(v) && v >= 0 && v <= max ? v : 0;
        static string? Str(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
        s.D = Num(s.D, 86400);
        s.Ps = Num(s.Ps, double.MaxValue);
        s.Pe = Num(s.Pe, double.MaxValue);
        if (s.Tn < 0) s.Tn = 0;
        s.Al = Str(s.Al);
        s.Aa = Str(s.Aa);
        s.Ty = Str(s.Ty);
        s.Rp = Str(s.Rp);
        s.E = Str(s.E);
        s.Lid = Str(s.Lid);
        s.Lal = Str(s.Lal);
        if (s.Ge != null)
        {
            var ge = s.Ge.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
            s.Ge = ge.Length > 0 ? ge : null;
        }
    }

    /// <summary>几个目录一起清空，全都删干净才算成功。</summary>
    public static bool Clear(IEnumerable<string> dirs)
    {
        var ok = true;
        foreach (var dir in dirs) ok &= Clear(dir); // 不能用 &&：前一个失败了后面的也要删
        return ok;
    }

    /// <summary>删掉全部片段文件，返回是否删干净了。</summary>
    public static bool Clear(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            var ok = true;
            foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
            {
                try { File.Delete(f); }
                catch (Exception ex)
                {
                    ok = false;
                    Log.Error("stats-clear", ex);
                }
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("stats-clear", ex);
            return false;
        }
    }
}

/// <summary>听歌计时的状态机，挂在 SMTC 轮询的回调上（线程池里跑，约 0.5 秒一跳）。
///
/// 每一跳把与上一跳之间的时长记给「上一跳看到的状态」：上一跳在放，这段时间才算听了；
/// 暂停之后的时长不算。全部操作在一把锁里，统计窗口取快照、关机时落盘都可能来自别的线程。</summary>
internal sealed class ListenRecorder
{
    internal const double MaxTickS = 3;      // 单跳最多记这么多：卡顿、轮询退避时不凭空多记
    internal const double WallJumpS = 5;     // 墙钟与单调钟的步差超过它，视为休眠过或改过时间
    internal const double MaxSegmentS = 300; // 片段满这么久强制落盘一次
    internal const double MinDurS = 10;      // 短于它的时长不可信（网易云的 SMTC 时长恒为 0）
    internal const double MetaSettleS = 5;   // 切歌后这么久内专辑等信息以最新的为准，之后只补空
    internal const int FormatVersion = 2;

    private readonly object _lock = new();
    private readonly StatsStore _store;
    private readonly Func<double> _mono;
    private readonly Func<DateTimeOffset> _now;
    private bool _closed;

    // 上一跳的观测
    private bool _hasPrev;
    private bool _prevPlaying;
    private double _prevMono;
    private DateTimeOffset _prevWall;

    private OpenSegment? _seg; // 正在累计、尚未落盘的片段
    private double _tzRefreshedAt = double.NegativeInfinity;

    // 播放次数的累计按播放源分开放。自动选源时，正在放的播放器一暂停，显示就会被另一个停着的
    // 播放器顶走；若只跟上一跳比歌名，会把这当成切歌，一首歌听到一半的累计就被清零了
    private readonly Dictionary<string, PlayCounter> _counters = new();

    private sealed class OpenSegment
    {
        public DateTimeOffset Start;
        public string Key = "", Ti = "", Ar = "", Src = "";
        public double S;
        public int P;
        // v2 的附带信息，每跳从计数器抄过来（见 Sync）
        public double Ps, Pe, D;
        public bool Dl;
        public string Al = "", Aa = "", Ty = "", Rp = "";
        public int Tn;
        public string[] Ge = [];
        public bool? Sh;
        public string? Lid, Lal;
    }

    private sealed class PlayCounter
    {
        public string Key = "";
        public double SeenAt;    // 第一次看到这首歌的单调钟时刻
        public double ListenedS; // 这首歌在这个播放器上连续累计听了多久（跨暂停）
        public double DurS;      // 已知时长，0 为未知
        public bool DurFromLyrics;
        public int Counted;      // 已经记过几次
        // 这首歌的元数据，跟着计数器走：同一首歌暂停再续播时不用重新等播放器报
        public string Album = "", AlbumArtist = "", Type = "", Repeat = "";
        public int TrackNumber;
        public string[] Genres = [];
        public bool? Shuffle;
        public string? LyricsId, LyricsAlbum;
    }

    /// <param name="dir">片段目录，每次写盘时取值。</param>
    /// <param name="mono">单调钟（秒），默认 <see cref="Clock.Now"/>；自测时注入假时钟。</param>
    /// <param name="now">墙钟，默认 <see cref="LocalNow"/>。</param>
    public ListenRecorder(Func<string> dir, Func<double>? mono = null, Func<DateTimeOffset>? now = null)
    {
        _store = new StatsStore(dir);
        _mono = mono ?? (() => Clock.Now);
        _now = now ?? LocalNow;
    }

    /// <summary>本地时间，时区跟着系统变。TimeZoneInfo.Local 在进程里有缓存，系统换了时区
    /// （带着笔记本出差、开着自动设置时区）也不会自己刷新，常驻的程序会一直按旧时区记「几点听的」，
    /// 而且是写进文件、事后改不回来的。每分钟清一次缓存重读，换时区后最多一分钟跟上。</summary>
    private DateTimeOffset LocalNow()
    {
        var mono = _mono();
        if (mono - _tzRefreshedAt >= 60)
        {
            _tzRefreshedAt = mono;
            TimeZoneInfo.ClearCachedData();
        }
        return DateTimeOffset.Now;
    }

    public string Dir => _store.Dir;

    /// <summary>喂一跳播放状态。null 表示没有可记的播放（没在放歌、读失败、记录已关）。</summary>
    /// <param name="lyrics">歌词接口的匹配结果（备用时长、曲库 id、专辑），没有为 null。</param>
    public void Observe(PlaybackState? s, LyricsMatch? lyrics)
    {
        lock (_lock)
        {
            if (_closed) return;
            if (s != null && (s.Title.Length == 0 || !StatsReport.IsSong(s.SourceId, s.Artist, s.PlaybackType))) s = null;
            var mono = _mono();
            var wall = _now();
            if (_hasPrev)
            {
                var dt = mono - _prevMono;
                var wallDt = (wall - _prevWall).TotalSeconds;
                // 单调钟不含睡眠，墙钟含：两者步调一错开，说明中间睡过一觉或者改了系统时间。
                // 这一跳不计，片段在这里断开，免得一段的开始时刻和时长对不上
                if (Math.Abs(wallDt - dt) > WallJumpS) CloseSegment("jump");
                else if (_prevPlaying && dt > 0) Credit(Math.Min(dt, MaxTickS));
            }
            _hasPrev = true;
            _prevMono = mono;
            _prevWall = wall;
            _prevPlaying = s is { Playing: true };

            var c = s != null ? Track(s, lyrics, mono) : null;
            // 先把这一跳的进度和元数据抄进片段，再决定切不切：切歌这一跳的状态已经是下一首了，
            // 只有同一首歌才抄，旧歌的最后一段不能带上新歌的专辑
            if (_seg != null && c != null && _seg.Key == s!.Key && _seg.Src == s.SourceId) Sync(_seg, c, s, mono);
            if (_seg != null)
            {
                var reason = s == null ? "none"
                    // 自动选源时，旧播放器不在放了才会换到一个停着的会话上：新源没在放，说明是旧源自己停了，
                    // 按暂停记（旧播放器直接关掉也会记成这个，监听层不报的话分不出来）
                    : _seg.Src != s.SourceId ? (s.Playing ? "src" : "pause")
                    : _seg.Key != s.Key ? "song"
                    : !s.Playing ? "pause"
                    : _seg.S >= MaxSegmentS ? "cap"
                    // 时区变了也切：一段只记一个偏移，后半段不能跟着前半段按旧时区算钟点
                    : _seg.Start.Offset != wall.Offset ? "tz"
                    : null;
                if (reason != null) CloseSegment(reason);
            }
            if (_seg == null && c != null && s!.Playing)
            {
                _seg = new OpenSegment
                {
                    Start = wall, Key = s.Key, Ti = s.Title, Ar = s.Artist, Src = s.SourceId,
                    Ps = PositionOf(s, mono),
                };
                Sync(_seg, c, s, mono);
            }
        }
    }

    /// <summary>该源换了歌才重置计数；时长只在拿到可信值时更新。元数据的取舍：
    /// 切歌后头几秒播放器常先报歌名、专辑后到，甚至还挂着上一首的专辑，这段时间以最新的为准；
    /// 过了这段只补空不覆盖，免得切歌前一瞬冒出来的下一首的专辑改掉这首的。</summary>
    private PlayCounter Track(PlaybackState s, LyricsMatch? lyrics, double mono)
    {
        if (!_counters.TryGetValue(s.SourceId, out var c) || c.Key != s.Key)
            _counters[s.SourceId] = c = new PlayCounter { Key = s.Key, SeenAt = mono };
        var lyr = lyrics != null && lyrics.Key == s.Key ? lyrics : null;
        if (s.DurationS >= MinDurS)
        {
            c.DurS = s.DurationS;
            c.DurFromLyrics = false;
        }
        // 播放器报过可信时长的就不拿歌词库的顶掉：曲库里的版本时长未必一样
        else if (lyr is { DurS: >= MinDurS } && (c.DurS == 0 || c.DurFromLyrics))
        {
            c.DurS = lyr.DurS;
            c.DurFromLyrics = true;
        }

        var settling = mono - c.SeenAt < MetaSettleS;
        if (s.Album.Length > 0 && (settling || c.Album.Length == 0)) c.Album = s.Album;
        if (s.AlbumArtist.Length > 0 && (settling || c.AlbumArtist.Length == 0)) c.AlbumArtist = s.AlbumArtist;
        if (s.TrackNumber > 0 && (settling || c.TrackNumber == 0)) c.TrackNumber = s.TrackNumber;
        if (s.Genres.Length > 0 && (settling || c.Genres.Length == 0)) c.Genres = s.Genres;
        // 播放方式是用户随时会切的，取最新的
        if (s.Shuffle != null) c.Shuffle = s.Shuffle;
        if (s.Repeat.Length > 0) c.Repeat = s.Repeat;
        if (s.PlaybackType.Length > 0) c.Type = s.PlaybackType;
        // 匹配结果可能晚到，也可能被更好的结果替换；null 不擦掉已有的
        if (lyr?.Id != null) c.LyricsId = lyr.Id;
        if (lyr?.Album != null) c.LyricsAlbum = lyr.Album;
        return c;
    }

    private static void Sync(OpenSegment seg, PlayCounter c, PlaybackState s, double mono)
    {
        seg.Pe = PositionOf(s, mono);
        seg.D = c.DurS;
        seg.Dl = c.DurFromLyrics;
        seg.Al = c.Album;
        seg.Aa = c.AlbumArtist;
        seg.Tn = c.TrackNumber;
        seg.Ge = c.Genres;
        seg.Ty = c.Type;
        seg.Rp = c.Repeat;
        seg.Sh = c.Shuffle;
        seg.Lid = c.LyricsId;
        seg.Lal = c.LyricsAlbum;
    }

    /// <summary>播放进度按注入的单调钟插值（不用 CurrentPositionS：那个读的是真时钟，自测控制不了）。</summary>
    private static double PositionOf(PlaybackState s, double mono)
        => Math.Max(0, s.Playing ? s.BasePositionS + (mono - s.BaseTime) : s.BasePositionS);

    private void Credit(double dt)
    {
        var seg = _seg;
        if (seg == null) return;
        seg.S += dt;
        if (!_counters.TryGetValue(seg.Src, out var c) || c.Key != seg.Key) return;
        c.ListenedS += dt;
        // 只增不减：时长后来才知道、按新时长算出来的次数变少时，已经记下的不收回
        var plays = PlaysFor(c.ListenedS, c.DurS);
        if (plays <= c.Counted) return;
        seg.P += plays - c.Counted;
        c.Counted = plays;
    }

    /// <summary>听了多久算几次播放：听满 30 秒或半首（取短的）记第一次，
    /// 之后单曲循环每多听一整首再记一次。时长未知时只记第一次。</summary>
    internal static int PlaysFor(double listenedS, double durS)
    {
        var d = durS >= MinDurS ? durS : 0;
        var t = d > 0 ? Math.Min(30, d / 2) : 30;
        if (listenedS < t) return 0;
        return d > 0 ? 1 + (int)((listenedS - t) / d) : 1;
    }

    private void CloseSegment(string reason)
    {
        var seg = _seg;
        if (seg == null) return;
        _seg = null;
        if (seg.S < 1 && seg.P == 0) return; // 刚点播放就暂停的碎片，不值得占一行
        _store.Append(ToRecord(seg, reason));
    }

    private static ListenSegment ToRecord(OpenSegment seg, string? reason)
    {
        var d = Math.Round(seg.D, 1);
        // 插值进度可能越过时长（尾奏、播放器报的时长偏短），落盘时夹回去
        double Pos(double p)
        {
            p = Math.Round(Math.Max(0, p), 1);
            return d > 0 ? Math.Min(p, d) : p;
        }
        static string? Text(string v) => v.Length > 0 ? v : null;
        return new()
        {
            T = seg.Start.ToUnixTimeSeconds(),
            O = (int)Math.Round(seg.Start.Offset.TotalMinutes),
            // 记了次数的碎片可能不足 0.05 秒，舍入成 0 会被读端（包括旧版本）当坏行整行丢掉
            S = Math.Max(Math.Round(seg.S, 1), seg.P > 0 ? 0.1 : 0),
            Ti = seg.Ti,
            Ar = seg.Ar,
            Src = seg.Src,
            P = seg.P,
            V = FormatVersion,
            Al = Text(seg.Al),
            Aa = Text(seg.Aa),
            Tn = seg.Tn,
            Ge = seg.Ge.Length > 0 ? seg.Ge : null,
            Ty = Text(seg.Ty),
            Sh = seg.Sh,
            Rp = Text(seg.Rp),
            D = d,
            Dl = d > 0 && seg.Dl,
            Ps = Pos(seg.Ps),
            Pe = Pos(seg.Pe),
            E = reason,
            Lid = seg.Lid,
            Lal = seg.Lal,
        };
    }

    /// <summary>把手里的片段立刻落盘（关机、注销时用）。</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_closed) return;
            CloseSegment("exit");
            _store.Flush();
        }
    }

    /// <summary>落盘并停止记录，之后的 Observe 一律忽略。</summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_closed) return;
            CloseSegment("exit");
            _store.Flush();
            _closed = true;
        }
    }

    /// <summary>统计用的全量数据：盘上的 + 还没写进去的 + 正在累计的那段。
    /// 放在锁里读盘：读的同时恰好有片段落盘的话，它要么在文件里、要么还在队列里，不会两头都漏或都算。</summary>
    /// <param name="dirs">要读的目录（<see cref="StatsStore.AllDirs"/>），须包含正在写的那个。</param>
    public List<ListenSegment> Snapshot(IEnumerable<string> dirs)
    {
        lock (_lock)
        {
            var list = StatsStore.ReadAll(dirs);
            list.AddRange(_store.Queued);
            if (_seg is { S: > 0 } seg) list.Add(ToRecord(seg, null));
            return list;
        }
    }

    /// <summary>清空全部记录（包括手里还没落盘的），返回文件是否删干净了。</summary>
    public bool ClearAll(IEnumerable<string> dirs)
    {
        lock (_lock)
        {
            _seg = null;
            _counters.Clear();
            _store.DropQueue();
            return StatsStore.Clear(dirs);
        }
    }
}
