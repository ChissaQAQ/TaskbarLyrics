// 听歌统计的配图收集：播放时顺带把专辑封面、歌手头像存进 ArtCache。
//
// 图从哪来：
// - 封面先用播放器经 SMTC 给的那张（不联网）；没有、或是网易云的白占位图时，
//   用歌词接口匹配到的那首的封面地址——但只在匹配到的专辑和正在放的对得上时用，免得配错图。
// - 头像只有歌词接口给的线索：QQ 是现成地址，网易云是歌手 id，要再查一次头像地址。
//   名字必须归一化后完全相等才认，「Ai」不能拿「Aimer」的头像。
//
// 请求纪律（这些是曲库的公开接口，没有义务伺候我们）：
// 一首歌听满 8 秒才动手（快速切歌不下载）；单线程排队、两次请求至少隔 1 秒；
// 遇到 403/429/5xx 半小时内不再请求；地址确实没图（404/410、网易云的默认图）记下来，
// 7 天内不再查这个地址（见 ArtCache.IsNone）；同一个键配同一个来源，这次运行只排一次队。
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics;

internal sealed partial class ArtCollector : IDisposable
{
    private const double MinPlayS = 8;
    private const double MaxTickS = 3;  // 两跳之间的时长封顶：睡眠、卡顿不能一下凑满 8 秒
    private const long GapMs = 1000;
    private const long BreakerMs = 30 * 60 * 1000;

    private const string NeteaseReferer = "https://music.163.com";
    private const string QqReferer = "https://y.qq.com/";

    private sealed record Job(string Key, byte[]? Bytes, string? Ref);

    private readonly Func<bool> _enabled;
    private readonly object _lock = new();
    private readonly HashSet<string> _tried = new();
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // 只在轮询线程上读写
    private string? _songKey;
    private double _playedS;
    private long _prevTick;
    private LyricsMatch? _seenMatch;
    private byte[]? _seenCover;
    private string? _lastKey;                // 最后见到的歌，中途断一跳（读失败、开关拨了一下）也不清
    private byte[]? _curCover, _prevCover;   // 这首、上一首最后见到的 SMTC 封面
    private string? _curAlbum, _prevAlbum;   // 同上，播放器报的专辑名（归一化，不带歌手）

    // 只在后台工作线程上读写；用开机以来的毫秒数，改系统时间不会让熔断失效或一等好几天
    private long _lastRequest;
    private long _blockedUntil;

    public ArtCollector(Func<bool> enabled)
    {
        _enabled = enabled;
        Task.Run(WorkAsync);
    }

    /// <summary>喂一跳播放状态（轮询线程调用），很轻：只在条件满足时往队列里放活。</summary>
    public void Observe(PlaybackState? s, LyricsMatch? match)
    {
        var tick = Environment.TickCount64;
        var dt = Math.Clamp((tick - _prevTick) / 1000.0, 0, MaxTickS);
        _prevTick = tick;
        if (s == null || s.Title.Length == 0 || !_enabled())
        {
            _songKey = null;
            return;
        }
        var smtcAlbum = Lyrics.NormalizeForMatch(s.Album) is { Length: > 0 } an ? an : null;
        if (s.Key != _songKey)
        {
            _songKey = s.Key;
            _playedS = 0;
            _seenMatch = null;
            _seenCover = null;
            // 同一首断了一跳又回来不算换歌：不然「上一首的封面」就成了它自己的，会被当成旧图
            if (s.Key != _lastKey) (_prevCover, _prevAlbum) = (_curCover, _curAlbum);
            _lastKey = s.Key;
            (_curCover, _curAlbum) = (s.CoverBytes, smtcAlbum);
            return;
        }
        (_curCover, _curAlbum) = (s.CoverBytes, smtcAlbum);
        // 不是歌（网页里没歌手的视频之类）不收图，但上面照样记下它的封面：同一来源从视频切回歌、
        // 歌的封面又晚到时，读到的旧图是视频缩略图，得认得出那是上一项的（见 StaleCover）
        if (!StatsReport.IsSong(s.SourceId, s.Artist, s.PlaybackType)) return;
        if (s.Playing) _playedS += dt;
        if (_playedS < MinPlayS) return;

        // 等歌词接口的结果：抓取失败也会送来一份字段全空的，不会一直等下去。
        // 结果被更好的一份替换（重试拿到了网易云的）、或封面字节晚到时再看一遍
        var lyr = match != null && match.Key == s.Key ? match : null;
        if (lyr == null || ReferenceEquals(lyr, _seenMatch) && ReferenceEquals(s.CoverBytes, _seenCover)) return;
        _seenMatch = lyr;
        _seenCover = s.CoverBytes;

        var albumKey = StatsReport.AlbumArtKey(s.Album, lyr.Album, s.AlbumArtist, s.Artist);
        var coverKey = albumKey ?? StatsReport.SongArtKey(s.Title, s.Artist);
        // 曲库的封面只在专辑对得上时用：播放器报了专辑而曲库匹配到的是另一张（精选集、单曲版），
        // 那张封面配不上这张专辑。播放器没报专辑时，专辑就是按曲库登记的认的，自然对得上
        var url = lyr.Cover != null && (s.Album.Trim().Length == 0 || SameName(s.Album, lyr.Album)) ? lyr.Cover : null;
        if (url != null && ArtCache.IsNone(NoneKey(coverKey, url))) url = null;
        var bytes = StaleCover(s.CoverBytes, smtcAlbum) ? null : s.CoverBytes;
        if (!ArtCache.Has(coverKey) && (bytes != null || url != null))
            Enqueue(new Job(coverKey, bytes, url));

        foreach (var name in StatsReport.SplitArtists(s.Artist))
        {
            var key = StatsReport.ArtistArtKey(name);
            if (ArtCache.Has(key)) continue;
            var nk = NameKey(name);
            var hit = lyr.Artists?.FirstOrDefault(a => nk.Length > 0 && NameKey(a.Name) == nk);
            if (hit != null && !ArtCache.IsNone(NoneKey(key, hit.Ref))) Enqueue(new Job(key, null, hit.Ref));
        }
    }

