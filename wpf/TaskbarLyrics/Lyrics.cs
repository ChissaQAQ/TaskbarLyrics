// 歌词获取与 LRC 解析（移植自 lyrics.py）。
//
// 来源优先级：网易云（含译文 tlyric，覆盖最好）→ QQ 音乐 → LRCLIB。
// （网易云未登录搜索会过滤部分版权歌曲如周杰伦，此时自动回退后续来源。）
// 逐字时间来自酷狗 KRC（XOR+zlib 解密），按时间+文本匹配挂载到主歌词行。
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarLyrics;

/// <summary>歌词行：(毫秒, 原文, 译文|罗马音|null)。</summary>
public readonly record struct LyricLine(int Ms, string Text, string? Trans);

/// <summary>逐字：(行内偏移ms, 持续ms, 字)。</summary>
public readonly record struct KaraokeWord(int OffsetMs, int DurationMs, string Text);

public static partial class Lyrics
{
    // [分:秒]、[分:秒.毫秒]、[分:秒:厘秒] 时间戳都支持，一行可能有多个
    [GeneratedRegex(@"\[(\d+):(\d+)(?:[.:](\d+))?\]")]
    private static partial Regex LrcTimeRegex();

    [GeneratedRegex(@"\[(\d+),(\d+)\](.*)")]
    private static partial Regex KrcLineRegex();

    [GeneratedRegex(@"<(\d+),(\d+),\d+>([^<]*)")]
    private static partial Regex KrcWordRegex();

    [GeneratedRegex(@"\[offset:(-?\d+)\]")]
    private static partial Regex KrcOffsetRegex();

    // 制作信息行（作词/编曲/制作人等），不应作为歌词显示。
    // 注意 QQ 源是缩写格式（词：/曲：/合声：/录音工程：），所以中文词后不要求 \b
    // （"录音"后紧跟"工程"没有词边界），靠行首词+冒号组合约束防误伤正常歌词
    [GeneratedRegex(@"^\s*(?:作词|作曲|编曲|制作人|监制|混音|录音|和声|合声|吉他|贝斯|鼓|键盘|弦乐|企划|统筹|发行|出品|封面|设计|词|曲|演唱|演奏|OP|SP|lyrics?|composed?|arranged?|music|producer|lyricist|songwriter|vocals?|guitar|bass|drums?|mixed|mastering|recording).*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditLineRegex();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private const string NeteaseUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    private static async Task<string> GetStringAsync(string url, string? referer = null, string? ua = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", ua ?? NeteaseUa);
        if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, string? referer = null, string? ua = null)
        => JsonDocument.Parse(await GetStringAsync(url, referer, ua));

