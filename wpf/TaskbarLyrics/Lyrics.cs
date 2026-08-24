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

    // 制作信息行（作词/编曲/制作人等），不应作为歌词显示。四条规则拼起来：
    //  1) 角色名 + 短前缀 + 冒号。冒号必须离角色名 16 字内——原先是 `.*[:：]`，
    //     一直贪到行尾最后一个冒号，「曲终人散那天，我说：再见」这种正常歌词
    //     会被当成 "曲……：" 滤掉；
    //  2) 单字角色（词/曲/唱/鼓）必须紧跟冒号，或与其他单字角色连写（"词曲："、"词/曲："）。
    //     单字太容易撞上歌词开头，不能享受规则 1 的 16 字宽限；
    //  3) 繁体曲库（QQ/酷狗的港台条目）用的是「編曲/製作人/錄音/發行」，
    //     原先整份简体清单对它们一条都不匹配——这是主人反馈「还是会显示非歌词信息」的主因；
    //  4) 版权声明句没有冒号（"未经著作权人许可不得翻唱"、"All Rights Reserved"），单列。
    // 注意中文词后不要求 \b（"录音"后紧跟"工程"没有词边界），靠行首词+冒号组合约束防误伤。
    //  5) 行首允许括号：曲库爱把版权声明和乐手表整行括起来（"（未经著作人许可…）"），
    //     光锚 ^\s* 会被那个全角括号挡在门外。
    //  6) 乐手与致谢单列（小提琴/特别支持/鸣谢…）：它们和「作词」是同一类信息，
    //     只是词表最初没收——反转成以 KRC 为文本主体后，KRC 开头那串乐手表全露出来了。
    [GeneratedRegex(@"^[\s(（\[【「『]*(?:(?:作词|作詞|作曲|编曲|編曲|改编|改編|填词|填詞|词曲|詞曲|制作|製作|制作人|製作人|监制|監製|监督|監督|出品|出品人|承制|承製|发行|發行|企划|企劃|策划|策劃|统筹|統籌|混音|母带|母帶|后期|後期|录音|錄音|录音室|錄音室|工作室|和声|和聲|合声|合聲|伴唱|主唱|配唱|合唱|演唱|演奏|吉他|贝斯|貝斯|鼓手|键盘|鍵盤|弦乐|弦樂|管乐|管樂|编写|編寫|封面|设计|設計|美术|美術|海报|海報|文案|宣传|宣傳|推广|推廣|翻译|翻譯|校对|校對|唱片|专辑|專輯|歌手|歌名|歌曲|版权|版權|著作权|著作權|授权|授權"
        + @"|小提琴|中提琴|大提琴|提琴|钢琴|鋼琴|长笛|長笛|笛子|唢呐|嗩吶|二胡|古筝|古箏|琵琶|竹笛|萨克斯|薩克斯|口琴|手风琴|手風琴|合成器|打击乐|打擊樂|人声|人聲|编程|編程|特别支持|特別支持|特别鸣谢|特別鳴謝|特别感谢|特別感謝|鸣谢|鳴謝|感谢|感謝|音乐总监|音樂總監|总监|總監|监棚|監棚|艺人|藝人|经纪|經紀|词曲版权|詞曲版權)[^:：]{0,16}[:：]"
        + @"|(?:[A-Za-z]{1,12}[\s.&/-]+){0,2}(?:OP|SP|ISRC|UPC|lyrics?|lyricist|composed?|composer|arrange[rd]?|arrangement|arranged|music|produced?|producer|vocals?|chorus|guitar|bass|drums?|keyboards?|strings|violin|cello|piano|flute|sax\w*|synth\w*|percussion|programming|engineer\w*|special\s+thanks|mix\w*|master\w*|record\w*|perform\w*|writer|written|label|studio|lrc|krc|qrc|trc)[^:：]{0,16}[:：]"
        + @"|[词詞曲唱鼓歌][\s/、&＆和与]*[词詞曲唱鼓歌]?\s*[:：]"
        + @"|(?:未经|未經|本(?:歌曲|作品|专辑|專輯))[^\n]{0,30}(?:许可|許可|授权|授權|版权|版權|同意)"
        + @"|.*(?:版权所有|版權所有|all\s+rights\s+reserved|unauthorized\s+(?:reproduction|copying)))",
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
            // 制作信息行不参与配译文。网易云的 lrc 开头一律带「作词/作曲/编曲/制作人」
            // 四五行、tlyric 一律不带，而这些行的时间戳全挤在 0~1s，正好落在首句译文的
            // 时间窗内——让它们参与就会把首句的译文抢走再标成已用，首句反倒没了译文。
            // 这是「首句经常没有翻译」的真凶（Lemon：两边首句都在 00:00.851，却配不上）
            if (CreditLineRegex().IsMatch(text))
            {
                merged.Add(new LyricLine(ms, text, null));
                continue;
            }
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

    /// <summary>一个歌词源的结果：歌词行 + 曲库登记的歌曲时长（秒，未知为 0）。
    ///
    /// 时长必须一路带出来，因为网易云客户端的 SMTC 完全不上报 timeline
    /// （实测 Position=0、EndTime=0、LastUpdatedTime 还停在 1601 年的初值），
    /// 而单曲循环检测要靠「插值进度超过歌曲时长」来判断，没有时长就只能拿
    /// 「最后一句歌词 + 一个猜的余量」凑——尾奏比余量长的歌一到尾奏就被误判成
    /// 重播、进度归零、显示回开头那句（主人反馈的「快结束时又显示开头歌词」）。</summary>
    private sealed record SourceResult(List<LyricLine> Lines, double DurationS);

    // ---- 网易云 ----

    private static async Task<SourceResult?> FetchNeteaseAsync(
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
        // 曲库登记的时长（毫秒 → 秒），既用于挑候选也要带回给调用方补 SMTC 的空缺
        static double DurOf(JsonElement song) =>
            song.TryGetProperty("duration", out var dv) ? dv.GetDouble() / 1000 : 0.0;
        // 歌名必须匹配（必要条件），歌手匹配与时长接近只是排序权重——
        // 只按歌手+时长挑会把同歌手、时长接近的别的歌抓来（主人反馈偶尔匹配错歌）
        var scored = all
            .Select(s => (Song: s,
                          Ts: TitleScore(s.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "", title),
                          Artist: ArtistMatch(s, artist),
                          DurDiff: durationS > 0 && s.TryGetProperty("duration", out _)
                              ? Math.Abs(DurOf(s) - durationS) : 0.0))
            .Where(x => x.Ts > 0)
            // 时长差太多基本是另一版本/另一首歌（现场版、remix 宁缺毋滥）
            .Where(x => durationS <= 0 || x.DurDiff <= 20)
            .OrderByDescending(x => x.Ts)
            .ThenByDescending(x => x.Artist)
            .ThenBy(x => x.DurDiff)
            .ToList();
        var ordered = PreferArtistMatched(scored, x => x.Artist)
            .Select(x => (x.Song, x.Ts))
            .ToList();
        // 候选逐个尝试：同一首歌常有多个版本，
        // 有的版本没译文（主人反馈网易云明显有译文却显示不出来），优先带译文的版本
        if (ordered.Count == 0) return null;
        SourceResult? firstResult = null;
        SourceResult? bestTrans = null;
        var bestTs = -1;
        var bestRatio = -1.0;
        foreach (var (cand, ts) in ordered.Take(3))
        {
            var id = cand.GetProperty("id").GetInt64();
            JsonDocument lyric;
            try
            {
                lyric = await GetJsonAsync(
                    "https://music.163.com/api/song/lyric?" + Q(new()
                    {
                        // rv 不能省：不带它，响应里连 romalrc 这个字段都不会出现，
                        // 「第二行显示罗马音」就永远是空的（lv 原文 / kv 逐字 / tv 译文 / rv 罗马音）
                        ["id"] = id.ToString(), ["lv"] = "1", ["kv"] = "1",
                        ["tv"] = "-1", ["rv"] = "-1",
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
                var picked = new SourceResult(merged, DurOf(cand));
                firstResult ??= picked;
                if (secondLine == "off") return picked;
                // 比较各候选的译文覆盖情况，取最好的那个——不能「见到任意一行译文就走」。
                // 网易云上「只翻译了副歌」的残缺条目非常常见，撞上它时前几句、间奏后
                // 都是空的，用户看到的就是「偶尔有几句少了翻译」；而搜索结果顺序会随
                // 热度和索引更新变动，所以同一首歌换个时间抓可能好可能坏，像是随机的。
                //
                // 歌名分数必须排在覆盖率前面，否则择优会把前面的排序整个推翻：
                // 比覆盖「行数」时，长一倍的 Live 版光靠行数多就能赢过录音室版
                // （实测「居眠り遠征隊」：录音室版 45 行 43 行有译文，Live 版 102 行
                //  100 行有译文，抓回来的整首都是 Live 的即兴口白）。
                // 而且这两道闸平时的兜底——时长差与「歌词比歌长」——在网易云上双双失效：
                // 它的 SMTC 完全不上报 timeline，durationS 恒为 0，两处判断直接短路。
                // 覆盖率也必须用比率而不是行数：本意只是「别挑到残缺翻译」，比率就够了
                var ratio = merged.Count == 0 ? 0 : merged.Count(l => l.Trans != null) / (double)merged.Count;
                // 歌名满分且译文全覆盖才立刻收工，不白打后面候选的接口。
                // 只看全覆盖是不够的：Live 版也可能全覆盖，先返回就再也轮不到正确的那首
                if (ts >= 2 && ratio >= 1) return picked;
                if (ts > bestTs || (ts == bestTs && ratio > bestRatio))
                {
                    bestTs = ts;
                    bestRatio = ratio;
                    bestTrans = picked;
                }
            }
        }
        return bestTrans ?? firstResult;
    }

    private static string GetLyricText(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("lyric", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? "";
        return "";
    }

    // ---- QQ 音乐 ----

    private static async Task<SourceResult?> FetchQqAsync(string title, string artist, double durationS)
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
        return new SourceResult(MergeTranslation(lines, trans), IntervalOf(chosen));
    }

    // ---- LRCLIB ----

    private static async Task<SourceResult?> FetchLrclibAsync(string title, string artist, double durationS)
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
        var dur = doc.RootElement.TryGetProperty("duration", out var dv)
                  && dv.ValueKind == JsonValueKind.Number ? dv.GetDouble() : 0.0;
        return new SourceResult(MergeTranslation(lines, new List<(int, string)>()), dur);
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

    /// <summary>匹配用归一化：全角转半角、繁体转简体、小写化，只留字母和数字（忽略空白与标点差异）。
    ///
    /// 繁简必须一起归一：SMTC 报的是播放器里那份文件的标题，港台条目常是繁体（「告白氣球」），
    /// 而曲库命中的是简体条目——「氣」不等于「气」，这一个字就能让歌名分数归零、
    /// 一路退到没有译文的 lrclib 兜底源；行文本对不上则整首歌的逐字全丢。
    /// 两侧同时归一化只会把本来不同的字合并，不会把本来相同的拆开，方向上是安全的；
    /// 代价是「干/幹/乾」这类多对一映射会略微放宽匹配，由歌手与时长那两道闸兜着。</summary>
    private static string NormalizeForMatch(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var ch = c;
            if (ch is >= '\uFF01' and <= '\uFF5E') ch = (char)(ch - 0xFEE0); // 全角 → 半角
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        // 放在最后：标点空白已经滤掉，要转换的串更短，一次 P/Invoke 也更省
        return NativeMethods.ToSimplifiedChinese(sb.ToString());
    }

    private const double MinTextSim = 0.6;    // 配对所需的文本相似度下限
    private const double MinMergedSim = 0.8;  // 一对二/二对一合并配对的下限（拼回来该几乎逐字相同）
    private const int MaxLineShiftMs = 3000;  // 扣除全局偏移后仍允许的行首时间差
    // 「主歌词是译文、KRC 是原文」的识别阈值（见 TryAlignAsTranslated）
    private const double MaxKanaHangulRatio = 0.02; // 主歌词侧作为中文译文的假名/谚文上限
    private const double MinHanRatio = 0.5;         // 且汉字得占一半以上（否则那是英文原文）
    private const double MinKanaHangulRatio = 0.15; // KRC 侧作为日 / 韩原文的下限
    private const double MinLatinRatio = 0.7;       // KRC 侧作为西文原文的下限
    private const int MaxTimeAlignShiftMs = 1500; // 纯时间对齐允许的单行偏差
    private const int MaxTimeAlignMedianMs = 600; // 且逐行偏差的中位数不得超过这个数

    /// <summary>两段归一化文本的相似度：2×最长公共子序列长度 / 两者总长（0~1，1 为完全相同）。
    /// 用子序列而不是编辑距离：两个曲库的差异多是多字/少字（和声括注、语气词、断句不同），
    /// 子序列对插入删除更宽容，而「整行其实是另一句」照样只能拿到低分。</summary>
    private static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        // 相似度上限就是 2×短的/总长，够不到门槛就不必跑 O(nm) 的 DP
        if (2.0 * Math.Min(a.Length, b.Length) / (a.Length + b.Length) < MinTextSim) return 0.0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            // cur[0] 恒为 0，内层把 cur[1..] 全写满，滚动复用无需清零
            for (var j = 1; j <= b.Length; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return 2.0 * prev[b.Length] / (a.Length + b.Length);
    }

    /// <summary>逐字整体平移，补偿行首时间差。KRC 里第 k 字唱在「KRC 行首 + 偏移」，
    /// 而显示层的行内进度是从主歌词行的时间戳起算的，两者不一致时整条扫过会偏一截。
    /// 平移到行首之前的部分只能压到 0（行还没上屏，没法更早扫）。</summary>
    private static List<KaraokeWord> ShiftWords(List<KaraokeWord> words, int shiftMs)
    {
        if (shiftMs == 0) return words;
        var result = new List<KaraokeWord>(words.Count);
        foreach (var w in words)
        {
            var start = w.OffsetMs + shiftMs;
            var end = start + w.DurationMs;
            if (start < 0) (start, end) = (0, Math.Max(1, end));
            result.Add(new KaraokeWord(start, Math.Max(1, end - start), w.Text));
        }
        return result;
    }

    /// <summary>行级对齐结果：配对（主歌词行下标, KRC 行下标）按时间正序，
    /// 以及两个曲库之间的系统性时间差（KRC 行首减主歌词行首的中位数，毫秒）。</summary>
    private sealed record Alignment(List<(int Main, int Krc)> Pairs, int OffsetMs);

    /// <summary>把主歌词的行与 KRC 的行一一对上（谁挂谁由调用方决定）。
    ///
    /// 主歌词与 KRC 来自两个不同曲库对同一首歌的独立录入，断句、用字、间奏长度都可能有差。
    /// 原先是「逐行独立贪心最近邻 + 文本必须归一化后完全相等 + 单一全局偏移 ±1.2s +
    /// 每个 KRC 行独占」，四个条件任一不满足这行就退化成匀速合成扫过——于是同一首歌里
    /// 逐字时有时无（主人正是这么反馈的）。三处脆弱点：
    ///   1. 要求完全相等：差一个异体字/送假名/和声括注就整行失配；
    ///   2. 单一全局偏移：两版间奏长度不同时偏移是逐段漂移的，固定容差挡不住；
    ///   3. 贪心 + 独占：副歌重复行里靠前的行会抢走本属于后面某行的 KRC 行。
    /// 改成整首歌一次单调序列对齐（DP）：两边本来都是按时间有序的序列，单调对齐天然
    /// 解决重复行抢占与局部漂移；文本改用相似度而非相等；配上行首时间差平移后，
    /// 时间容差可以放宽到只用来拦「整体错配到另一首歌」。</summary>
    private static Alignment AlignLines(
        List<LyricLine> mainLines, List<KrcLine> krcLines)
    {
        var pairs = new List<(int Main, int Krc)>();
        var n = mainLines.Count;
        var m = krcLines.Count;
        if (n == 0 || m == 0) return new Alignment(pairs, 0);

        var normMain = mainLines.Select(l => NormalizeForMatch(l.Text)).ToList();
        var normKrc = krcLines.Select(k => NormalizeForMatch(k.Plain)).ToList();

        // 第一遍：拿文本完全相等的行估全局偏移（两个源的版本不同常带一个恒定时间差）
        var diffs = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (normMain[i].Length == 0) continue;
            for (var j = 0; j < m; j++)
            {
                if (normKrc[j].Length == 0 || normKrc[j] != normMain[i]) continue;
                if (Math.Abs(krcLines[j].StartMs - mainLines[i].Ms) <= 4000)
                    diffs.Add(krcLines[j].StartMs - mainLines[i].Ms);
            }
        }
        diffs.Sort();
        var offset = diffs.Count > 0 ? diffs[diffs.Count / 2] : 0;

        // 配对得分矩阵（0 = 不允许配对）。除 1:1 外还算「一个主行 ↔ 相邻两个 KRC 行」与
        // 「相邻两个主行 ↔ 一个 KRC 行」：两个曲库对同一首歌的断句粒度常不同——KRC 按「唱的
        // 断句」把一句拆成两行，或反过来把两句并成一行。严格 1:1 时这些行整片落空，
        // 且相似度还会双双跌破阈值（Lemon 首句：网易云一行 16 字、KRC 拆成 4+12 两行，
        // 单看任一半的相似度只有 0.4）
        double Score(string a, int aMs, string b, int bMs, double min)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (Math.Abs(bMs - offset - aMs) > MaxLineShiftMs) return 0;
            var s = Similarity(a, b);
            return s >= min ? s : 0;
        }

        var sim = new double[n, m];    // main[i] ↔ krc[j]
        var sim1x2 = new double[n, m]; // main[i] ↔ krc[j-1] + krc[j]
        var sim2x1 = new double[n, m]; // main[i-1] + main[i] ↔ krc[j]
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                sim[i, j] = Score(normMain[i], mainLines[i].Ms,
                    normKrc[j], krcLines[j].StartMs, MinTextSim);
                // 合并只认高相似度：拆行拼回来本该几乎逐字相同，阈值松了会把
                // 一句歌词旁边那行无关的短句也一起吞进来
                if (j > 0)
                    sim1x2[i, j] = Score(normMain[i], mainLines[i].Ms,
                        normKrc[j - 1] + normKrc[j], krcLines[j - 1].StartMs, MinMergedSim);
                if (i > 0)
                    sim2x1[i, j] = Score(normMain[i - 1] + normMain[i], mainLines[i - 1].Ms,
                        normKrc[j], krcLines[j].StartMs, MinMergedSim);
            }
        }

        // 单调序列对齐：dp[i,j] = 前 i 个主行与前 j 个 KRC 行配对能拿到的最高总分。
        // 只允许「同时前进（配对）/ 各自跳过」，因此结果天然保持两边的时间顺序
        var dp = new double[n + 1, m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var best = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                var s = sim[i - 1, j - 1];
                if (s > 0) best = Math.Max(best, dp[i - 1, j - 1] + s);
                // 合并配对按「吃掉两行」记两份分：这样当那多出来的一行另有 1:1 的好归宿时，
                // 「各配各的」总分更高，DP 会选它，合并只在那行本来无处可去时才发生
                if (j > 1 && sim1x2[i - 1, j - 1] > 0)
                    best = Math.Max(best, dp[i - 1, j - 2] + sim1x2[i - 1, j - 1] * 2);
                if (i > 1 && sim2x1[i - 1, j - 1] > 0)
                    best = Math.Max(best, dp[i - 2, j - 1] + sim2x1[i - 1, j - 1] * 2);
                dp[i, j] = best;
            }
        }

        // 回溯取出配对。dp[i,j] 是各候选的 max，与某个候选相等即说明走的是那一条。
        // 一对多的组拆成多条 (主行, KRC 行) 记录，由调用方决定怎么用
        var (ii, jj) = (n, m);
        while (ii > 0 && jj > 0)
        {
            var s = sim[ii - 1, jj - 1];
            var s12 = jj > 1 ? sim1x2[ii - 1, jj - 1] : 0;
            var s21 = ii > 1 ? sim2x1[ii - 1, jj - 1] : 0;
            if (s > 0 && dp[ii, jj] <= dp[ii - 1, jj - 1] + s + 1e-9)
            {
                pairs.Add((ii - 1, jj - 1));
                ii--;
                jj--;
            }
            else if (s12 > 0 && dp[ii, jj] <= dp[ii - 1, jj - 2] + s12 * 2 + 1e-9)
            {
                pairs.Add((ii - 1, jj - 1));
                pairs.Add((ii - 1, jj - 2));
                ii--;
                jj -= 2;
            }
            else if (s21 > 0 && dp[ii, jj] <= dp[ii - 2, jj - 1] + s21 * 2 + 1e-9)
            {
                pairs.Add((ii - 1, jj - 1));
                pairs.Add((ii - 2, jj - 1));
                ii -= 2;
                jj--;
            }
            else if (dp[ii - 1, jj] >= dp[ii, jj - 1]) ii--;
            else jj--;
        }
        pairs.Reverse(); // 回溯是从尾往头走的，转成时间正序方便调用方顺着用
        return new Alignment(pairs, offset);
    }

    /// <summary>分三类算字符占比：(假名与谚文, 拉丁字母, 汉字)，分母为非空白字符数。
    ///
    /// 刻意不合成一个数：判「这份是不是中文译文」只能看假名 / 谚文与汉字，
    /// 因为中文译文里保留英文段落太常见了（实测这首「ハツコイノウタ」的译文里就夹着
    /// "I want u baby"、"属于我的love song"，拉丁字母一并计入的话占比 11%，
    /// 一刀切的阈值会把它挡在门外）。而判 KRC 那侧是不是原文两类都要看：
    /// 日 / 韩原文靠假名谚文，英文原文只有拉丁字母。</summary>
    private static (double KanaHangul, double Latin, double Han) ScriptRatios(string s)
    {
        var total = 0;
        var kana = 0;
        var latin = 0;
        var han = 0;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            total++;
            // 中点与长音符落在假名区段里，但它们是记号、中文文本也会用，不足以判语言
            if (c is '・' or 'ー') continue;
            if (c is >= '぀' and <= 'ヿ'      // 平假名 + 片假名
                || c is >= '가' and <= '힯') kana++; // 谚文音节
            else if (char.IsAsciiLetter(c)) latin++;
            else if (c is >= '一' and <= '鿿') han++; // CJK 统一汉字
        }
        return total == 0 ? (0, 0, 0)
            : ((double)kana / total, (double)latin / total, (double)han / total);
    }

    /// <summary>认出「主歌词整份是中文译文、原文只存在于 KRC 那侧」的投稿。
    /// 成立时给出按时间对齐的结果，以及改造过的主歌词（正文挪到译文位上）。
    ///
    /// 实测 7co「ハツコイノウタ」：网易云那份 lrc 从头到尾是中文翻译、tlyric 是空的，
    /// 完整的日文原文躺在酷狗 KRC 里。这时文本相似度对齐必然全线落空（日文比中文，
    /// 52 行只凑巧配上 3 行），既反转不了主从也挂不上逐字，任务栏上只剩中文——
    /// 主人反馈的「没有原文只有译文」就是这一幕。
    ///
    /// 文本已经不能当判据，只剩两样证据，必须同时成立：
    ///   1. 语言对不上，且**主歌词那侧确实是中文**。后半句一点都不能省：英文歌的
    ///      主歌词是英文原文、KRC 也是英文，只看「KRC 像原文」的话两边都成立，
    ///      反转后上下两行会显示一模一样的英文。同理，拉丁那道门槛必须定得高——
    ///      中文歌里夹几句英文很常见，门槛低了会把「两侧都是中文」也认成原文/译文对。
    ///   2. 时间轴逐行贴合：这是区分「原文/译文对」与「酷狗压根搜错了歌」的唯一办法。
    ///      两首不同的歌，不可能大半行的行首在扣掉恒定偏移后还两两对得上。
    /// 单靠任一条都会错判，所以宁可漏掉几首（代价是照旧只显示译文，跟改之前一样），
    /// 也不能把别的歌的原文贴上来——那比没有原文严重得多。</summary>
    private static bool TryAlignAsTranslated(
        List<LyricLine> mainLines, List<KrcLine> krcLines, string secondLine,
        out Alignment al, out List<LyricLine> asTrans)
    {
        al = new Alignment(new List<(int, int)>(), 0);
        asTrans = mainLines;
        if (mainLines.Count == 0 || krcLines.Count == 0) return false;

        // 主歌词已经带着译文，说明它的正文本来就是原文，没什么可换的
        if (mainLines.Count(l => !string.IsNullOrEmpty(l.Trans)) > mainLines.Count * 0.1) return false;
        // 行数悬殊：残缺的副歌版 KRC 当主体会丢大段歌词，行数远多于主歌词则多半是另一首歌
        if (krcLines.Count < mainLines.Count * 0.6 || krcLines.Count > mainLines.Count * 2) return false;

        var mainR = ScriptRatios(string.Concat(mainLines.Select(l => l.Text)));
        var krcR = ScriptRatios(string.Concat(krcLines.Select(k => k.Plain)));
        if (mainR.KanaHangul > MaxKanaHangulRatio || mainR.Han < MinHanRatio) return false;
        if (krcR.KanaHangul < MinKanaHangulRatio && krcR.Latin < MinLatinRatio) return false;

        var byTime = AlignByTime(mainLines, krcLines);
        // 配对率按行少的那侧算（两侧行数本就允许有差）
        if (byTime.Pairs.Count < Math.Min(mainLines.Count, krcLines.Count) * 0.7) return false;
        var devs = byTime.Pairs
            .Select(p => Math.Abs(krcLines[p.Krc].StartMs - byTime.OffsetMs - mainLines[p.Main].Ms))
            .OrderBy(d => d).ToList();
        if (devs[devs.Count / 2] > MaxTimeAlignMedianMs) return false;

        al = byTime;
        // 把中文正文挪到译文位上：BuildFromKrc 挂的是主歌词的译文字段（那才是它的语义），
        // 这么一换，反转后自然是 KRC 原文在上、中文在下，它一行都不用改。
        // 罗马音模式不挪：用户要的是罗马音，塞中文进去只会让人以为设置没生效
        // （这类投稿没有罗马音数据，第二行就空着，但上行的原文和全曲逐字照样拿到）
        if (secondLine == "translation")
            asTrans = mainLines.Select(l => new LyricLine(l.Ms, l.Text, l.Text)).ToList();
        return true;
    }

    /// <summary>只按行首时间把两侧的行一一对上，完全不看文本（文本对不上正是它的用途，
    /// 见 TryAlignAsTranslated）。跑两轮：先按零偏移得一份粗配对，取它时间差的中位数
    /// 当偏移再跑一轮——两个曲库对同一首歌的录入常整体差一个恒定量（前奏剪得不一样长），
    /// 不校掉的话整首都配歪。只做 1:1：没有文本可依时，合并配对纯属瞎猜。</summary>
    private static Alignment AlignByTime(List<LyricLine> mainLines, List<KrcLine> krcLines)
    {
        var rough = AlignByTimeOnce(mainLines, krcLines, 0);
        if (rough.Pairs.Count == 0) return rough;
        var diffs = rough.Pairs
            .Select(p => krcLines[p.Krc].StartMs - mainLines[p.Main].Ms)
            .OrderBy(d => d).ToList();
        return AlignByTimeOnce(mainLines, krcLines, diffs[diffs.Count / 2]);
    }

    private static Alignment AlignByTimeOnce(
        List<LyricLine> mainLines, List<KrcLine> krcLines, int offset)
    {
        var n = mainLines.Count;
        var m = krcLines.Count;

        // 时间越近分越高。0 专门表示「不许配对」，所以贴着容差上限的行也留 0.1 分
        double Score(int i, int j)
        {
            var d = Math.Abs(krcLines[j].StartMs - offset - mainLines[i].Ms);
            return d > MaxTimeAlignShiftMs ? 0 : 1.0 - 0.9 * d / MaxTimeAlignShiftMs;
        }

        // 与 AlignLines 同一套单调序列对齐，只是打分换成纯时间
        var dp = new double[n + 1, m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var best = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                var s = Score(i - 1, j - 1);
                if (s > 0) best = Math.Max(best, dp[i - 1, j - 1] + s);
                dp[i, j] = best;
            }
        }

        var pairs = new List<(int Main, int Krc)>();
        var (ii, jj) = (n, m);
        while (ii > 0 && jj > 0)
        {
            var s = Score(ii - 1, jj - 1);
            if (s > 0 && dp[ii, jj] <= dp[ii - 1, jj - 1] + s + 1e-9)
            {
                pairs.Add((ii - 1, jj - 1));
                ii--;
                jj--;
            }
            else if (dp[ii - 1, jj] >= dp[ii, jj - 1]) ii--;
            else jj--;
        }
        pairs.Reverse();
        return new Alignment(pairs, offset);
    }

    /// <summary>认曲库塞在开头的「歌手 - 歌名」标题行。它一个职务词都不带，正则的
    /// 词表抓不到，只能靠「曲名与歌手名同时出现」来认——歌词正文里同时出现这两者
    /// 的概率极低（副歌复述曲名很常见，但不会连歌手名一起唱）。</summary>
    private static bool LooksLikeTitleLine(string text, string title, string artist)
    {
        if (title.Length == 0 || artist.Length == 0) return false;
        var t = NormalizeForMatch(text);
        return t.Contains(NormalizeForMatch(title)) && t.Contains(NormalizeForMatch(artist));
    }

    /// <summary>方案 A：以主歌词为文本主体，把 KRC 的逐字时间挂上去。
    /// 配不上的行没有逐字数据，调用方会退化成匀速合成扫过。</summary>
    private static Dictionary<int, List<KaraokeWord>> AttachKaraoke(
        List<LyricLine> mainLines, List<KrcLine> krcLines, Alignment al)
    {
        var karaoke = new Dictionary<int, List<KaraokeWord>>();
        var usedKrc = new HashSet<int>();
        foreach (var (i, j) in al.Pairs)
        {
            // 二对一（一个 KRC 行覆盖了两个主行）：字表没有能拆开的锚点，按字数硬切纯属猜，
            // 只给第一个主行用，另一行退回匀速合成
            if (!usedKrc.Add(j)) continue;
            // KRC 那行的字时间是相对它自己的行首的，挂到主歌词行上要补两行行首之差
            var shift = krcLines[j].StartMs - al.OffsetMs - mainLines[i].Ms;
            var words = ShiftWords(krcLines[j].Words, shift);
            // 一对二（KRC 把这一句拆成两行唱）：两行字表接起来正好覆盖主行全文。
            // 复制一份再接，ShiftWords 在 shift 为 0 时会把原 list 直接还回来
            if (karaoke.TryGetValue(mainLines[i].Ms, out var prev)) prev.AddRange(words);
            else karaoke[mainLines[i].Ms] = new List<KaraokeWord>(words);
        }
        return karaoke;
    }

    /// <summary>方案 B（默认，见 FetchAsync 的闸门）：以 KRC 为文本主体，
    /// 把主歌词的译文按配对挂上来。
    ///
    /// 为什么要反转：逐字数据在 KRC 里本就是全曲每行都有的，而跨曲库配对总有失手的行。
    /// 以主歌词为主体时，失手的代价落在逐字上——整行退化成匀速均分，而歌唱节奏天生极不
    /// 均匀（实测同一行内单字从 148ms 到 3280ms，差 22 倍），扫过条明显跟不上人声，
    /// 这正是「同一首歌里逐字时准时不准」的由来。反过来以 KRC 为主体，逐字天然 100%，
    /// 失手的代价只是这一行没有译文（中文歌本就没译文，纯赚）。</summary>
    private static (List<LyricLine> Lines, Dictionary<int, List<KaraokeWord>> Karaoke) BuildFromKrc(
        List<LyricLine> mainLines, List<KrcLine> krcLines, Alignment al)
    {
        var transOf = new Dictionary<int, string>();
        var krcsOf = new Dictionary<int, List<int>>(); // 主行 -> 它配到的 KRC 行
        foreach (var (i, j) in al.Pairs)
        {
            if (krcsOf.TryGetValue(i, out var l)) l.Add(j);
            else krcsOf[i] = new List<int> { j };
            var t = mainLines[i].Trans;
            if (string.IsNullOrEmpty(t)) continue;
            // 二对一（KRC 把两句并成一行唱）：两句译文接起来给这一行，
            // 直接赋值会让后一句把前一句覆盖掉
            transOf[j] = transOf.TryGetValue(j, out var prev) ? prev + " " + t : t;
        }

        var mainOf = new Dictionary<int, List<int>>(); // KRC 行 -> 它配到的主行
        foreach (var (i, j) in al.Pairs)
        {
            if (mainOf.TryGetValue(j, out var m)) m.Add(i);
            else mainOf[j] = new List<int> { i };
        }

        // 向后吸附：把「一对二里只认出一半」的那半找回来。
        //
        // KRC 常把主歌词的一句拆成两行唱，而两侧对同一个词的写法可能不同
        // （实测 Superfly「Bi-Li-Li Emotion」：KRC 写「諸行無常ね ジーザス」，
        // 主歌词写「諸行無常ね、Jesus! 全てはフェイドアウト」——片假名对拉丁字母）。
        // 于是只有后半句文本对得上，前半句一行配不到任何主行，krcsOf 里那句就只有
        // 一个 KRC 行、凑不满「一对二」的门槛，下面的合并逻辑不接管，这行就空着译文，
        // 显示上退化成「第二行显示下一句」——主人看到的正是这一幕。
        //
        // 判据不能再靠文本（它已经失手了），改用时间：主行的行首时间几乎就等于
        // 这一行的行首、且明显比下一行更近，说明这句主歌词从这一行就开始唱了。
        // 三道闸一起卡（主行只配到一个 KRC 行、主行有译文、时间差 < 1.5s 且更近），
        // 错吸的代价才不会大于收益。
        for (var j = 0; j < krcLines.Count - 1; j++)
        {
            if (mainOf.ContainsKey(j)) continue;                 // 本来就配上了
            if (!mainOf.TryGetValue(j + 1, out var next) || next.Count != 1) continue;
            var i = next[0];
            if (krcsOf[i].Count != 1 || string.IsNullOrEmpty(mainLines[i].Trans)) continue;
            var mainMs = mainLines[i].Ms + al.OffsetMs;          // 换到 KRC 时间轴
            var dj = Math.Abs(mainMs - krcLines[j].StartMs);
            if (dj > 1500 || dj >= Math.Abs(mainMs - krcLines[j + 1].StartMs)) continue;
            krcsOf[i].Add(j);
            mainOf[j] = new List<int> { i };
            // 译文按 KRC 行索引存，而吸附进来的这行排在前面、会成为合并后的组首
            // （显示时只取组首那行的译文）——不一起挂上去，合并完反倒一行译文都没有
            transOf[j] = mainLines[i].Trans!;
        }

        // 一对二（KRC 把主歌词的一句拆成两行唱）且这句有译文时，把这两行并回一行显示。
        // 不并的话两行都挂着同一条译文，同一句翻译连着出现两遍，看着像卡带重复了；
        // 而按字数把译文切两半纯属猜。并回去不损失逐字——字表接起来正好覆盖这一整句。
        // 没译文的歌不并：KRC 的细断句本身更好读，行更短也更不容易触发横向滚动
        var groupOf = new Dictionary<int, List<int>>(); // 组首 KRC 行 -> 组内全部行
        var headOf = new Dictionary<int, int>();        // 组内任一行 -> 组首行
        foreach (var i in krcsOf.Keys.OrderBy(x => x))  // 定序遍历，结果不随字典枚举顺序变
        {
            var list = krcsOf[i];
            if (list.Count < 2 || string.IsNullOrEmpty(mainLines[i].Trans)) continue;
            list.Sort();
            // 只并相邻且未被别的组占用的行：单调对齐下一对多必然相邻，
            // 不相邻说明这组配对已经不可信，硬并会把中间那行的文本顺序和时间一起搅乱
            var ok = !headOf.ContainsKey(list[0]);
            for (var k = 1; k < list.Count && ok; k++)
                ok = list[k] == list[k - 1] + 1 && !headOf.ContainsKey(list[k]);
            if (!ok) continue;
            foreach (var j in list) headOf[j] = list[0];
            groupOf[list[0]] = list;
        }

        // 夹逼填空：补配对空隙落下的译文。
        //
        // 以 KRC 为主体的代价是配对失手时那一行没译文（见本方法注释）。但失手往往是
        // 孤立的一行——它前后两行都配上了，只有它自己因为用词差异（同一首歌的两个版本、
        // 简繁、语气词）相似度没过门槛。这时中间那行主歌词是谁其实是确定的：对齐是
        // 单调的（见 AlignLines 的 DP），前一 KRC 行配到主行 p、后一行配到主行 q，
        // 而 q 与 p 之间正好只隔一行，那被跳过的主行 p+1 只可能对应被跳过的这行 KRC。
        // 严格要求「正好隔一行」而不是「取区间里第一个有译文的」：多隔几行时中间是
        // 哪几行对哪几行就有多种排法，猜错会把译文错配到别的句子上——那比没译文更糟。
        // 主人反馈的「偶尔有几句少了翻译」还有这一路空隙。
        var mainUsed = new HashSet<int>(al.Pairs.Select(p => p.Main));
        for (var j = 1; j < krcLines.Count - 1; j++)
        {
            if (mainOf.ContainsKey(j)) continue; // 这行本来就配上了
            if (!mainOf.TryGetValue(j - 1, out var prev)) continue;
            if (!mainOf.TryGetValue(j + 1, out var next)) continue;
            var mid = prev.Max() + 1;
            if (next.Min() - prev.Max() != 2) continue; // 中间不是恰好一行，不猜
            if (mainUsed.Contains(mid)) continue;       // 那行已被别的 KRC 行配走
            var t = mainLines[mid].Trans;
            if (!string.IsNullOrEmpty(t)) transOf[j] = t;
        }

        var lines = new List<LyricLine>(krcLines.Count);
        var karaoke = new Dictionary<int, List<KaraokeWord>>(krcLines.Count);
        for (var j = 0; j < krcLines.Count; j++)
        {
            if (headOf.TryGetValue(j, out var head) && head != j) continue; // 已并进组首那行
            // 换算回主歌词那一侧的时间轴：播放进度由播放器上报，对应的是它自己那份音频，
            // 直接用 KRC 的绝对时间会整首歌偏一个两版之差
            var ms = Math.Max(0, krcLines[j].StartMs - al.OffsetMs);
            if (karaoke.ContainsKey(ms)) continue; // 撞到同一毫秒（如 offset 把开头几行都压到 0）
            var text = krcLines[j].Plain;
            var words = krcLines[j].Words; // 字时间已相对本行行首，不需要平移
            if (groupOf.TryGetValue(j, out var group))
            {
                words = new List<KaraokeWord>();
                foreach (var g in group)
                    // 并进来的行，字时间要从「相对自己行首」改成「相对组首行的行首」
                    words.AddRange(ShiftWords(krcLines[g].Words,
                        krcLines[g].StartMs - krcLines[j].StartMs));
                // 文本按 ParseKrc 的同一公式重算：高亮边界是逐字累加字宽算出来的，
                // 显示文本必须与字表严格同源，拿 Plain 直接相接会因它已 Trim 过而错位
                text = string.Concat(words.Select(w => w.Text)).Trim();
            }
            lines.Add(new LyricLine(ms, text, transOf.TryGetValue(j, out var tr) ? tr : null));
            karaoke[ms] = words;
        }
        return (lines, karaoke);
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

    /// <summary>抓取结果：歌词行 + 逐字时间表 + 命中的源名 + 曲库登记的歌曲时长（秒，未知为 0）。</summary>
    public readonly record struct FetchResult(
        List<LyricLine>? Lines,
        Dictionary<int, List<KaraokeWord>> Karaoke,
        string Source,
        double SongDurationS);

    /// <summary>依次尝试各歌词源。
    /// 逐字表：{主行时间ms: 逐字}，取不到或匹配不上时为空 dict（退化为逐行显示）。
    /// secondLine: translation 译文 | romaji 罗马音（仅网易云源支持）| off 关闭。
    /// useCache: 命中磁盘缓存时直接返回，抓到首选源结果时写入缓存。</summary>
    public static async Task<FetchResult> FetchAsync(string title, string artist, double durationS = 0,
        bool withKaraoke = true, string secondLine = "translation", bool useCache = true)
    {
        var cacheKey = LyricsCache.KeyFor(title, artist, durationS, withKaraoke, secondLine);
        if (useCache && LyricsCache.TryLoad(cacheKey) is { } hit)
            return hit;

        SourceResult? found = null;
        var sourceName = "";
        var sources = new (string Name, Func<Task<SourceResult?>> Fetch)[]
        {
            ("_fetch_netease", () => FetchNeteaseAsync(title, artist, durationS, secondLine)),
            ("_fetch_qq", () => FetchQqAsync(title, artist, durationS)),
            ("_fetch_lrclib", () => FetchLrclibAsync(title, artist, durationS)),
        };
        foreach (var (name, fetch) in sources)
        {
            try { found = await fetch(); }
            catch { found = null; } // 单个源网络异常不致命，换下一个
            if (found is { Lines.Count: > 0 })
            {
                sourceName = name;
                break;
            }
        }
        if (found is not { Lines.Count: > 0 })
            return new FetchResult(null, new Dictionary<int, List<KaraokeWord>>(), "", 0);
        var lines = found.Lines;

        // 过滤制作信息行（作词/编曲/制作人等不是歌词，不该占用任务栏）。
        // 滤掉超过六成就认定是正则误伤（正常歌曲的制作信息只占开头几行），整份留原样：
        // 宁可多显示几行制作信息，也不能把歌词本身滤成残缺的
        // 译文轨也查一遍：有些投稿把制作信息塞在翻译那一行上（正文是作品名、
        // 译文写「作詞：某某 作曲：某某」），只看正文会漏掉整行
        var filtered = lines.Where(l => !CreditLineRegex().IsMatch(l.Text)
            && !(l.Trans != null && CreditLineRegex().IsMatch(l.Trans))).ToList();
        if (filtered.Count > 0 && filtered.Count >= lines.Count * 0.4) lines = filtered;

        var karaoke = new Dictionary<int, List<KaraokeWord>>();
        if (withKaraoke)
        {
            try
            {
                var krc = await FetchKugouKaraokeAsync(title, artist, durationS);
                if (krc is { Count: > 0 })
                {
                    // KRC 侧也得滤制作信息：它开头那几行（「歌手 - 歌名」、「作词：…」、
                    // 「编曲：…」）同样带着逐字时间戳，一旦拿 KRC 当文本主体就会显示到任务栏上。
                    // 标题行只在开头几行查：往后再出现同名文本就是副歌在唱曲名了
                    var kept = krc.Where((k, i) => !CreditLineRegex().IsMatch(k.Plain)
                        && !(i < 4 && LooksLikeTitleLine(k.Plain, title, artist))).ToList();
                    if (kept.Count > 0 && kept.Count >= krc.Count * 0.4) krc = kept;

                    var al = AlignLines(lines, krc);
                    // 两道闸决定敢不敢反转主从（反转的收益见 BuildFromKrc 的注释）：
                    //   1. 配对覆盖了 KRC 的大半行——覆盖率极低多半是酷狗那边搜错了歌，
                    //      此时用它的文本就是显示错歌词，比逐字不准严重得多；
                    //   2. KRC 行数不明显少于主歌词——只录了副歌的残缺版 KRC 当主体
                    //      会把大段歌词丢掉。
                    var covered = al.Pairs.Count / (double)krc.Count;
                    if (covered >= 0.5 && krc.Count >= lines.Count * 0.6)
                        (lines, karaoke) = BuildFromKrc(lines, krc, al);
                    // 文本对不上还有一种成因不是「搜错歌」：这份投稿的主歌词整份是中文
                    // 译文，原文只在 KRC 那侧。此时反转不但照旧成立，而且是唯一能让原文
                    // 上屏的路（判据全靠时间轴与语言，见 TryAlignAsTranslated）
                    else if (TryAlignAsTranslated(lines, krc, secondLine, out var tal, out var asTrans))
                        (lines, karaoke) = BuildFromKrc(asTrans, krc, tal);
                    else
                        karaoke = AttachKaraoke(lines, krc, al);
                }
            }
            catch
            {
                karaoke = new Dictionary<int, List<KaraokeWord>>(); // 逐字失败不影响逐行
            }
        }
        var result = new FetchResult(lines, karaoke, sourceName, found.DurationS);
        // 只缓存首选源的结果：落到备选源说明首选源当时抓失败了（多半是网络抖动），
        // 那是调用方 5s 后要重试自愈的情况，写进缓存等于把「没有译文的次优结果」
        // 永久冻结，重试也只会一遍遍读到同一份坏缓存
        if (useCache && sourceName == "_fetch_netease")
            LyricsCache.Save(cacheKey, result);
        return result;
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

    /// <param name="secondLine">第二行内容：translation / romaji / off，与设置页同名。</param>
    public static async Task RunConsoleTestAsync(string title, string artist,
        string secondLine = "translation")
    {
        // 诊断入口一律绕过缓存：否则改完匹配算法再来验证，读到的还是上次的结果
        var (found, karaoke, source, songDur) =
            await FetchAsync(title, artist, secondLine: secondLine, useCache: false);
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
        Console.WriteLine($"... 共 {found.Count} 行，其中 {hasTrans} 行带译文，"
            + $"{karaoke.Count} 行带逐字（{karaoke.Count * 100.0 / found.Count:F0}%）"
            + $"（来源 {source}，曲库时长 {songDur:F0}s）");

        // 主歌词侧对照（不抓逐字，拿到的就是网易云原样的行集）：反转成以 KRC 为文本主体后
        // 译文是靠跨源配对挂回来的，某行缺译文有两种完全不同的成因——网易云自己的译文轨
        // 就没对上这行（MergeTranslation 的时间容差），或者跨源配对没把它挂上。两侧一比即分晓
        var (raw, _, _, _) = await FetchAsync(title, artist, withKaraoke: false,
            secondLine: secondLine, useCache: false);
        if (raw != null)
        {
            Console.WriteLine($"主歌词侧（未反转）：{raw.Count} 行，{raw.Count(l => l.Trans != null)} 行带译文");
            foreach (var (ms, text, trans) in raw.Take(4))
                Console.WriteLine($"  原 {ms / 1000.0,8:F2}s  {text}"
                    + (trans != null ? $"  /  {trans}" : "  ← 无译文"));
        }

        // KRC 侧对照：逐字数据本身是全曲每行都有的，配对率低有两种完全不同的成因——
        // 阈值太严（放宽能救）或两源断句粒度不同（酷狗把主歌词两行并成一行唱，
        // 单调对齐 1:1 挂不过来，放宽阈值也救不了）。行数与行长的对比能区分这两种
        List<KrcLine>? krc = null;
        try { krc = await FetchKugouKaraokeAsync(title, artist, songDur); }
        catch { /* 逐字源抓失败不影响上面的主歌词诊断 */ }
        if (krc is not { Count: > 0 })
        {
            Console.WriteLine("酷狗 KRC：没抓到");
            return;
        }
        var lens = krc.Select(k => k.Plain.Length).OrderBy(x => x).ToList();
        Console.WriteLine($"酷狗 KRC：{krc.Count} 行（主歌词 {found.Count} 行），"
            + $"行长中位 {lens[lens.Count / 2]} 字、最长 {lens[^1]} 字");
        foreach (var k in krc.Take(4))
            Console.WriteLine($"  KRC {k.StartMs / 1000.0,8:F2}s  {k.Plain}");

        // 「主歌词整份是译文、原文在 KRC 侧」的三道闸各自实测值（见 TryAlignAsTranslated）。
        // 上面显示的行全是中文却一句原文都没有时，看这行就知道是哪一条没过
        // ——语言闸不过多半是两侧都是中文（本来就该这样），时间闸不过则是酷狗搜错了歌
        if (raw != null)
        {
            var byTime = AlignByTime(raw, krc);
            var devs = byTime.Pairs
                .Select(p => Math.Abs(krc[p.Krc].StartMs - byTime.OffsetMs - raw[p.Main].Ms))
                .OrderBy(d => d).ToList();
            var mainR = ScriptRatios(string.Concat(raw.Select(l => l.Text)));
            var krcR = ScriptRatios(string.Concat(krc.Select(k => k.Plain)));
            Console.WriteLine("译文投稿判据：主歌词假名/谚文 "
                + $"{mainR.KanaHangul:P0}（需 ≤{MaxKanaHangulRatio:P0}）、汉字 "
                + $"{mainR.Han:P0}（需 ≥{MinHanRatio:P0}）；"
                + $"KRC 假名/谚文 {krcR.KanaHangul:P0}、拉丁 {krcR.Latin:P0}"
                + $"（需 ≥{MinKanaHangulRatio:P0} 或 ≥{MinLatinRatio:P0}）；"
                + $"纯时间配对 {byTime.Pairs.Count}/{Math.Min(raw.Count, krc.Count)}（需 ≥70%），"
                + $"全局偏移 {byTime.OffsetMs}ms，逐行偏差中位 "
                + (devs.Count > 0 ? $"{devs[devs.Count / 2]}ms" : "—")
                + $"（需 ≤{MaxTimeAlignMedianMs}ms）");
        }
    }
}