    /// <summary>SMTC 的封面常比标题晚到：切歌后一阵子给的还是上一首的图。
    /// 跟上一首的一模一样、又不是同一张专辑的，当它还没换过来——等新图到了（字节一变会再看一遍）再存。
    /// 只比专辑名不比歌手：合辑、原声带每首歌手不同，封面却是同一张。</summary>
    private bool StaleCover(byte[]? cover, string? album)
        => cover != null && _prevCover != null && cover.AsSpan().SequenceEqual(_prevCover)
           && (album == null || album != _prevAlbum);

    /// <summary>「没有」按键加来源记：这个地址没图，换一个来源（另一家曲库、另一位同名歌手）照样可以试。</summary>
    internal static string NoneKey(string key, string source) => key + "\n" + source;

    // 带不带 SMTC 字节分开算：只有地址的那次网络出错，后来封面字节到了照样要排
    private static string TriedKey(Job job) => job.Key + "\n" + job.Ref + (job.Bytes != null ? "\n+" : "");

    /// <summary>清空听歌记录后调用：「这次已试过」也清掉，接着听同一首歌能重新存。</summary>
    public void Reset()
    {
        lock (_lock) _tried.Clear();
        _seenMatch = null;
    }

    private void Enqueue(Job job)
    {
        lock (_lock)
            if (!_tried.Add(TriedKey(job))) return;
        _queue.Writer.TryWrite(job);
    }

    /// <summary>这件活没做成、但不是「确实没有」：放它下次再排队。</summary>
    private void Forget(Job job)
    {
        lock (_lock) _tried.Remove(TriedKey(job));
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    if (!_enabled()) { Forget(job); continue; }
                    await DoAsync(job, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("art", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 退出
        }
    }

    private async Task DoAsync(Job job, CancellationToken ct)
    {
        if (ArtCache.Has(job.Key)) return;
        if (job.Bytes != null && IsRealCover(job.Bytes) && ArtCache.Save(job.Key, job.Bytes)) return;
        if (job.Ref == null || ArtCache.IsNone(NoneKey(job.Key, job.Ref)) || Environment.TickCount64 < _blockedUntil)
        {
            Forget(job); // 曲库的结果可能晚点才带着地址来；熔断过了再试
            return;
        }

        var url = job.Ref;
        if (url.StartsWith("ne:", StringComparison.Ordinal))
        {
            var (avatar, none, retry) = await NeteaseAvatarAsync(url[3..], ct);
            if (none) ArtCache.MarkNone(NoneKey(job.Key, job.Ref));
            if (retry) Forget(job);
            if (avatar == null) return; // 网络出错的这次运行不再试
            url = avatar;
        }
        var (status, bytes) = await GetAsync(url, Referer(url), ct);
        if (status == null) return; // 网络错误：这次运行不再试，也不记「没有」
        if (status == HttpStatusCode.OK && bytes != null && ArtCache.LooksLikeImage(bytes))
        {
            ArtCache.Save(job.Key, bytes); // 存不下是本机的问题（盘满、解不开），不算这个地址没图
            return;
        }
        // 只有 404/410 才是真没有；熔断、200 却不是图（风控页之类）都是一时的，下次再试
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) ArtCache.MarkNone(NoneKey(job.Key, job.Ref));
        else if (Blocking(status.Value) || status == HttpStatusCode.OK) Forget(job);
    }