    private static string Q(Dictionary<string, string> p)
        => string.Join("&", p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    // ---- LRC 解析 ----

    private static int ToMs(string minutes, string seconds, string? frac)
    {
        var ms = int.Parse(minutes) * 60_000 + int.Parse(seconds) * 1000;
        if (!string.IsNullOrEmpty(frac))
            // 1 位按十分之一秒、2 位按厘秒、3 位按毫秒
            ms += int.Parse(frac) * (1000 / (int)Math.Pow(10, frac.Length));
        return ms;
    }

    /// <summary>把 LRC 文本解析成 [(毫秒, 歌词)]，按时间排序，去掉空行和元信息行。</summary>
    public static List<(int Ms, string Text)> ParseLrc(string lrcText)
    {
        var lines = new List<(int, string)>();
        foreach (var raw in lrcText.Split('\n'))
        {
            var stamps = LrcTimeRegex().Matches(raw);
            if (stamps.Count == 0) continue;
            var text = LrcTimeRegex().Replace(raw, "").Trim();
            if (text.Length == 0) continue;
            foreach (Match m in stamps)
                lines.Add((ToMs(m.Groups[1].Value, m.Groups[2].Value,
                    m.Groups[3].Success ? m.Groups[3].Value : null), text));
        }
        return lines.OrderBy(x => x.Item1).ToList(); // OrderBy 稳定排序，同刻多行保持原序
    }

    /// <summary>把译文按最近时间戳并到原文行，每条译文最多用一次。</summary>
    private static List<LyricLine> MergeTranslation(
        List<(int Ms, string Text)> lines, List<(int Ms, string Text)> trans, int tolMs = 1200)
    {
        var used = new bool[trans.Count];
        var merged = new List<LyricLine>(lines.Count);
        foreach (var (ms, text) in lines)
        {
            var best = -1;
            for (var i = 0; i < trans.Count; i++)
            {
                if (used[i] || Math.Abs(trans[i].Ms - ms) > tolMs) continue;
                if (best < 0 || Math.Abs(trans[i].Ms - ms) < Math.Abs(trans[best].Ms - ms))
                    best = i;
            }
            if (best >= 0)
            {
                used[best] = true;
                merged.Add(new LyricLine(ms, text, trans[best].Text));
            }
            else
            {
                merged.Add(new LyricLine(ms, text, null));
            }
        }
        return merged;
    }

    // ---- 网易云 ----

    private static async Task<List<LyricLine>?> FetchNeteaseAsync(
        string title, string artist, double durationS, string secondLine)
    {
        const string referer = "https://music.163.com";
        using var search = await GetJsonAsync(
            "https://music.163.com/api/search/get/web?" + Q(new()
            {
                ["s"] = $"{title} {artist}", ["type"] = "1", ["limit"] = "30",
            }), referer);
        if (!search.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
            return null;
        // 歌手宽松匹配：SMTC 的歌手串常是多歌手（"A/B"）或变体名，
        // 严格相等会漏歌（「Lyricify 能显示而我们不能」的主因之一）
        static bool ArtistMatch(JsonElement song, string artist)
        {
            if (!song.TryGetProperty("artists", out var artists)) return false;
            foreach (var a in artists.EnumerateArray())
            {
                var n = a.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
                if (n.Length == 0) continue;
                if (string.Equals(n, artist, StringComparison.OrdinalIgnoreCase)) return true;
                if (artist.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
                if (n.Contains(artist, StringComparison.OrdinalIgnoreCase) && artist.Length > 0) return true;
            }
            return false;
        }
        var all = songs.EnumerateArray().ToList();
        // 歌名必须匹配（必要条件），歌手匹配与时长接近只是排序权重——
        // 只按歌手+时长挑会把同歌手、时长接近的别的歌抓来（主人反馈偶尔匹配错歌）
        var scored = all
            .Select(s => (Song: s,
                          Ts: TitleScore(s.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "", title),
                          Artist: ArtistMatch(s, artist),
                          DurDiff: durationS > 0 && s.TryGetProperty("duration", out var dv)
                              ? Math.Abs(dv.GetDouble() / 1000 - durationS) : 0.0))
            .Where(x => x.Ts > 0)
            // 时长差太多基本是另一版本/另一首歌（现场版、remix 宁缺毋滥）
            .Where(x => durationS <= 0 || x.DurDiff <= 20)
            .OrderByDescending(x => x.Ts)
            .ThenByDescending(x => x.Artist)
            .ThenBy(x => x.DurDiff)
            .ToList();
        var ordered = PreferArtistMatched(scored, x => x.Artist)
            .Select(x => x.Song)
            .ToList();
        // 候选逐个尝试：同一首歌常有多个版本，
        // 有的版本没译文（主人反馈网易云明显有译文却显示不出来），优先带译文的版本
        if (ordered.Count == 0) return null;
        List<LyricLine>? firstResult = null;
        foreach (var cand in ordered.Take(3))
        {
            var id = cand.GetProperty("id").GetInt64();
            JsonDocument lyric;
            try
            {
                lyric = await GetJsonAsync(
                    "https://music.163.com/api/song/lyric?" + Q(new()
                    {
                        ["id"] = id.ToString(), ["lv"] = "1", ["kv"] = "1", ["tv"] = "-1",
                    }), referer);
            }
            catch { continue; } // 单个候选失败换下一个
            using (lyric)
            {
                var root = lyric.RootElement;
                var lines = ParseLrc(GetLyricText(root, "lrc"));
                // 有的条目只上传了译文或罗马音，原文 lrc 是空的（May'n「春夢」就是这样：
                // 955 字带时间轴的歌词全在 tlyric 里，lrc 一个字都没有）。
                // 直接跳过这条候选就会退化去抓同名的另一首歌，不如把现成的第二语言当主歌词用
                var altAsMain = false;
                if (lines.Count == 0)
                {
                    lines = ParseLrc(GetLyricText(root, "tlyric"));
                    if (lines.Count == 0) lines = ParseLrc(GetLyricText(root, "romalrc"));
                    if (lines.Count == 0) continue;
                    altAsMain = true;
                }
                // 歌词总长远超歌曲时长 → 多半是抓错了歌（同名歌/不同版本），换下一个候选
                if (durationS > 0 && lines[^1].Ms / 1000.0 > durationS + 30) continue;
                // 第二行：译文（tlyric）或罗马音（romalrc）。
                // 主歌词本身就是译文/罗马音时不再挂第二行，否则整行重复一遍
                var trans = altAsMain
                    ? new List<(int Ms, string Text)>()
                    : secondLine switch
                    {
                        "translation" => ParseLrc(GetLyricText(root, "tlyric")),
                        "romaji" => ParseLrc(GetLyricText(root, "romalrc")),
                        _ => new List<(int, string)>(),
                    };
                var merged = MergeTranslation(lines, trans);
                firstResult ??= merged;
                if (secondLine != "translation" || merged.Any(l => l.Trans != null))
                    return merged; // 带译文（或不需要译文）的候选直接采用
            }
        }
        return firstResult;
    }

    private static string GetLyricText(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("lyric", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? "";
        return "";
    }

    // ---- QQ 音乐 ----

    private static async Task<List<LyricLine>?> FetchQqAsync(string title, string artist, double durationS)
    {
        const string referer = "https://y.qq.com/portal/player.html";
        using var search = await GetJsonAsync(
            "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?" + Q(new()
            {
                ["w"] = $"{title} {artist}", ["format"] = "json", ["n"] = "5",
            }), referer);
        if (!search.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("song", out var song)
            || !song.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return null;

        static string SingerNames(JsonElement s) =>
            s.TryGetProperty("singer", out var singers)
                ? string.Join(" ", singers.EnumerateArray().Select(x =>
                    x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
                : "";

        var songs = list.EnumerateArray().ToList();
        // 与网易云同一套标准：歌名必须匹配，歌手/时长只是排序权重（防兜底拿到同歌手别的歌）
        static double IntervalOf(JsonElement s) =>
            s.TryGetProperty("interval", out var iv) ? iv.GetDouble() : 0;
        var ordered = songs
            .Select(s => (Song: s,
                          Ts: TitleScore(s.TryGetProperty("songname", out var nv) ? nv.GetString() ?? "" : "", title),
                          Artist: artist.Length > 0
                              && SingerNames(s).Contains(artist, StringComparison.OrdinalIgnoreCase),
                          DurDiff: durationS > 0 ? Math.Abs(IntervalOf(s) - durationS) : 0.0))
            .Where(x => x.Ts > 0)
            .Where(x => durationS <= 0 || x.DurDiff <= 20)
            .OrderByDescending(x => x.Ts)
            .ThenByDescending(x => x.Artist)
            .ThenBy(x => x.DurDiff)
            .ToList();
        if (ordered.Count == 0) return null;
        var chosen = PreferArtistMatched(ordered, x => x.Artist)[0].Song;

        var text = await GetStringAsync(
            "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?" + Q(new()
            {
                ["songmid"] = chosen.GetProperty("songmid").GetString() ?? "",
                ["format"] = "json", ["nobase64"] = "1", ["g_tk"] = "5381",
            }), referer);
        text = text.Trim();
        if (text.StartsWith("MusicJsonCallback")) // 兼容 JSONP 包裹
            text = text[(text.IndexOf('(') + 1)..text.LastIndexOf(')')];
        using var lyric = JsonDocument.Parse(text);
        var lines = ParseLrc(lyric.RootElement.TryGetProperty("lyric", out var l) ? l.GetString() ?? "" : "");
        if (lines.Count == 0) return null;
        // 歌词总长远超歌曲时长 → 抓错歌嫌疑，放弃本源交给 LRCLIB 兜底
        if (durationS > 0 && lines[^1].Ms / 1000.0 > durationS + 30) return null;
        var trans = ParseLrc(lyric.RootElement.TryGetProperty("trans", out var t) ? t.GetString() ?? "" : "");
        return MergeTranslation(lines, trans);
    }

    // ---- LRCLIB ----

    private static async Task<List<LyricLine>?> FetchLrclibAsync(string title, string artist, double durationS)
    {
        var p = new Dictionary<string, string> { ["track_name"] = title, ["artist_name"] = artist };
        if (durationS > 0) p["duration"] = ((int)durationS).ToString();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/get?" + Q(p));
        req.Headers.TryAddWithoutValidation("User-Agent", "taskbar-lyrics v0.1");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("syncedLyrics", out var synced)
            || synced.ValueKind != JsonValueKind.String)
            return null;
        var lines = ParseLrc(synced.GetString() ?? "");
        if (lines.Count == 0) return null;
        return MergeTranslation(lines, new List<(int, string)>());
    }

    // ---- 逐字：酷狗 KRC ----

    // KRC 解密 key
    private static readonly byte[] KrcKey =
        { 0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
          0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69 };

    /// <summary>KRC 解码：base64 → 跳过 4 字节头 → XOR → zlib 解压。</summary>
    private static string DecodeKrc(string b64Content)
    {
        var raw = Convert.FromBase64String(b64Content);
        if (raw.Length < 4 || raw[0] != 'k' || raw[1] != 'r' || raw[2] != 'c' || raw[3] != '1')
            throw new InvalidDataException("不是 KRC 格式");
        var body = new byte[raw.Length - 4];
        for (var i = 0; i < body.Length; i++)
            body[i] = (byte)(raw[i + 4] ^ KrcKey[i % 16]);
        using var input = new MemoryStream(body);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public sealed record KrcLine(int StartMs, string Plain, List<KaraokeWord> Words);

    /// <summary>解析 KRC，返回 [(行开始ms, 纯文本, 逐字)]。</summary>
    private static List<KrcLine> ParseKrc(string text)
    {
        var offset = 0;
        var om = KrcOffsetRegex().Match(text);
        if (om.Success) offset = int.Parse(om.Groups[1].Value);
        var lines = new List<KrcLine>();
        foreach (var raw in text.Split('\n'))
        {
            var m = KrcLineRegex().Match(raw.TrimEnd('\r'));
            if (!m.Success || m.Index != 0) continue;
            var start = int.Parse(m.Groups[1].Value) - offset;
            var words = KrcWordRegex().Matches(m.Groups[3].Value)
                .Select(w => new KaraokeWord(int.Parse(w.Groups[1].Value), int.Parse(w.Groups[2].Value), w.Groups[3].Value))
                .ToList();
            var plain = string.Concat(words.Select(w => w.Text)).Trim();
            if (words.Count > 0 && plain.Length > 0)
                lines.Add(new KrcLine(start, plain, words));
        }
        return lines.OrderBy(x => x.StartMs).ToList();
    }

    private static async Task<List<KrcLine>?> FetchKugouKaraokeAsync(string title, string artist, double durationS)
    {
        using var search = await GetJsonAsync(
            "http://mobilecdn.kugou.com/api/v3/search/song?" + Q(new()
            {
                ["format"] = "json", ["keyword"] = $"{title} {artist}", ["page"] = "1", ["pagesize"] = "5",
            }));
        if (!search.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0)
            return null;
        var songs = info.EnumerateArray().ToList();
        // 与主歌词源同一套标准：歌名必须匹配（必要条件），歌手/时长只是排序权重。
        // 逐字数据挂错歌会让扫过时间完全错乱，比没有逐字更糟
        static bool KgArtistMatch(JsonElement s, string artist)
        {
            var sn = s.TryGetProperty("singername", out var n) ? n.GetString() ?? "" : "";
            // singername 为空时不能放行（空串是任何串的子串，恒真会误匹配）
            return artist.Length > 0 && sn.Length > 0
                && (artist.Contains(sn, StringComparison.OrdinalIgnoreCase)
                    || sn.Contains(artist, StringComparison.OrdinalIgnoreCase));
        }
        var ordered = songs
            .Select(s => (Song: s,
                          Ts: TitleScore(s.TryGetProperty("songname", out var nv) ? nv.GetString() ?? "" : "", title),
                          Artist: KgArtistMatch(s, artist),
                          DurDiff: durationS > 0 && s.TryGetProperty("duration", out var dv)
                              ? Math.Abs(dv.GetDouble() - durationS) : 0.0))
            .Where(x => x.Ts > 0)
            .Where(x => durationS <= 0 || x.DurDiff <= 20)
            .OrderByDescending(x => x.Ts)
            .ThenByDescending(x => x.Artist)
            .ThenBy(x => x.DurDiff)
            .ToList();
        if (ordered.Count == 0) return null;
        var chosen = PreferArtistMatched(ordered, x => x.Artist)[0].Song;

        var songname = chosen.TryGetProperty("songname", out var snv) ? snv.GetString() ?? title : title;
        var chosenDur = chosen.TryGetProperty("duration", out var cd) ? cd.GetDouble() : durationS;
        var hash = chosen.GetProperty("hash").GetString() ?? "";
        using var krcSearch = await GetJsonAsync(
            "http://krcs.kugou.com/search?" + Q(new()
            {
                ["ver"] = "1", ["man"] = "yes", ["client"] = "mobi",
                ["keyword"] = songname,
                ["duration"] = ((int)(chosenDur * 1000)).ToString(),
                ["hash"] = hash,
            }));
        if (!krcSearch.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return null;
        var cand = candidates[0];
        using var download = await GetJsonAsync(
            "http://lyrics.kugou.com/download?" + Q(new()
            {
                ["ver"] = "1", ["client"] = "pc",
                ["id"] = cand.GetProperty("id").GetString() ?? "",
                ["accesskey"] = cand.GetProperty("accesskey").GetString() ?? "",
                ["fmt"] = "krc", ["charset"] = "utf8",
            }));
        var content = download.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        return ParseKrc(DecodeKrc(content));
    }

    /// <summary>歌名匹配度：0=不匹配，1=归一化后一方包含另一方（覆盖 "(Live)" 等后缀差异），
    /// 2=归一化完全相等。歌名是防错配的第一道闸：只按歌手+时长挑候选，
    /// 会把同歌手、时长接近的另一首歌的歌词抓来。</summary>
    private static int TitleScore(string candidate, string title)
    {
        var a = NormalizeForMatch(candidate);
        var b = NormalizeForMatch(title);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 2;
        return a.Contains(b) || b.Contains(a) ? 1 : 0;
    }

    /// <summary>候选池收敛：只要有一个候选的歌手对得上，就只在这些候选里挑。
    ///
    /// 歌名相同而歌手不符，基本就是同名的另一首歌——网易云上叫「春夢」的条目有四首，
    /// 分属 May'n / 倒车入库 / 中川孝 / 初音ミク，拿错的那首冒充比不显示歌词更糟。
    /// 光靠排序不够：排前面的候选可能因为没歌词被跳过，兜底就落到歌手不符的那首上。
    /// 一个都对不上时（SMTC 的歌手写法与曲库不一致）才放开，按歌名分数照原顺序试。</summary>
    private static List<T> PreferArtistMatched<T>(List<T> scored, Func<T, bool> artistMatched)
        => scored.Any(artistMatched) ? scored.Where(artistMatched).ToList() : scored;

    /// <summary>匹配用归一化：全角转半角、小写化，只留字母和数字（忽略空白与标点差异）。</summary>
    private static string NormalizeForMatch(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var ch = c;
            if (ch is >= '\uFF01' and <= '\uFF5E') ch = (char)(ch - 0xFEE0); // 全角 → 半角
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>把 KRC 逐字时间挂到主歌词行上。
    /// 两遍匹配：先在 ±4s 内收集文本一致行的时间差，取中位数估计全局偏移
    /// （KRC 与主歌词源版本不同常带恒定偏移）；再扣除偏移按时间接近 + 文本一致挂载。</summary>
    private static Dictionary<int, List<KaraokeWord>> AttachKaraoke(
        List<LyricLine> mainLines, List<KrcLine> krcLines, int tolMs = 1200)
    {
        var normMain = mainLines.Select(l => NormalizeForMatch(l.Text)).ToList();
        var normKrc = krcLines.Select(k => NormalizeForMatch(k.Plain)).ToList();

        // 第一遍：估计全局偏移
        var diffs = new List<int>();
        for (var i = 0; i < mainLines.Count; i++)
        {
            if (normMain[i].Length == 0) continue;
            for (var j = 0; j < krcLines.Count; j++)
            {
                if (normKrc[j].Length == 0 || normKrc[j] != normMain[i]) continue;
                if (Math.Abs(krcLines[j].StartMs - mainLines[i].Ms) <= 4000)
                    diffs.Add(krcLines[j].StartMs - mainLines[i].Ms);
            }
        }
        diffs.Sort();
        var offset = diffs.Count > 0 ? diffs[diffs.Count / 2] : 0;

        // 第二遍：扣除偏移后挂载
        var karaoke = new Dictionary<int, List<KaraokeWord>>();
        var used = new HashSet<int>();
        for (var i = 0; i < mainLines.Count; i++)
        {
            if (normMain[i].Length == 0) continue;
            var best = -1;
            for (var j = 0; j < krcLines.Count; j++)
            {
                if (used.Contains(j) || normKrc[j] != normMain[i]) continue;
                var diff = Math.Abs(krcLines[j].StartMs - offset - mainLines[i].Ms);
                if (diff > tolMs) continue;
                if (best < 0 || diff < Math.Abs(krcLines[best].StartMs - offset - mainLines[i].Ms))
                    best = j;
            }
            if (best >= 0)
            {
                used.Add(best);
                karaoke[mainLines[i].Ms] = krcLines[best].Words;
            }
        }
        return karaoke;
    }

    /// <summary>为没匹配到逐字数据的行合成匀速扫过：西文按单词、其余按字符切分单元，
    /// 时长按单元字数均摊。保证同一首歌内所有行的扫过效果一致。</summary>
    public static List<KaraokeWord> SynthesizeWords(string text, int durationMs)
    {
        var units = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsAsciiLetterOrDigit(text[i]))
            {
                var j = i;
                while (j < text.Length && char.IsAsciiLetterOrDigit(text[j])) j++;
                units.Add(text[i..j]); // 连续字母/数字算一个西文单词
                i = j;
            }
            else
            {
                var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1; // 代理对不拆开
                units.Add(text.Substring(i, len));
                i += len;
            }
        }
        if (units.Count == 0) return new List<KaraokeWord>();

        var total = units.Sum(u => u.Length);
        var words = new List<KaraokeWord>(units.Count);
        var offset = 0;
        var acc = 0;
        foreach (var unit in units)
        {
            acc += unit.Length;
            var end = (int)Math.Round(durationMs * (double)acc / total);
            words.Add(new KaraokeWord(offset, Math.Max(1, end - offset), unit));
            offset = end;
        }
        return words;
    }

    // ---- 总入口 ----

    /// <summary>依次尝试各歌词源，返回 (歌词行, 逐字时间表, 命中的源名)。
    /// 逐字表：{主行时间ms: 逐字}，取不到或匹配不上时为空 dict（退化为逐行显示）。
    /// secondLine: translation 译文 | romaji 罗马音（仅网易云源支持）| off 关闭。</summary>
    public static async Task<(List<LyricLine>? Lines, Dictionary<int, List<KaraokeWord>> Karaoke, string Source)>
        FetchAsync(string title, string artist, double durationS = 0,
            bool withKaraoke = true, string secondLine = "translation")
    {
        List<LyricLine>? lines = null;
        var sourceName = "";
        var sources = new (string Name, Func<Task<List<LyricLine>?>> Fetch)[]
        {
            ("_fetch_netease", () => FetchNeteaseAsync(title, artist, durationS, secondLine)),
            ("_fetch_qq", () => FetchQqAsync(title, artist, durationS)),
            ("_fetch_lrclib", () => FetchLrclibAsync(title, artist, durationS)),
        };
        foreach (var (name, fetch) in sources)
        {
            try { lines = await fetch(); }
            catch { lines = null; } // 单个源网络异常不致命，换下一个
            if (lines is { Count: > 0 })
            {
                sourceName = name;
                break;
            }
        }
        if (lines is not { Count: > 0 })
            return (null, new Dictionary<int, List<KaraokeWord>>(), "");

        // 过滤制作信息行（作词/编曲/制作人等不是歌词，不该占用任务栏）
        var filtered = lines.Where(l => !CreditLineRegex().IsMatch(l.Text)).ToList();
        if (filtered.Count > 0) lines = filtered; // 万一全被滤掉则保留原样（防误判）

        var karaoke = new Dictionary<int, List<KaraokeWord>>();
        if (withKaraoke)
        {
            try
            {
                var krc = await FetchKugouKaraokeAsync(title, artist, durationS);
                if (krc != null)
                    karaoke = AttachKaraoke(lines, krc);
            }
            catch
            {
                karaoke = new Dictionary<int, List<KaraokeWord>>(); // 逐字失败不影响逐行
            }
        }
        return (lines, karaoke, sourceName);
    }

    /// <summary>返回当前进度对应的 (行索引, 原文, 译文)；还没到第一句时索引为 -1。</summary>
    public static (int Index, string Original, string Trans) CurrentLine(List<LyricLine> lines, int positionMs)
    {
        var index = -1;
        var original = "";
        var trans = "";
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Ms > positionMs) break;
            (index, original, trans) = (i, lines[i].Text, lines[i].Trans ?? "");
        }
        return (index, original, trans);
    }

    // ---- 命令行验证入口（对应 Python 的 __main__）----

    public static async Task RunConsoleTestAsync(string title, string artist)
    {
        var (found, karaoke, source) = await FetchAsync(title, artist);
        if (found == null)
        {
            Console.WriteLine("没找到歌词");
            return;
        }
        var hasTrans = found.Count(l => l.Trans != null);
        foreach (var (ms, text, trans) in found.Take(8))
        {
            var suffix = trans != null ? $"  /  {trans}" : "";
            var mark = karaoke.ContainsKey(ms) ? " [逐字]" : "";
            Console.WriteLine($"{ms / 1000.0,8:F2}s  {text}{suffix}{mark}");
        }
        Console.WriteLine($"... 共 {found.Count} 行，其中 {hasTrans} 行带译文，{karaoke.Count} 行带逐字（来源 {source}）");
    }
}