    /// <summary>网易云歌手头像：搜索结果里的头像是占位图，要按歌手 id 另查。
    /// 没传过头像的歌手照样给一张默认剪影，这时 None 为 true（记「没有」）；
    /// 被熔断、或应答不像样（风控页之类）时 Retry 为 true（一时的，过后再查）；网络出错三项都是空的。</summary>
    private async Task<(string? Url, bool None, bool Retry)> NeteaseAvatarAsync(string id, CancellationToken ct)
    {
        if (!long.TryParse(id, out var aid) || aid <= 0) return (null, true, false);
        var (status, bytes) = await GetAsync($"https://music.163.com/api/artist/head/info/get?id={aid}", NeteaseReferer, ct);
        if (status == null) return (null, false, false);
        if (status != HttpStatusCode.OK || bytes == null) return (null, false, Blocking(status.Value));
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() == 200
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("artist", out var ar) && ar.ValueKind == JsonValueKind.Object
                && ar.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String
                && av.GetString() is { Length: > 0 } avatar
                && Uri.TryCreate(avatar, UriKind.Absolute, out var uri) && uri.Host.EndsWith(".126.net", StringComparison.Ordinal))
                return Lyrics.IsNeteaseDefaultPic(avatar) ? (null, true, false) : ("https://" + uri.Host + uri.AbsolutePath + "?param=300y300", false, false);
        }
        catch (JsonException)
        {
            // 应答不是 JSON（风控页之类），落到下面过后再查
        }
        return (null, false, true);
    }

    /// <summary>发一个 GET，守着两次请求的间隔和熔断。网络错误返回 (null, null)；403/429/5xx 触发熔断。</summary>
    private async Task<(HttpStatusCode? Status, byte[]? Bytes)> GetAsync(string url, string referer, CancellationToken ct)
    {
        if (Environment.TickCount64 < _blockedUntil) return (null, null);
        var wait = Math.Clamp(_lastRequest + GapMs - Environment.TickCount64, 0, GapMs);
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
        _lastRequest = Environment.TickCount64;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", Lyrics.NeteaseUa);
            req.Headers.TryAddWithoutValidation("Referer", referer);
            using var resp = await _http.SendAsync(req, ct);
            if (Blocking(resp.StatusCode))
            {
                _blockedUntil = Environment.TickCount64 + BreakerMs;
                return (resp.StatusCode, null);
            }
            if (resp.StatusCode != HttpStatusCode.OK) return (resp.StatusCode, null);
            // 一张 300px 的图几十 KB；超过 4 MB 的不是我们要的东西
            if (resp.Content.Headers.ContentLength > 4 << 20) return (resp.StatusCode, null);
            return (resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    private static bool Blocking(HttpStatusCode s) => s is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)s >= 500;

    private static string Referer(string url) => url.Contains(".qq.com", StringComparison.Ordinal) || url.Contains("gtimg.cn", StringComparison.Ordinal)
        ? QqReferer : NeteaseReferer;

    /// <summary>SMTC 给的封面能不能用：解得开、且不是网易云的白占位图。</summary>
    private static bool IsRealCover(byte[] bytes)
    {
        if (!ArtCache.LooksLikeImage(bytes)) return false;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 64;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return !MainController.IsPlaceholderCover(img);
        }
        catch
        {
            return false;
        }
    }

    private static bool SameName(string? a, string? b)
    {
        var (x, y) = (Lyrics.NormalizeForMatch(a ?? ""), Lyrics.NormalizeForMatch(b ?? ""));
        return x.Length > 0 && x == y;
    }

    // QQ 的歌手名常带括号注音：「YOASOBI (ヨアソビ)」「周杰伦 (Jay Chou)」
    [GeneratedRegex(@"\s*[(（][^()（）]*[)）]\s*$")]
    private static partial Regex TrailingAliasRegex();

    /// <summary>认歌手用的名字：去掉末尾括号注音再归一化（繁简、全角、大小写）。
    /// 比的是相等不是包含：歌词匹配用的 ArtistMatches 是双向包含，「Ai」会认成「Aimer」。</summary>
    internal static string NameKey(string name)
    {
        var bare = TrailingAliasRegex().Replace(name, "").Trim();
        return Lyrics.NormalizeForMatch(bare.Length > 0 ? bare : name);
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        _http.Dispose();
    }
}
