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
    //  7) 日文曲库的片假名职务（ギター/ミックス/ディレクター…）与「原唱/原曲」。
    // 词表再怎么补也收不全乐手表（什么乐器都有），开头/结尾整块的放宽判定见 CreditMask
    [GeneratedRegex(@"^[\s(（\[【「『]*(?:(?:作词|作詞|作曲|编曲|編曲|改编|改編|填词|填詞|词曲|詞曲|制作|製作|制作人|製作人|监制|監製|监督|監督|出品|出品人|承制|承製|发行|發行|企划|企劃|策划|策劃|统筹|統籌|混音|母带|母帶|后期|後期|录音|錄音|录音室|錄音室|工作室|和声|和聲|合声|合聲|伴唱|主唱|配唱|合唱|演唱|演奏|吉他|贝斯|貝斯|鼓手|键盘|鍵盤|弦乐|弦樂|管乐|管樂|编写|編寫|封面|设计|設計|美术|美術|海报|海報|文案|宣传|宣傳|推广|推廣|翻译|翻譯|校对|校對|唱片|专辑|專輯|歌手|歌名|歌曲|版权|版權|著作权|著作權|授权|授權"
        + @"|小提琴|中提琴|大提琴|提琴|钢琴|鋼琴|长笛|長笛|笛子|唢呐|嗩吶|二胡|古筝|古箏|琵琶|竹笛|萨克斯|薩克斯|口琴|手风琴|手風琴|合成器|打击乐|打擊樂|人声|人聲|编程|編程|特别支持|特別支持|特别鸣谢|特別鳴謝|特别感谢|特別感謝|鸣谢|鳴謝|感谢|感謝|音乐总监|音樂總監|总监|總監|监棚|監棚|艺人|藝人|经纪|經紀|词曲版权|詞曲版權"
        + @"|原唱|原曲|ボーカル|ギター|ベース|ドラム|キーボード|ストリングス|コーラス|ミックス|マスタリング|レコーディング|エンジニア|ディレクター|ディレクション|プロデューサー|プロデュース|アレンジ|イラスト|動画|映像|調声|調教)[^:：]{0,16}[:：]"
        + @"|(?:[A-Za-z]{1,12}[\s.&/-]+){0,2}(?:OP|SP|ISRC|UPC|lyrics?|lyricist|composed?|composer|arrange[rd]?|arrangement|arranged|music|produced?|producer|vocals?|chorus|guitar|bass|drums?|keyboards?|strings|violin|viola|cello|piano|flute|trumpet|trombone|horn|organ|sax\w*|synth\w*|percussion|programming|instrument\w*|direct(?:or|ion|ed)|assist\w*|engineer\w*|special\s+thanks|mix\w*|master\w*|record\w*|perform\w*|writer|written|label|studio|lrc|krc|qrc|trc)[^:：]{0,16}[:：]"
        + @"|[词詞曲唱鼓歌][\s/、&＆和与]*[词詞曲唱鼓歌]?\s*[:：]"
        + @"|(?:未经|未經|本(?:歌曲|作品|专辑|專輯))[^\n]{0,30}(?:许可|許可|授权|授權|版权|版權|同意)"
        + @"|.*(?:版权所有|版權所有|all\s+rights\s+reserved|unauthorized\s+(?:reproduction|copying)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditLineRegex();

    // 「键：值」形状的行，键取冒号前那段，只在开头/结尾的制作信息块里用（见 CreditMask）。
    // 键里不许有句读和引号（那是在说话，不是在报职务），也不许有平假名：日文职务名只用
    // 汉字、片假名或英文（作詞/ギター/Sound Director），出现平假名就是在唱歌词。
    // 冒号必须是全角，或半角后面跟空白——半角紧贴着值的 "BPM:140"、"4:30 AM"、
    // "RE:ラビュー" 都是歌词本身，前一个还正好是某首歌的最后一句
    [GeneratedRegex(@"^[\s(（\[【「『]*(?<key>[^:：。，！？!?「」『』“”""ぁ-ゟ]{1,40}?)\s*(?:：|:\s)\s*\S")]
    private static partial Regex KeyValueLineRegex();

    // 对唱/分段标签（「男：」「合唱：」「Rap：」）标明的是谁唱、唱哪段，后面跟的是歌词。
    // 形状和制作信息一模一样，放宽判定时得单独放行；单字的（男/女/合/A）按长度放行，不列在这
    [GeneratedRegex(@"^(?:男女|女男|男声|女声|男聲|女聲|童声|童聲|全体|全體|全员|全員|all|both|rap|verse\s*\d*|hook|bridge|intro|outro|pre-?chorus)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartLabelRegex();

    // 键像在说话：带人称代词，或以「说/问」这类言说动词收尾（「你说：……」「妈妈问：……」）。
    // 职务名从来不带人称，而歌词首句用引语开头并不少见，放宽判定不能把它当成制作信息。
    // 「吉他」「其他/其它乐器」里的他/它不是人称，得让开
    [GeneratedRegex(@"(?<![吉其])他|(?<!其)它|[我你她您咱俺私僕君]|(?:说|說|问|問|讲|講|曰|喊|答)$|\b(?:i|you|he|she|we|they|me|my|your|his|her|our|their)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeechKeyRegex();

    // 标题行里歌名和歌手之间的分隔符，两侧必须有空白：歌词里的「ー」「-」多是连在字上的
    [GeneratedRegex(@"\s[-－–—]\s")]
    private static partial Regex TitleSeparatorRegex();

    // 译文行尾挂着的译者署名：「……（翻译：某某）」「……(翻译水平有限 授权网易云音乐使用)」。
    // 它附在真歌词的译文后面，整行滤掉会连原文一起丢，只能把这段括号剪掉
    [GeneratedRegex(@"\s*[（(][^（）()]*(?:翻译|翻譯|译者|譯者|校对|校對|听译|聽譯|授权|授權|转载|轉載|translat\w*)[^（）()]*[）)]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransCreditSuffixRegex();

    /// <summary>逐行判定是不是制作信息（true = 该滤掉）。所有滤制作信息的地方
    /// （合并译文、译文覆盖率、主歌词、KRC）共用这一个判定，免得各处口径不一。
    ///
    /// 光靠 CreditLineRegex 的职务词表永远收不全：乐手表里什么乐器都有（Trumpet、
    /// Hammond Organ、SN Roll……），还有「Sound Direction」「原唱」「电吉他 Electric Guitar」
    /// 这种词表外或中英双语的写法；主歌词开头的「歌名 - 歌手」标题行更是一个职务词都不带。
    /// 实测缓存里漏网的全是这两类，而且无一例外挤在歌词的最前面。
    ///
    /// 所以按位置放宽：曲库总把制作信息整块放在开头（或结尾），块内每行都是「键：值」。
    /// 从首行往后、从末行往前各扫一段，只要还是「键：值」或标题行就算制作信息，
    /// 碰到第一句正常歌词就停——块外仍只认严格的词表，歌词中间带冒号的句子不受影响。
    /// 放宽的「键：值」还要过两道闸，防的是对唱标签（「男：」「周：」「Rap：」）：
    /// 键不能是分段/对唱标签或歌手名，且同一个键整首只能出现一次——
    /// 对唱标签会反复出现，制作信息里每个职务只报一次。</summary>
    private static bool[] CreditMask(IReadOnlyList<string> texts, IReadOnlyList<string?>? trans,
        string title, string artist)
    {
        var n = texts.Count;
        var mask = new bool[n];
        for (var i = 0; i < n; i++)
            mask[i] = CreditLineRegex().IsMatch(texts[i])
                // 译文轨也查一遍：有些投稿把制作信息塞在翻译那一行上（正文是作品名、
                // 译文写「作詞：某某 作曲：某某」），只看正文会漏掉整行
                || (trans?[i] is { } tr && CreditLineRegex().IsMatch(tr))
                // 标题行只在开头几行查：往后再出现同名文本就是副歌在唱曲名了
                || (i < 4 && LooksLikeTitleLine(texts[i], title, artist));

        var artists = SplitArtists(artist);
        var keys = new string?[n];
        var keyCount = new Dictionary<string, int>();
        for (var i = 0; i < n; i++)
        {
            var m = KeyValueLineRegex().Match(texts[i]);
            if (!m.Success) continue;
            var key = m.Groups["key"].Value.Trim();
            var nk = NormalizeForMatch(key);
            // 键里得有字母：纯数字的是时刻（「4：30」）；
            // 单字键（男/女/合/周/A）是对唱标签；键是歌手名的也是（「周杰伦：……」）
            if (!key.Any(char.IsLetter) || nk.Length <= 1 || PartLabelRegex().IsMatch(key)
                || SpeechKeyRegex().IsMatch(key)
                || artists.Any(a => a == nk || a.Contains(nk))) continue;
            keys[i] = nk;
            keyCount[nk] = keyCount.GetValueOrDefault(nk) + 1;
        }
        bool Edge(int i) => mask[i] || (keys[i] is { } k && keyCount[k] == 1);

        for (var i = 0; i < n && (Edge(i) || LooksLikeHeaderTitle(texts[i], title, artists)); i++)
            mask[i] = true;
        for (var i = n - 1; i >= 0 && Edge(i); i--)
            mask[i] = true;
        return mask;
    }

    /// <summary>主歌词开头的「歌名 - 歌手」（或反过来）标题行，网易云常见、时间戳是 0。
    /// 比 LooksLikeTitleLine 宽：歌名或任一歌手出现一个就够——标题行里的歌手常比 SMTC
    /// 报的多一截（"キタニタツヤ/suis (suis from ヨルシカ)"），要求整串歌手名原样出现就对不上。
    /// 放宽的代价由两条约束兜着：必须带两侧有空白的分隔符，且只在开头的制作信息块里查。</summary>
    private static bool LooksLikeHeaderTitle(string text, string title, List<string> artists)
    {
        if (!TitleSeparatorRegex().IsMatch(text)) return false;
        var t = NormalizeForMatch(text);
        var nt = NormalizeForMatch(title);
        return (nt.Length > 0 && t.Contains(nt)) || artists.Any(t.Contains);
    }

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
        List<(int Ms, string Text)> lines, List<(int Ms, string Text)> trans,
        string title, string artist, int tolMs = 1200)
    {
        var used = new bool[trans.Count];
        var merged = new List<LyricLine>(lines.Count);
        var credit = CreditMask(lines.Select(l => l.Text).ToList(), null, title, artist);
        for (var li = 0; li < lines.Count; li++)
        {
            var (ms, text) = lines[li];
            // 制作信息行不参与配译文。网易云的 lrc 开头一律带「作词/作曲/编曲/制作人」
            // 四五行、tlyric 一律不带，而这些行的时间戳全挤在 0~1s，正好落在首句译文的
            // 时间窗内——让它们参与就会把首句的译文抢走再标成已用，首句反倒没了译文。
            // 这是「首句经常没有翻译」的真凶（Lemon：两边首句都在 00:00.851，却配不上）
            if (credit[li])
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
                var tr = TransCreditSuffixRegex().Replace(trans[best].Text, "");
                merged.Add(new LyricLine(ms, text, tr.Length > 0 ? tr : null));
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
    /// 重播、进度归零、显示回开头那句（主人反馈的「快结束时又显示开头歌词」）。
    ///
    /// Degraded / NotFound 只有网易云源会置位，含义对应 FetchResult 的 Degraded / PrimaryNotFound：
    /// 前者是「首选候选的歌词请求失败、用了次选候选」，后者是「正常应答、确实没有」（此时 Lines 为空）。
    /// 「确实没有」不能再用 null 表达——null 同时还代表限流、错误码，调用方分不清该不该重试。
    /// ArtistMismatch：候选里一个歌手都对不上，是放开歌手闸挑出来的（见 PreferArtistMatched），
    /// 多半是同名翻唱。它只配当备胎，FetchAsync 会先去别的源找歌手对得上的版本。</summary>
    private sealed record SourceResult(List<LyricLine> Lines, double DurationS,
        bool Degraded = false, bool NotFound = false, bool ArtistMismatch = false);

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
        // 限流 / 风控时网易云照样回 HTTP 200，只是 JSON 的 code 变成 -460 / -462 / 405 / 406 之类，
        // 也不带 result。原先这和「确实搜不到」一起走 return null，调用方就分不清是该等它恢复
        // 还是该死心——所以非正常应答直接抛出，按请求失败处理（FetchAsync 会换下一个源）
        if (!NeteaseOk(search.RootElement))
            throw new InvalidDataException($"网易云搜索应答异常 code={NeteaseCode(search.RootElement)}");
        // 走到这里是正常应答：没有 songs（搜索结果为 0 时连这个字段都不带）就是确实没有
        if (!search.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
            return new SourceResult(new List<LyricLine>(), 0, NotFound: true);
        // 歌手宽松匹配：SMTC 的歌手串常是多歌手（"A/B"）或变体名，
        // 严格相等会漏歌（「Lyricify 能显示而我们不能」的主因之一）。比较规则见 ArtistMatches
        static bool ArtistMatch(JsonElement song, string artist)
        {
            if (!song.TryGetProperty("artists", out var artists)
                || artists.ValueKind != JsonValueKind.Array) return false;
            return ArtistMatches(artists.EnumerateArray()
                .Select(a => a.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : ""), artist);
        }
        // 别名只给版本标签判断用：有的 Live / 伴奏条目歌名是干净的，标签在 alias 里
        static string? AliasOf(JsonElement song) =>
            song.TryGetProperty("alias", out var al) && al.ValueKind == JsonValueKind.Array
                ? string.Join(" ", al.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : ""))
                : null;
        var all = songs.EnumerateArray().ToList();
        // 曲库登记的时长（毫秒 → 秒），既用于挑候选也要带回给调用方补 SMTC 的空缺
        static double DurOf(JsonElement song) =>
            song.TryGetProperty("duration", out var dv) ? dv.GetDouble() / 1000 : 0.0;
        // 歌名必须匹配（必要条件），歌手匹配与时长接近只是排序权重——
        // 只按歌手+时长挑会把同歌手、时长接近的别的歌抓来（主人反馈偶尔匹配错歌）
        var scored = all
            .Select(s => (Song: s,
                          Ts: TitleScore(s.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "", title,
                              AliasOf(s)),
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
        // 歌曲搜索会漏掉个别曲目：实测ヘクとパスカル「fish in the pool」（同名专辑的主打曲）
        // 换关键词、换搜索接口都搜不出来，只搜得到同专辑没歌词的「fish in the pool・花屋敷」，
        // 于是判成「网易云没有这首歌」退到 QQ，拿回一份不带译文的英文歌词——而网易云上
        // 这首明明有整份中文译文。专辑搜索倒能找到那张专辑，专辑详情里也列着这首歌。
        // 所以只在搜不到「歌名满分且歌手对得上」的候选时，再按专辑找一遍：平时不多花请求
        var extra = scored.Any(x => x.Ts >= TitleScoreMax && x.Artist)
            ? new List<(long Id, double Dur, int Ts)>()
            : await NeteaseAlbumTracksAsync(title, artist, durationS, referer);
        var ordered = (extra ?? new List<(long Id, double Dur, int Ts)>())
            .Concat(PreferArtistMatched(scored, x => x.Artist)
                .Select(x => (Id: x.Song.GetProperty("id").GetInt64(), Dur: DurOf(x.Song), x.Ts)))
            .ToList();
        // 实测「告白氣球 / 周杰倫」：网易云没有周杰伦的版本，搜出来全是翻唱、伴奏和 beat。
        // 带时长的播放器（QQ 音乐、Spotify）一过时长闸，剩下的歌手全对不上，放开后挑中的是
        // 歌名完全相等的 228s 翻唱——时间轴跟原唱差十几秒，还当成完整结果写进缓存冻结 30 天，
        // 而 QQ 那边明明就有原唱。所以得把「是放开挑的」带出去，由 FetchAsync 统筹各源。
        // 按专辑找回的曲目歌手是对得上的，有它就不算放开挑
        var artistMismatch = extra is not { Count: > 0 } && !scored.Any(x => x.Artist);
        // 候选逐个尝试：同一首歌常有多个版本，
        // 有的版本没译文（主人反馈网易云明显有译文却显示不出来），优先带译文的版本
        // 搜索正常应答、只是没有歌名/时长对得上的候选：同样是「确实没有」。
        // 按专辑找那一步失败了就不能这么说，按请求失败处理
        if (ordered.Count == 0)
            return extra == null ? null : new SourceResult(new List<LyricLine>(), 0, NotFound: true);
        SourceResult? firstResult = null;
        SourceResult? bestTrans = null;
        var bestTs = -1;
        var bestRatio = -1.0;
        // 有候选的歌词请求失败过：这时挑出来的未必是本该挑的那个（多半就是首选候选挂了、
        // 落到了次选），结果要标成降级、不进缓存，否则一次抖动会把次优版本冻结 30 天。
        // 按专辑找那一步失败同理：漏掉的可能正是本该挑的那首
        var anyFailed = extra == null;
        foreach (var (id, dur, ts) in ordered.Take(3))
        {
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
            catch { anyFailed = true; continue; } // 单个候选失败换下一个
            using (lyric)
            {
                var root = lyric.RootElement;
                // 歌词接口被限流时同样是 HTTP 200 + 非 200 的 code、没有 lrc 字段，
                // 不查 code 的话下面会把它当成「这个版本没歌词」静默跳过，算成请求失败才对
                if (!NeteaseOk(root)) { anyFailed = true; continue; }
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
                var merged = MergeTranslation(lines, trans, title, artist);
                // 提前返回时后面的候选还没打，失败只可能出在前面，此刻的 anyFailed 就是全部
                var picked = new SourceResult(merged, dur, Degraded: anyFailed,
                    ArtistMismatch: artistMismatch);
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
                // 覆盖率也必须用比率而不是行数：本意只是「别挑到残缺翻译」，比率就够了。
                // 比率要把制作信息行排除在外再算（判定与 FetchAsync 的过滤一致）：作词/作曲那几行
                // 从来没有译文，按全部行算比率永远到不了 1（「居眠り遠征隊」是 43/45 = 0.956），
                // 下面的收工条件原先等于死代码，每首歌都白打后面两个候选
                var credit = CreditMask(merged.Select(l => l.Text).ToList(),
                    merged.Select(l => l.Trans).ToList(), title, artist);
                var lyricLines = merged.Where((_, i) => !credit[i]).ToList();
                var ratio = lyricLines.Count == 0 ? 0 : lyricLines.Count(l => l.Trans != null) / (double)lyricLines.Count;
                // 歌名满分且译文全覆盖才立刻收工，不白打后面候选的接口。
                // 只看全覆盖是不够的：Live 版也可能全覆盖，先返回就再也轮不到正确的那首。
                // 收工和择优用的是同一个 ratio：满足条件的已是 (歌名满分, 1)，前面不可能有更好的，
                // 后面的在严格 > 的比较下也赢不了它，提前收工与走完全程挑中的是同一个
                if (ts >= TitleScoreMax && ratio >= 1) return picked;
                if (ts > bestTs || (ts == bestTs && ratio > bestRatio))
                {
                    bestTs = ts;
                    bestRatio = ratio;
                    bestTrans = picked;
                }
            }
        }
        var best = bestTrans ?? firstResult;
        // 一个能用的都没有：有候选请求失败就是「没拿到」，返回 null 让调用方换源并重试；
        // 全都正常应答却没有可用歌词（纯音乐、歌词比歌长的错版本）才是「确实没有」
        if (best == null)
            return anyFailed ? null : new SourceResult(new List<LyricLine>(), 0, NotFound: true);
        // 择优挑中的候选可能早于后面某个失败的候选，失败标记要按全程重算
        return best with { Degraded = anyFailed };
    }

    /// <summary>按专辑找回歌曲搜索漏掉的曲目（为什么要找见 FetchNeteaseAsync 的调用处）：
    /// 专辑名与歌名对得上、专辑歌手也对得上的专辑最多看两张，取其中歌名满分、时长不离谱的曲目。
    /// 只认专辑名对得上的：歌手名下的专辑可能有几十张，挨个翻请求太多，而漏搜的
    /// 实测是同名专辑的主打曲。请求失败返回 null（与「确实没找到」的空列表区分开）。</summary>
    private static async Task<List<(long Id, double Dur, int Ts)>?> NeteaseAlbumTracksAsync(
        string title, string artist, double durationS, string referer)
    {
        static string NameOf(JsonElement e) =>
            e.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "";
        static IEnumerable<string> NamesOf(JsonElement e, string key) =>
            e.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(NameOf) : Enumerable.Empty<string>();
        var found = new List<(long Id, double Dur, int Ts)>();
        try
        {
            using var search = await GetJsonAsync(
                "https://music.163.com/api/search/get/web?" + Q(new()
                {
                    ["s"] = $"{title} {artist}", ["type"] = "10", ["limit"] = "10",
                }), referer);
            if (!NeteaseOk(search.RootElement)) return null;
            if (!search.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("albums", out var albums)
                || albums.ValueKind != JsonValueKind.Array)
                return found;
            var albumIds = albums.EnumerateArray()
                .Where(a => TitleScore(NameOf(a), title) > 0 && ArtistMatches(NamesOf(a, "artists"), artist))
                .Select(a => a.GetProperty("id").GetInt64())
                .Take(2)
                .ToList();
            foreach (var albumId in albumIds)
            {
                using var album = await GetJsonAsync($"https://music.163.com/api/v1/album/{albumId}", referer);
                if (!NeteaseOk(album.RootElement)) return null;
                if (!album.RootElement.TryGetProperty("songs", out var songs)
                    || songs.ValueKind != JsonValueKind.Array) continue;
                // 专辑详情里的曲目是另一套字段名：歌手在 ar、时长在 dt（毫秒）
                foreach (var s in songs.EnumerateArray())
                {
                    var ts = TitleScore(NameOf(s), title);
                    if (ts < TitleScoreMax || !ArtistMatches(NamesOf(s, "ar"), artist)) continue;
                    var dur = s.TryGetProperty("dt", out var dt) && dt.ValueKind == JsonValueKind.Number
                        ? dt.GetDouble() / 1000 : 0.0;
                    if (durationS > 0 && dur > 0 && Math.Abs(dur - durationS) > 20) continue;
                    found.Add((s.GetProperty("id").GetInt64(), dur, ts));
                }
            }
        }
        catch
        {
            return null;
        }
        return found;
    }

    /// <summary>网易云应答的 code 是不是 200。缺 code 也按异常算：正常应答一向带着它，
    /// 缺了多半是被网关/风控页替换过的内容，不能当「没有」解读。</summary>
    private static bool NeteaseOk(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
            && c.TryGetInt32(out var v) && v == 200;

    private static string NeteaseCode(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var c)
            ? c.GetRawText() : "缺失";

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
        // 与 NeteaseOk 同理：code 非 0 是限流/风控，得按请求失败抛出，不能当「没这首歌」——
        // FetchAsync 要靠它分辨「退回翻唱备胎」这份结果是最终答案还是这次运气不好
        if (search.RootElement.TryGetProperty("code", out var qc) && qc.ValueKind == JsonValueKind.Number
            && qc.TryGetInt32(out var qv) && qv != 0)
            throw new InvalidDataException($"QQ 搜索应答异常 code={qv}");
        if (!search.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("song", out var song)
            || !song.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return null;

        // 逐个歌手名交给 ArtistMatches 拆分比较。原先拼成 "B A" 一整串再 Contains SMTC 的 "A/B"，
        // 多歌手的歌几乎必然对不上
        static IEnumerable<string> SingerNames(JsonElement s) =>
            s.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array
                ? singers.EnumerateArray().Select(x =>
                    x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                : Enumerable.Empty<string>();

        var songs = list.EnumerateArray().ToList();
        // 与网易云同一套标准：歌名必须匹配，歌手/时长只是排序权重（防兜底拿到同歌手别的歌）
        static double IntervalOf(JsonElement s) =>
            s.TryGetProperty("interval", out var iv) ? iv.GetDouble() : 0;
        var ordered = songs
            .Select(s => (Song: s,
                          Ts: TitleScore(s.TryGetProperty("songname", out var nv) ? nv.GetString() ?? "" : "", title),
                          Artist: ArtistMatches(SingerNames(s), artist),
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
        return new SourceResult(MergeTranslation(lines, trans, title, artist), IntervalOf(chosen),
            ArtistMismatch: !ordered.Any(x => x.Artist));
    }

    // ---- LRCLIB ----

    private static async Task<SourceResult?> FetchLrclibAsync(string title, string artist, double durationS)
    {
        var p = new Dictionary<string, string> { ["track_name"] = title, ["artist_name"] = artist };
        if (durationS > 0) p["duration"] = ((int)durationS).ToString();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/get?" + Q(p));
        req.Headers.TryAddWithoutValidation("User-Agent", "taskbar-lyrics v0.1");
        using var resp = await Http.SendAsync(req);
        // 404 是「确实没有」；别的失败码（5xx、限流）是这次没拿到，抛出去按请求失败算（理由同 QQ）
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("syncedLyrics", out var synced)
            || synced.ValueKind != JsonValueKind.String)
            return null;
        var lines = ParseLrc(synced.GetString() ?? "");
        if (lines.Count == 0) return null;
        var dur = doc.RootElement.TryGetProperty("duration", out var dv)
                  && dv.ValueKind == JsonValueKind.Number ? dv.GetDouble() : 0.0;
        // LRCLIB 按歌名+歌手取条目，一般不会给错歌手；照样核一遍，跟另外两家同一套口径
        var mismatch = doc.RootElement.TryGetProperty("artistName", out var an)
            && an.ValueKind == JsonValueKind.String
            && !ArtistMatches(new[] { an.GetString() ?? "" }, artist);
        return new SourceResult(MergeTranslation(lines, new List<(int, string)>(), title, artist), dur,
            ArtistMismatch: mismatch);
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

    /// <summary>给酷狗候选过滤用的歌曲时长：SMTC 报了就用 SMTC 的，否则退回主歌词源曲库登记的时长。
    /// 网易云客户端的 SMTC 不上报 timeline，durationS 恒为 0，直接传进去酷狗那边的
    /// 「时长差 ≤ 20s」过滤就整个短路，逐字很容易对到同名的 Live/remix 版本上。
    /// 正式路径和诊断入口都走这里，诊断结果才代表正式程序的行为。</summary>
    private static double KugouDurationOf(double smtcDurationS, double songDurationS)
        => smtcDurationS > 0 ? smtcDurationS : songDurationS;

    private static async Task<List<KrcLine>?> FetchKugouKaraokeAsync(string title, string artist, double durationS)
    {
        using var search = await GetJsonAsync(
            "http://mobilecdn.kugou.com/api/v3/search/song?" + Q(new()
            {
                ["format"] = "json", ["keyword"] = $"{title} {artist}", ["page"] = "1", ["pagesize"] = "5",
            }));
        // 与 NeteaseOk 同理：被限流/风控时酷狗常照样回 HTTP 200，只是状态码不对、不带 data.info。
        // 当成「酷狗没有」返回 null 会让这首歌「没有逐字」进缓存冻结 30 天，所以抛出去，
        // 由 FetchAsync 当作请求失败标降级。只有应答正常而确实没结果才返回 null
        if (!KugouOk(search.RootElement, "status", 1) || !KugouOk(search.RootElement, "errcode", 0))
            throw new InvalidDataException("酷狗搜索应答异常");
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
            // singername 是 "A、B" 一整串，拆分交给 ArtistMatches；
            // 为空时它也不会放行（空串是任何串的子串，恒真会误匹配）
            return ArtistMatches(new[] { sn }, artist);
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
        if (!KugouOk(krcSearch.RootElement, "status", 200))
            throw new InvalidDataException("酷狗歌词搜索应答异常");
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

    /// <summary>酷狗应答里的某个状态字段是不是给定的数字。缺字段也按异常算（理由同 NeteaseOk）。</summary>
    private static bool KugouOk(JsonElement root, string key, int expected)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(key, out var c) && c.ValueKind == JsonValueKind.Number
            && c.TryGetInt32(out var v) && v == expected;

    /// <summary>歌名匹配度：0=不匹配；归一化完全相等为 4，一方包含另一方（覆盖 "(Live)" 等后缀差异）为 2；
    /// 候选带了 SMTC 歌名里没有的版本标签（Live/现场/伴奏/Inst/纯音乐/DJ/Remix/翻唱/Cover/Karaoke/
    /// Acoustic/弾き語り/Piano/加速降速等，完整列表见 VersionTagRegex）再减 1。
    /// 歌名是防错配的第一道闸：只按歌手+时长挑候选，会把同歌手、时长接近的另一首歌的歌词抓来。
    ///
    /// 标签只在「同一档」里往后挪（4→3、2→1），不跨档：完全相等的永远排在包含的前面，
    /// 跟原来「0/1/2」三档的先后一致，择优时「歌名分数优先、再比译文覆盖率」的逻辑不受影响。
    /// 原来「告白气球 (Live)」和「告白气球 (电影插曲)」同为 1 分，谁先谁后全看搜索排序，
    /// 现在前者一定在后。SMTC 歌名自己带着同类标签就不罚——用户听的本来就是 Live 版。
    /// versionHint 是只参与标签判断的附加文本（网易云的 alias：有的 Live 版歌名干干净净，
    /// 标签写在别名里），不参与歌名相等/包含的比较。</summary>
    private static int TitleScore(string candidate, string title, string? versionHint = null)
    {
        var a = NormalizeForMatch(candidate);
        var b = NormalizeForMatch(title);
        if (a.Length == 0 || b.Length == 0) return 0;
        var score = a == b ? TitleScoreMax : a.Contains(b) || b.Contains(a) ? 2 : 0;
        if (score == 0) return 0;
        var titleTags = VersionTagsOf(title);
        var candTags = VersionTagsOf(versionHint == null ? candidate : candidate + " " + versionHint);
        return candTags.Any(t => !titleTags.Contains(t)) ? score - 1 : score;
    }

    /// <summary>TitleScore 的满分：归一化完全相等、且没有多出来的版本标签。</summary>
    private const int TitleScoreMax = 4;

    /// <summary>候选池收敛：只要有一个候选的歌手对得上，就只在这些候选里挑。
    ///
    /// 歌名相同而歌手不符，基本就是同名的另一首歌——网易云上叫「春夢」的条目有四首，
    /// 分属 May'n / 倒车入库 / 中川孝 / 初音ミク，拿错的那首冒充比不显示歌词更糟。
    /// 光靠排序不够：排前面的候选可能因为没歌词被跳过，兜底就落到歌手不符的那首上。
    /// 一个都对不上时（SMTC 的歌手写法与曲库不一致）才放开，按歌名分数照原顺序试。
    /// 放开挑出来的结果要标成 ArtistMismatch：它也可能只是同名翻唱，FetchAsync 会先问完别的源。</summary>
    private static List<T> PreferArtistMatched<T>(List<T> scored, Func<T, bool> artistMatched)
        => scored.Any(artistMatched) ? scored.Where(artistMatched).ToList() : scored;

    // 多歌手分隔符：各家写法不一（SMTC 常是 "A/B"，酷狗 "A、B"，还有逗号、分号、feat.）。
    // 故意不拆 "&"、"x"、"×"、"and"、"with"：它们常是组合名本身的一部分
    // （"Simon & Garfunkel"、"Lil Nas X"），拆开反而把一个名字切成两个互不相干的碎片。
    // feat/ft 要求前有词边界、后跟空白，免得切到 "Daft"、"Swift" 这种词中间
    [GeneratedRegex(@"[/／、,，;；|｜]|\b(?:feat|ft)\.?\s+|\bfeaturing\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistSeparatorRegex();

    /// <summary>把歌手串拆成单个歌手，并逐个做匹配用归一化（繁简、全角、大小写、去标点）。</summary>
    private static List<string> SplitArtists(string s)
        => ArtistSeparatorRegex().Split(s)
            .Select(NormalizeForMatch)
            .Where(x => x.Length > 0)
            .ToList();

    /// <summary>歌手宽松匹配，三家曲库共用：SMTC 的歌手串与候选的任一歌手名，
    /// 拆成单个歌手、归一化之后，只要有一对相等或一方包含另一方就算对上。
    ///
    /// 原先三处各写各的，都拿原始字符串做 OrdinalIgnoreCase 的相等/包含：
    /// 「周杰倫」对不上「周杰伦」、"A/B" 对不上 QQ 拼出来的 "B A"、酷狗的 "A、B"，
    /// 歌手闸一失灵，PreferArtistMatched 就放开到同名翻唱里去挑（抓错版本的根因之一）。
    /// 包含判断保留原来的宽松度（SMTC 常带变体名，如 "Jay Chou 周杰倫"），
    /// 但放在拆开之后逐个比，不会跨分隔符把两个歌手名拼成的串拿去误中第三个名字。
    /// 任一侧拆完是空的（空串、纯标点）一律不算匹配：空串是任何串的子串。</summary>
    private static bool ArtistMatches(IEnumerable<string> candidateNames, string artist)
    {
        var want = SplitArtists(artist);
        if (want.Count == 0) return false;
        foreach (var name in candidateNames)
            foreach (var p in SplitArtists(name))
                foreach (var w in want)
                    if (p == w || p.Contains(w) || w.Contains(p)) return true;
        return false;
    }

    // 版本标签：翻唱、现场、伴奏、remix 这类「同名不同版」。英文词两侧要求不是字母，
    // 免得 "Alive" 里的 live、"Instant" 里的 inst 被当成标签；繁简两种写法都列上，
    // 因为这里对的是原始歌名（归一化会去掉空白标点，英文词边界就没了）。
    // 不笼统地罚一切「某某 ver.」「某某版」（"Full ver."、「完整版」常常正是要找的那个），
    // 只列重新编曲、重新录过或变速的：它们的时间轴跟原曲对不上。
    // 实测「晩餐歌 / tuki.」：网易云按搜索顺序排在前面的是「晩餐歌(acoustic ver.)」和
    // 「晩餐歌 (弾き語りver)」，跟录音室版「晩餐歌 - Bansanka」同为包含档，原先挑中的是 acoustic 版
    [GeneratedRegex(@"(?<![a-z])(?:live|inst(?:rumental)?|dj|remix(?:ed)?|cover|karaoke"
        + @"|acoustic|unplugged|piano|sped\s*up|speed\s*up|nightcore|slowed)(?![a-z])"
        + @"|现场|現場|伴奏|纯音乐|純音樂|翻唱|弾き語り|弹唱|彈唱|アコースティック|ピアノ|钢琴版|鋼琴版"
        + @"|加速版|降速版|慢速版",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionTagRegex();

    /// <summary>歌名里出现的版本标签，同类写法归成一个：SMTC 写「现场」、候选写 "Live" 算同一种。</summary>
    private static HashSet<string> VersionTagsOf(string s)
    {
        var tags = new HashSet<string>();
        foreach (Match m in VersionTagRegex().Matches(s))
            tags.Add(m.Value.ToLowerInvariant() switch
            {
                "现场" or "現場" => "live",
                "instrumental" or "伴奏" or "纯音乐" or "純音樂" or "karaoke" => "inst",
                "remixed" => "remix",
                "翻唱" => "cover",
                "unplugged" or "弾き語り" or "弹唱" or "彈唱" or "アコースティック" => "acoustic",
                "ピアノ" or "钢琴版" or "鋼琴版" => "piano",
                "nightcore" or "加速版" => "fast",
                "slowed" or "降速版" or "慢速版" => "slow",
                // sped up / speed up 中间的空白写法不一，按前缀归
                var v when v.StartsWith("sped") || v.StartsWith("speed") => "fast",
                var v => v,
            });
        return tags;
    }

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
    private const double MinMergedSim = 0.8;  // 一对多/多对一合并配对的下限（拼回来该几乎逐字相同）
    private const int MaxLineShiftMs = 3000;  // 扣除局部偏移后仍允许的行首时间差（见 LocalOffsets）
    private const int MaxAnchorShiftMs = 15000; // 锚点偏离全局偏移的上限，即能跟上的最大漂移
    // 一个主歌词行最多认领几个连续的 KRC 行。原先只算两行，而酷狗对「A（A）」这种
    // 括注重复句常拆到四行（实测 KICK BACK「ハッピー ラッキー こんにちはベイビー
    // (ハッピー ラッキー こんにちはベイビー)」拆成 4 行）：单行比整句连 MinTextSim
    // 都摸不到（上限 2×9/47 = 0.38），两行拼起来也够不到 MinMergedSim，于是整句
    // 一行都配不上——原文被切成四行显示，译文一条都挂不上。
    // 封在 4 是因为再往上就得靠 MinMergedSim 独自兜着了，而拼进来的行越多，
    // 「凑巧凑够相似度」的风险越大
    private const int MaxKrcSpan = 4;
    private const int MinResegmentLen = 4; // 归一化后不足这么多字符的主行，按主歌词重切时只认原样出现（见 BuildByMainLines）

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
    /// <param name="floor">低于这个数就不必算出准确值，直接返回 0（省掉 O(nm) 的 DP）。
    /// 需要拿两个都够不到门槛的分数比大小时传 0——那时返回 0 会让两边都成 0、比不出来。</param>
    private static double Similarity(string a, string b, double floor = MinTextSim)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        // 相似度上限就是 2×短的/总长，够不到门槛就不必跑 O(nm) 的 DP
        if (2.0 * Math.Min(a.Length, b.Length) / (a.Length + b.Length) < floor) return 0.0;
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

    /// <summary>行级对齐结果：配对（主歌词行下标, KRC 行下标）按时间正序；
    /// 两个曲库之间的系统性时间差（KRC 行首减主歌词行首的中位数，毫秒），换算显示时间用；
    /// 以及逐个主歌词行的局部时间差（见 LocalOffsets），判断两行时间上对不对得上用。</summary>
    private sealed record Alignment(List<(int Main, int Krc)> Pairs, int OffsetMs, int[]? LocalMs = null)
    {
        /// <summary>主歌词第 i 行处的时间差：有逐行估计就用逐行的，没有（纯时间对齐）退回全局。</summary>
        public int ShiftAt(int i) => LocalMs?[i] ?? OffsetMs;
    }

    /// <summary>逐个主歌词行估计「KRC 行首减主歌词行首」的局部时间差。
    ///
    /// 两个曲库对同一首歌的时间差并不恒定，会逐段漂移：实测「アンノウン・マザーグース」
    /// 网易云那份与 KRC 前半首只差 0.5s 上下，第二段主歌起一路差到 4~5s（其余几家曲库在
    /// 那几句上都与 KRC 一致，多半是那份投稿后半首打轴拖了拍）。全局偏移配 ±MaxLineShiftMs
    /// 的容差把后半首文本几乎一字不差的三十多行全拦在门外，主歌词 88 行都带译文，最后只挂上 48 行。
    /// 容差不能直接放宽：它防的就是副歌重复句配到隔壁那一遍上去。
    ///
    /// 做法：文本完全相等的行对当锚点，取两侧下标都严格递增的最长锚点链——单调性把
    /// 重复句配到别处那一遍的锚点排掉（同 AlignLines 的 DP）——链上相邻锚点之间按时间
    /// 线性插值。容差改为相对「这一段的偏移」，漂移多少都跟得上，重复句照旧拦得住。</summary>
    private static int[] LocalOffsets(List<LyricLine> mainLines, List<KrcLine> krcLines,
        List<string> normMain, List<string> normKrc, int globalMs)
    {
        var n = mainLines.Count;
        var local = new int[n];
        Array.Fill(local, globalMs);
        // 按 (主行, KRC 行) 的字典序收集，下面的链 DP 依赖这个顺序
        var anchors = new List<(int I, int J, int D)>();
        for (var i = 0; i < n; i++)
        {
            if (normMain[i].Length == 0) continue;
            for (var j = 0; j < krcLines.Count; j++)
            {
                if (normKrc[j] != normMain[i]) continue;
                var d = krcLines[j].StartMs - mainLines[i].Ms;
                if (Math.Abs(d - globalMs) <= MaxAnchorShiftMs) anchors.Add((i, j, d));
            }
        }
        if (anchors.Count == 0) return local;

        // 最长单调链：len[k] 为以锚点 k 结尾的最长链。同样长时取偏移跳变总量小的那条：
        // 副歌整段重复时「错开一遍」的链可能与正确的链一样长，但它的偏移会跳一整遍的时长
        var len = new int[anchors.Count];
        var jump = new long[anchors.Count];
        var prev = new int[anchors.Count];
        var end = 0;
        for (var k = 0; k < anchors.Count; k++)
        {
            (len[k], jump[k], prev[k]) = (1, 0, -1);
            for (var p = 0; p < k; p++)
            {
                if (anchors[p].I >= anchors[k].I || anchors[p].J >= anchors[k].J) continue;
                var l = len[p] + 1;
                var jp = jump[p] + Math.Abs(anchors[k].D - anchors[p].D);
                if (l > len[k] || (l == len[k] && jp < jump[k])) (len[k], jump[k], prev[k]) = (l, jp, p);
            }
            if (len[k] > len[end] || (len[k] == len[end] && jump[k] < jump[end])) end = k;
        }
        var chain = new List<(int I, int D)>();
        for (var k = end; k >= 0; k = prev[k]) chain.Add((anchors[k].I, anchors[k].D));
        chain.Reverse();
        // 三点中值滤掉孤立的错锚（「啊」「la」这种短句最容易配到别处），台阶式的真漂移不受影响
        var ds = chain.Select(c => c.D).ToArray();
        for (var k = 1; k < chain.Count - 1; k++)
        {
            int a = ds[k - 1], b = ds[k], c = ds[k + 1];
            chain[k] = (chain[k].I, Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c)));
        }

        // 链头之前、链尾之后沿用端点的偏移，中间按主歌词时间线性插值
        var t = 0;
        for (var i = 0; i < n; i++)
        {
            while (t + 1 < chain.Count && chain[t + 1].I <= i) t++;
            var (ia, da) = chain[t];
            if (i <= ia || t + 1 == chain.Count) { local[i] = da; continue; }
            var (ib, db) = chain[t + 1];
            var span = mainLines[ib].Ms - mainLines[ia].Ms;
            local[i] = span <= 0 ? da
                : da + (int)((long)(db - da) * (mainLines[i].Ms - mainLines[ia].Ms) / span);
        }
        return local;
    }

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
    /// 解决重复行抢占；文本改用相似度而非相等；时间容差相对逐行估计的局部偏移算
    /// （见 LocalOffsets），只用来拦「配到另一遍重复句 / 整体错配到另一首歌」。</summary>
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
        var local = LocalOffsets(mainLines, krcLines, normMain, normKrc, offset);

        // 配对得分矩阵（0 = 不允许配对）。除 1:1 外还算「一个主行 ↔ 相邻 k 个 KRC 行」
        // （k 到 MaxKrcSpan）与「相邻两个主行 ↔ 一个 KRC 行」：两个曲库对同一首歌的断句
        // 粒度常不同——KRC 按「唱的断句」把一句拆成好几行，或反过来把两句并成一行。
        // 严格 1:1 时这些行整片落空，且相似度还会双双跌破阈值（Lemon 首句：网易云一行
        // 16 字、KRC 拆成 4+12 两行，单看任一半的相似度只有 0.4）。
        // mi 是这组配对里打头的主歌词行，时间差按它那一处的局部偏移算
        double Score(string a, int mi, string b, int bMs, double min)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (Math.Abs(bMs - local[mi] - mainLines[mi].Ms) > MaxLineShiftMs) return 0;
            var s = Similarity(a, b);
            return s >= min ? s : 0;
        }

        var sim = new double[n, m];    // main[i] ↔ krc[j]
        // main[i] ↔ krc[j-k+1..j]（往前接 k 行，k 从 2 起，[.., 0] 与 [.., 1] 不用）
        var simSpan = new double[n, m, MaxKrcSpan + 1];
        var sim2x1 = new double[n, m]; // main[i-1] + main[i] ↔ krc[j]
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                sim[i, j] = Score(normMain[i], i,
                    normKrc[j], krcLines[j].StartMs, MinTextSim);
                // 合并只认高相似度：拆行拼回来本该几乎逐字相同，阈值松了会把
                // 一句歌词旁边那行无关的短句也一起吞进来
                var cat = normKrc[j];
                for (var k = 2; k <= MaxKrcSpan && j - k + 1 >= 0; k++)
                {
                    cat = normKrc[j - k + 1] + cat;
                    // 拼过头就不必再往前接：相似度上限是 2×短的/总长，拼接串一旦超过
                    // 主行长度的 1.5 倍这个上限就跌破 0.8，而 cat 只会越接越长
                    if (cat.Length * 2 > normMain[i].Length * 3) break;
                    simSpan[i, j, k] = Score(normMain[i], i,
                        cat, krcLines[j - k + 1].StartMs, MinMergedSim);
                }
                if (i > 0)
                    sim2x1[i, j] = Score(normMain[i - 1] + normMain[i], i - 1,
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
                // 合并配对按「吃掉 k 行」记 k 份分：这样当那些多出来的行另有 1:1 的好归宿时，
                // 「各配各的」总分更高，DP 会选它，合并只在那些行本来无处可去时才发生
                for (var k = 2; k <= MaxKrcSpan && j - k >= 0; k++)
                    if (simSpan[i - 1, j - 1, k] > 0)
                        best = Math.Max(best, dp[i - 1, j - k] + simSpan[i - 1, j - 1, k] * k);
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
            var s21 = ii > 1 ? sim2x1[ii - 1, jj - 1] : 0;
            // 走的是「这个主行吃掉 k 个 KRC 行」的哪个 k（0 = 不走这条）。多个 k 同时与
            // 最优值相等时取最小的那个：总分一样，而吞掉的行少，猜错的余地也小
            var span = 0;
            for (var k = 2; k <= MaxKrcSpan && jj - k >= 0; k++)
            {
                var sk = simSpan[ii - 1, jj - 1, k];
                if (sk > 0 && dp[ii, jj] <= dp[ii - 1, jj - k] + sk * k + 1e-9)
                {
                    span = k;
                    break;
                }
            }
            if (s > 0 && dp[ii, jj] <= dp[ii - 1, jj - 1] + s + 1e-9)
            {
                pairs.Add((ii - 1, jj - 1));
                ii--;
                jj--;
            }
            else if (span > 0)
            {
                // 按 KRC 下标递减加入，与 1:1 分支同向；最后整体 Reverse 成时间正序
                for (var t = 0; t < span; t++) pairs.Add((ii - 1, jj - 1 - t));
                ii--;
                jj -= span;
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
        return new Alignment(pairs, offset, local);
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
            // KRC 那行的字时间是相对它自己的行首的，挂到主歌词行上要补两行行首之差。
            // 扣的是这一处的局部偏移：两侧打轴漂开几秒的段落里扣全局偏移，整行的字会
            // 跟着漂几秒，扫过条要么提前走完、要么迟迟不动
            var shift = krcLines[j].StartMs - al.ShiftAt(i) - mainLines[i].Ms;
            var words = ShiftWords(krcLines[j].Words, shift);
            // 一对多（KRC 把这一句拆成好几行唱）：那几行的字表接起来正好覆盖主行全文。
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

        // 向后吸附：把「一句拆成两行、只认出后半」的那前半找回来。
        //
        // KRC 常把主歌词的一句拆成好几行唱，而两侧对同一个词的写法可能不同
        // （实测 Superfly「Bi-Li-Li Emotion」：KRC 写「諸行無常ね ジーザス」，
        // 主歌词写「諸行無常ね、Jesus! 全てはフェイドアウト」——片假名对拉丁字母）。
        // 于是只有后半句文本对得上，前半句一行配不到任何主行，krcsOf 里那句就只有
        // 一个 KRC 行、凑不满合并的门槛，下面的合并逻辑不接管，这行就空着译文，
        // 显示上退化成「第二行显示下一句」——主人看到的正是这一幕。
        //
        // 判据不能再靠相似度门槛（它已经失手了），改用时间加一道相对文本闸：主行的
        // 行首时间几乎就等于这一行的行首、且明显比下一行更近，说明这句主歌词从这一行
        // 就开始唱了；再要求「并起来比原先更像主歌词那句」挡住误吸（见下面的文本闸）。
        for (var j = 0; j < krcLines.Count - 1; j++)
        {
            if (mainOf.ContainsKey(j)) continue;                 // 本来就配上了
            if (!mainOf.TryGetValue(j + 1, out var next) || next.Count != 1) continue;
            var i = next[0];
            if (krcsOf[i].Count != 1 || string.IsNullOrEmpty(mainLines[i].Trans)) continue;
            var mainMs = mainLines[i].Ms + al.ShiftAt(i);        // 换到 KRC 时间轴（按局部偏移）
            var dj = Math.Abs(mainMs - krcLines[j].StartMs);
            if (dj > 1500 || dj >= Math.Abs(mainMs - krcLines[j + 1].StartMs)) continue;
            // 文本闸：吸附等于把这两行并成一行显示，那么拼起来必须比原先那一行更像
            // 主歌词这一句。光靠时间会误吸——实测 KICK BACK 有一句主歌词被 KRC 拆成
            // 四行（MaxKrcSpan 之前四行全落空），其中末行到隔壁那句主歌词的行首只差
            // 10ms，比到它自己那句的 320ms 还近，上面三道闸全过，结果把两句毫不相干
            // 的歌词粘成一行、逐字字表也跟着串。
            // 比大小得用真实相似度（floor 传 0）：吸附本就是给「文本相似度失手」兜底的，
            // 两边都够不到 MinTextSim 是常态，按门槛算会双双归零、比不出高下
            var mainNorm = NormalizeForMatch(mainLines[i].Text);
            var alone = Similarity(mainNorm, NormalizeForMatch(krcLines[j + 1].Plain), 0);
            var joined = Similarity(mainNorm,
                NormalizeForMatch(krcLines[j].Plain) + NormalizeForMatch(krcLines[j + 1].Plain), 0);
            if (joined <= alone) continue;
            krcsOf[i].Add(j);
            mainOf[j] = new List<int> { i };
            // 译文按 KRC 行索引存，而吸附进来的这行排在前面、会成为合并后的组首
            // （显示时只取组首那行的译文）——不一起挂上去，合并完反倒一行译文都没有
            transOf[j] = mainLines[i].Trans!;
        }

        // 一对多（KRC 把主歌词的一句拆成好几行唱）且这句有译文时，把那几行并回一行显示。
        // 不并的话每行都挂着同一条译文，同一句翻译连着出现好几遍，看着像卡带重复了；
        // 而按字数把译文切成几段纯属猜。并回去不损失逐字——字表接起来正好覆盖这一整句。
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
            // 直接用 KRC 的绝对时间会整首歌偏一个两版之差。
            // 这里刻意扣全局偏移而不是局部偏移：局部漂移实测多出在 LRC 投稿那侧（后半首
            // 打轴拖拍，其余曲库都与 KRC 一致），按局部扣等于把 KRC 精确的时间轴改成拖拍的那份
            var ms = Math.Max(0, krcLines[j].StartMs - al.OffsetMs);
            if (karaoke.ContainsKey(ms)) continue; // 撞到同一毫秒（如 offset 把开头几行都压到 0）
            var text = krcLines[j].Plain;
            var words = krcLines[j].Words; // 字时间已相对本行行首，不需要平移
            if (groupOf.TryGetValue(j, out var group))
            {
                words = new List<KaraokeWord>();
                foreach (var g in group)
                {
                    // 并进来的行，字时间要从「相对自己行首」改成「相对组首行的行首」
                    var add = ShiftWords(krcLines[g].Words, krcLines[g].StartMs - krcLines[j].StartMs);
                    // 西文两行直接相接会把词粘死（实测 fish in the pool 显示成「on my toeslet me」），
                    // 补的空格挂在前一行末字上：显示文本由字表拼出，这样两边仍严格同源
                    if (words.Count > 0 && add.Count > 0 && NeedsSpace(words[^1].Text, add[0].Text))
                        words[^1] = words[^1] with { Text = words[^1].Text + " " };
                    words.AddRange(add);
                }
                // 文本按 ParseKrc 的同一公式重算：高亮边界是逐字累加字宽算出来的，
                // 显示文本必须与字表严格同源，拿 Plain 直接相接会因它已 Trim 过而错位
                text = string.Concat(words.Select(w => w.Text)).Trim();
            }
            lines.Add(new LyricLine(ms, text, transOf.TryGetValue(j, out var tr) ? tr : null));
            karaoke[ms] = words;
        }
        return (lines, karaoke);
    }

    /// <summary>方案 C（主歌词带译文时的默认路，闸门见 FetchAsync）：文本与逐字照旧取自 KRC，
    /// 断句改跟主歌词走——把 KRC 全曲的字摊平成一条流，按主歌词每一行占了其中哪一段重新切行，
    /// 译文原样挂在自己那一句上。
    ///
    /// 为什么不沿用 BuildFromKrc 的「按行配对再挂译文」：两个曲库断句的位置常常是错开的，
    /// 而不只是粗细不同。实测 ヘクとパスカル「fish in the pool」开头三句：
    ///     主歌词  Let me hear~ ｜ the sound of your heartbeat on my toes. ｜ Let me touch my ear on your chest.
    ///     KRC     Let me hear the sound of your heartbeat ｜ on my toes ｜ let me touch my ear on your chest
    /// 行与行之间不是一对一、一对多、多对一里的任何一种，按行挂译文只能挂歪：第一行才唱到
    /// heartbeat，译文已经说到「我踮起脚尖」；on my toes 又被并到下一句头上。
    /// 「アンノウン・マザーグース」里这样的错位有五六处，还有一句主歌词被挤得一行都配不上、
    /// 译文直接丢了。主人反馈的「歌词和译文的单句不是严格对应的」就是这一类。
    ///
    /// 主歌词那侧原文与译文的逐行对应是严格的（同一份投稿、同一组时间戳），所以只要照主歌词的
    /// 断句切，对应关系就歪不了；逐字时间戳是跟着字走的，切在哪儿都不损失。BuildFromKrc 里的
    /// 向后吸附、一对多合并、夹逼填空，在这里都只是「这一行占了哪一段」的自然结果。
    ///
    /// 做法：两侧归一化后的全曲字符流做一次最长公共子序列对齐，主歌词每行只许配到时间上
    /// 够得着的那一段 KRC（窗口同 AlignLines，按局部偏移算）；得到每行在 KRC 字流里的起止后，
    /// 没被任何主行认领的字按 KRC 自己的行归并。返回 null 表示这条路走不通，调用方退回 BuildFromKrc。</summary>
    private static (List<LyricLine> Lines, Dictionary<int, List<KaraokeWord>> Karaoke)? BuildByMainLines(
        List<LyricLine> mainLines, List<KrcLine> krcLines, Alignment al)
    {
        // KRC 摊平成字流：每个字记所属行与绝对时间；kWord 把归一化后的字符映回它所在的字
        // （西文一个「字」是一个单词，归一化后占好几个字符；标点和空白归一化后不占字符）
        var words = new List<KaraokeWord>();
        var wLine = new List<int>();
        var wAbs = new List<int>();
        var wFirstQ = new List<int>(); // 字的第一个归一化字符在字流里的下标（-1 = 归一化后是空的）
        var kChar = new List<char>();
        var kWord = new List<int>();
        for (var j = 0; j < krcLines.Count; j++)
        {
            foreach (var w in krcLines[j].Words)
            {
                var norm = NormalizeForMatch(w.Text);
                wFirstQ.Add(norm.Length > 0 ? kChar.Count : -1);
                foreach (var c in norm)
                {
                    kChar.Add(c);
                    kWord.Add(words.Count);
                }
                words.Add(w);
                wLine.Add(j);
                wAbs.Add(krcLines[j].StartMs + w.OffsetMs);
            }
        }
        var n = mainLines.Count;
        var wn = words.Count;
        var normMain = mainLines.Select(l => NormalizeForMatch(l.Text)).ToList();
        var mChar = new List<char>();
        var mLine = new List<int>();
        var mSep = new List<bool?>(); // 主歌词这个字符在原文里跟前一个字符之间隔没隔着空白/标点（null = 不知道）
        for (var i = 0; i < n; i++)
        {
            var seps = SeparatedBefore(mainLines[i].Text);
            // 繁简转换按理逐字一一对应，万一长度对不上就不用这份标记
            var sepOk = seps.Count == normMain[i].Length;
            for (var k = 0; k < normMain[i].Length; k++)
            {
                mChar.Add(normMain[i][k]);
                mLine.Add(i);
                mSep.Add(sepOk && k > 0 ? seps[k] : null);
            }
        }
        if (mChar.Count == 0 || kChar.Count == 0) return null;

        // 每个主行在 KRC 字符流上够得着的区间 [lo, hi]（从 1 起数）：自己行首前 MaxLineShiftMs
        // 到下一行行首后 MaxLineShiftMs，时间先按局部偏移换到 KRC 那一侧。作用同 AlignLines 的
        // 时间闸——不拦的话副歌重复句会配到隔壁那一遍去。
        // 区间逐行单调不减，下面的带状 DP 靠这一点只存带内的格子
        var kTime = new int[kChar.Count];
        for (var q = 0; q < kTime.Length; q++)
            kTime[q] = q == 0 ? wAbs[kWord[q]] : Math.Max(kTime[q - 1], wAbs[kWord[q]]);
        int CountBelow(int t) // kTime 里小于 t 的个数（kTime 已单调）
        {
            var (l, r) = (0, kTime.Length);
            while (l < r)
            {
                var mid = (l + r) / 2;
                if (kTime[mid] < t) l = mid + 1;
                else r = mid;
            }
            return l;
        }
        var bandLo = new int[n];
        var bandHi = new int[n];
        long cells = 0;
        for (var i = 0; i < n; i++)
        {
            var s = mainLines[i].Ms + al.ShiftAt(i);
            var e = i + 1 < n ? Math.Max(s, mainLines[i + 1].Ms + al.ShiftAt(i + 1)) : int.MaxValue / 2;
            var lo = CountBelow(s - MaxLineShiftMs) + 1;
            var hi = CountBelow(e + MaxLineShiftMs + 1);
            if (i > 0) (lo, hi) = (Math.Max(lo, bandLo[i - 1]), Math.Max(hi, bandHi[i - 1]));
            (bandLo[i], bandHi[i]) = (lo, Math.Max(hi, lo - 1));
            cells += (long)normMain[i].Length * (bandHi[i] - lo + 2);
        }
        // 极端长的歌、或时间窗被拉得极宽：不值得为一首歌的歌词临时占几十 MB
        if (cells > 4_000_000) return null;

        // 带状最长公共子序列：dp[p][q] = 主歌词前 p 个字符与 KRC 前 q 个字符最多能配上几个，
        // 第 p 行只存 q ∈ [lo-1, hi] 这一段。带外的值不必存：带左侧等于上一行同列，
        // 带右侧等于本行带内最后一格（hi 之后这一行再没有可配的字符）
        var pn = mChar.Count;
        var rowLo = new int[pn + 1];
        var rows = new int[pn + 1][];
        rowLo[0] = 1;
        rows[0] = new int[1];
        int Get(int p, int q)
        {
            var r = rows[p];
            var k = q - rowLo[p] + 1;
            return r[k < r.Length ? k : r.Length - 1];
        }
        for (var p = 1; p <= pn; p++)
        {
            var (lo, hi) = (bandLo[mLine[p - 1]], bandHi[mLine[p - 1]]);
            var r = new int[hi - lo + 2];
            r[0] = Get(p - 1, lo - 1);
            var c = mChar[p - 1];
            for (var q = lo; q <= hi; q++)
            {
                var best = Math.Max(Get(p - 1, q), r[q - lo]);
                if (c == kChar[q - 1]) best = Math.Max(best, Get(p - 1, q - 1) + 1);
                r[q - lo + 1] = best;
            }
            rowLo[p] = lo;
            rows[p] = r;
        }

        // 回溯：matchK[p] = 主歌词第 p 个字符配到的 KRC 字符下标（-1 = 没配上）。
        // 能走对角就走对角，等价于「同样多的配法里尽量往后配」
        var matchK = new int[pn];
        Array.Fill(matchK, -1);
        for (int p = pn, q = rowLo[pn] + rows[pn].Length - 2; p > 0;)
        {
            var lo = rowLo[p];
            if (q >= lo && mChar[p - 1] == kChar[q - 1] && rows[p][q - lo + 1] == Get(p - 1, q - 1) + 1)
            {
                matchK[p - 1] = q - 1;
                q--;
            }
            else if (q >= lo && rows[p][q - lo + 1] == rows[p][q - lo])
            {
                q--;
                continue;
            }
            p--;
            // 换到上一行：q 超出那一行带的右端就夹回去（带右侧的值都等于带内最后一格）
            q = Math.Min(q, rowLo[p] + rows[p].Length - 2);
        }

        // 每个主行认领它配上的那一段字。owner[w] = 第 w 个字归哪个主行（-1 = 没人认领）
        var owner = new int[wn];
        Array.Fill(owner, -1);
        var ownCnt = new int[wn];
        var curLine = new int[wn];
        Array.Fill(curLine, -1);
        var curCnt = new int[wn];
        var qs = new List<int>();
        for (int i = 0, p = 0; i < n; i++)
        {
            var len = normMain[i].Length;
            qs.Clear();
            for (var k = 0; k < len; k++, p++)
                if (matchK[p] >= 0) qs.Add(matchK[p]);
            if (qs.Count == 0) continue;
            // 只留最密的一段：最长公共子序列只管配得多，零星的字会被它配到窗口里老远的
            // 同一个字上（假名、虚词到处都是），照单全收的话这一行会把中间不相干的字全吞进来。
            // 密度就用 Similarity 的同一公式：2×配上的 / (主行长 + 这一段 KRC 的长)
            var (ba, bb, sim) = (0, 0, 0.0);
            for (var a = 0; a < qs.Count; a++)
            {
                for (var b = qs.Count - 1; b >= a; b--)
                {
                    var s = 2.0 * (b - a + 1) / (len + qs[b] - qs[a] + 1);
                    if (s > sim) (ba, bb, sim) = (a, b, s);
                }
            }
            if (sim < MinTextSim) continue;
            // 极短的行（「ねえ」「Oh」）只认原样整段出现：两三个字符凑够相似度太容易了，
            // 从别的句子里抠出一个「あ」当成这一行，会把那句歌词切掉一个字
            if (len < MinResegmentLen && (bb - ba + 1 != len || qs[bb] - qs[ba] + 1 != len)) continue;
            for (var k = ba; k <= bb; k++)
            {
                // 一个字被前后两个主行各配上一部分（主行的断句落在一个西文单词中间）：归配得多的那行
                var w = kWord[qs[k]];
                if (curLine[w] != i) (curLine[w], curCnt[w]) = (i, 0);
                if (++curCnt[w] > ownCnt[w]) (owner[w], ownCnt[w]) = (i, curCnt[w]);
            }
        }
        // 一行认领的首尾两个字之间全归它：夹在中间没配上的是和声、语气词、两侧写法不同的字。
        // 对齐是单调的，别的主行不可能落在这中间
        for (var w = 0; w < wn;)
        {
            var i = owner[w];
            if (i < 0)
            {
                w++;
                continue;
            }
            var last = w;
            for (var v = w + 1; v < wn && (owner[v] < 0 || owner[v] == i); v++)
                if (owner[v] == i) last = v;
            for (var v = w; v <= last; v++) owner[v] = i;
            w = last + 1;
        }

        // 没人认领的字，按 KRC 自己的行处理：
        //   · 所在的 KRC 行另一头已归了某个主行（那句多唱了几个字、或两侧写法不同没配上）→ 跟着那个主行；
        //   · 整个 KRC 行都没人认领（主歌词没有这一句）→ 自己成一行。
        var kTrans = new Dictionary<int, string>(); // 自己成行的 KRC 行 -> 夹逼补上的译文
        for (var a = 0; a < wn;)
        {
            if (owner[a] >= 0)
            {
                a++;
                continue;
            }
            var b = a;
            while (b + 1 < wn && owner[b + 1] < 0) b++;
            var x = a > 0 ? owner[a - 1] : -1;      // 这段空隙前面的主行
            var y = b + 1 < wn ? owner[b + 1] : -1; // 后面的主行
            var alone = new List<int>();
            for (var sa = a; sa <= b;)
            {
                var sb = sa;
                while (sb < b && wLine[sb + 1] == wLine[sa]) sb++;
                var sharesPrev = sa > 0 && wLine[sa - 1] == wLine[sa];
                var sharesNext = sb + 1 < wn && wLine[sb + 1] == wLine[sb];
                if (sharesPrev && sharesNext)
                {
                    // 同一个 KRC 行里夹在两句主歌词之间：恰有一处空白就在那儿切开，否则都跟前一句
                    var (cut, cands) = (sb + 1, 0);
                    for (var c = sa; c <= sb + 1; c++)
                    {
                        if (!words[c - 1].Text.EndsWith(' ') && !words[c - 1].Text.EndsWith('　')
                            && !words[c].Text.StartsWith(' ') && !words[c].Text.StartsWith('　')) continue;
                        cut = c;
                        cands++;
                    }
                    if (cands != 1) cut = sb + 1;
                    for (var w = sa; w <= sb; w++) owner[w] = w < cut ? x : y;
                }
                else if (sharesPrev || sharesNext)
                {
                    for (var w = sa; w <= sb; w++) owner[w] = sharesPrev ? x : y;
                }
                else
                {
                    alone.Add(wLine[sa]);
                }
                sa = sb + 1;
            }
            // 夹逼填空（判据与理由同 BuildFromKrc）：前后两个主行之间正好漏了一句主歌词，
            // 空隙里也正好只有一个没人认领的 KRC 行，那它们只能是同一句——文本没对上是用词差太多
            if (alone.Count == 1 && x >= 0 && y - x == 2 && !string.IsNullOrEmpty(mainLines[x + 1].Trans))
                kTrans[alone[0]] = mainLines[x + 1].Trans!;
            a = b + 1;
        }

        // 按归属切成显示行。没译文的主行不并：KRC 的细断句本身更好读（理由同 BuildFromKrc），
        // 所以它那一段里再按 KRC 的行切开
        (int Main, int Krc) IdOf(int w) => owner[w] < 0 ? (-1, wLine[w])
            : string.IsNullOrEmpty(mainLines[owner[w]].Trans) ? (owner[w], wLine[w]) : (owner[w], -1);
        var kToMain = new int[kChar.Count];
        Array.Fill(kToMain, -1);
        for (var p = 0; p < pn; p++)
            if (matchK[p] >= 0) kToMain[matchK[p]] = p;
        // 两个 KRC 行并进同一显示行，接缝处补不补空格：照主歌词原文在这儿有没有断开。
        // 光看字符种类会把日文粘死——主歌词写「愛すりゃいいじゃん 泣けばいいじゃん」，
        // 酷狗拆成两行，直接相接就成了一整串；「feeling」接「私は」也一样。
        // 接缝后那个字没配上主歌词时才退回按字符种类猜
        bool JoinSpace(int w, int line, string left, string right)
        {
            if (left.Length == 0 || right.Length == 0 || char.IsWhiteSpace(left[^1]) || char.IsWhiteSpace(right[0]))
                return false;
            var p = wFirstQ[w] >= 0 ? kToMain[wFirstQ[w]] : -1;
            return p >= 0 && mLine[p] == line && mSep[p] is { } sep ? sep : NeedsSpace(left, right);
        }
        var lines = new List<LyricLine>();
        var karaoke = new Dictionary<int, List<KaraokeWord>>();
        var placed = 0;
        for (int a = 0, b; a < wn; a = b + 1)
        {
            var id = IdOf(a);
            b = a;
            while (b + 1 < wn && IdOf(b + 1) == id) b++;
            var first = a;
            while (first < b && string.IsNullOrWhiteSpace(words[first].Text)) first++;
            // 换算回主歌词那一侧的时间轴，刻意扣全局偏移而不是局部偏移（理由见 BuildFromKrc）
            var ms = Math.Max(0, wAbs[first] - al.OffsetMs);
            if (karaoke.ContainsKey(ms)) continue; // 撞到同一毫秒
            var list = new List<KaraokeWord>(b - first + 1);
            for (var w = first; w <= b; w++)
            {
                // 显示文本由字表拼出（高亮边界是逐字累加字宽算的，两边必须严格同源）：
                // 行首的空白直接从字上去掉；跨 KRC 行相接处要补的空格挂在前一个字上
                var text = w == first ? words[w].Text.TrimStart() : words[w].Text;
                if (w > first && wLine[w] != wLine[w - 1] && JoinSpace(w, id.Main, list[^1].Text, text))
                    list[^1] = list[^1] with { Text = list[^1].Text + " " };
                // 字时间改成相对这一显示行的行首
                list.Add(new KaraokeWord(Math.Max(0, wAbs[w] - wAbs[first]), words[w].DurationMs, text));
            }
            var lineText = string.Concat(list.Select(w => w.Text)).TrimEnd();
            if (lineText.Length == 0) continue;
            var trans = id.Main >= 0 ? mainLines[id.Main].Trans : kTrans.GetValueOrDefault(wLine[first]);
            if (string.IsNullOrEmpty(trans)) trans = null;
            else placed++;
            lines.Add(new LyricLine(ms, lineText, trans));
            karaoke[ms] = list;
        }

        // 兜底：挂上的译文明显少于按行配对能配上的句数，说明字符流对齐在这首歌上出了岔子
        var paired = al.Pairs.Select(p => p.Main).Distinct()
            .Count(i => !string.IsNullOrEmpty(mainLines[i].Trans));
        return placed < paired * 0.9 ? null : (lines, karaoke);
    }

    /// <summary>两段文本首尾相接处要不要补空格：只在两侧都不是中日韩文字时补
    /// （中日文本就不靠空格断词，「張り裂けて」接「叫ばせて」补了反倒难看）。</summary>
    private static bool NeedsSpace(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return false;
        char a = left[^1], b = right[0];
        if (char.IsWhiteSpace(a) || char.IsWhiteSpace(b)) return false;
        return a < '⺀' && b < '⺀'; // U+2E80 起是 CJK 部首、假名、汉字、谚文等
    }

    /// <summary>与 NormalizeForMatch 留下的字符逐个对应：这个字符跟前一个留下的字符之间，
    /// 原文里有没有被滤掉的空白或标点。第一个字符前面没有字符，恒为 false。</summary>
    private static List<bool> SeparatedBefore(string s)
    {
        var flags = new List<bool>(s.Length);
        var gap = false;
        foreach (var c in s)
        {
            var ch = c;
            if (ch is >= '\uFF01' and <= '\uFF5E') ch = (char)(ch - 0xFEE0); // 与 NormalizeForMatch 同一套取舍
            if (!char.IsLetterOrDigit(ch))
            {
                gap = true;
                continue;
            }
            flags.Add(flags.Count > 0 && gap);
            gap = false;
        }
        return flags;
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

    /// <summary>抓取结果：歌词行 + 逐字时间表 + 命中的源名 + 曲库登记的歌曲时长（秒，未知为 0）。
    ///
    /// Degraded：这份结果因为某一步「请求失败」（而不是「确实没有」）打了折扣——网易云首选
    /// 候选的歌词接口失败只好用了次选候选，或开了逐字但酷狗 KRC 请求失败。这种结果不写缓存，
    /// 否则一次网络抖动会把次优结果冻结 30 天。落到 QQ/LRCLIB 备选源不置位：那条路本来就不缓存。
    /// PrimaryNotFound：网易云正常应答、确实没有这首歌（不是限流/风控/网络异常）。调用方据此
    /// 不再为「等网易云恢复」而重试——即使备选源给了歌词，这个标志也照样带着。
    /// ArtistMismatch：各源都没有歌手对得上的版本，这是放开歌手闸退回来的备胎（可能是翻唱），
    /// 调用方给它排最低一档，重试拿到歌手对得上的任何结果都该顶掉它。
    /// 这几个字段只能追加在末尾且带默认值：缓存读取和调用方都还在用前四个参数的构造。</summary>
    public readonly record struct FetchResult(
        List<LyricLine>? Lines,
        Dictionary<int, List<KaraokeWord>> Karaoke,
        string Source,
        double SongDurationS,
        bool Degraded = false,
        bool PrimaryNotFound = false,
        bool ArtistMismatch = false);

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
        // 网易云正常应答说「没有这首歌」：哪怕后面备选源给了歌词也要带出去，
        // 调用方靠它判断还值不值得为「等网易云恢复」重试
        var primaryNotFound = false;
        // 歌手对不上的结果先存着不用（见循环里的注释），各源都问完了才轮到它
        SourceResult? fallback = null;
        var fallbackName = "";
        var neteaseMismatch = false;
        // 有源请求失败（不是「确实没有」）：退回备胎时据此判断那是不是最终答案
        var anyFailed = false;
        var sources = new (string Name, Func<Task<SourceResult?>> Fetch)[]
        {
            ("_fetch_netease", () => FetchNeteaseAsync(title, artist, durationS, secondLine)),
            ("_fetch_qq", () => FetchQqAsync(title, artist, durationS)),
            ("_fetch_lrclib", () => FetchLrclibAsync(title, artist, durationS)),
        };
        foreach (var (name, fetch) in sources)
        {
            try { found = await fetch(); }
            catch { found = null; anyFailed = true; } // 单个源网络异常不致命，换下一个
            // 网易云返回 null 也是请求失败（「确实没有」走的是 NotFound），QQ/LRCLIB 的 null 才是没有
            if (name == "_fetch_netease" && found == null) anyFailed = true;
            if (name == "_fetch_netease" && found is { NotFound: true }) primaryNotFound = true;
            if (found is not { Lines.Count: > 0 }) continue;
            if (!found.ArtistMismatch)
            {
                sourceName = name;
                break;
            }
            // 歌手对不上的先存成备胎，接着问下一个源：网易云缺版权的歌（「告白氣球」）
            // 搜出来全是同名翻唱，而 QQ / LRCLIB 手里就有原唱。
            // 几个备胎之间默认留先到的（网易云带译文），只有后来的时长明显更贴近 SMTC 才换：
            // SMTC 报「Jay Chou」而 LRCLIB 也没收这个写法时，各家都对不上「周杰伦」，
            // 这时靠时长认出 QQ 的 215s 原唱，而不是网易云那个 228s 的翻唱；
            // 差个零点几秒不算数，免得为此丢掉译文
            if (name == "_fetch_netease") neteaseMismatch = true;
            if (fallback == null || (durationS > 0 && Math.Abs(found.DurationS - durationS) + 5
                    < Math.Abs(fallback.DurationS - durationS)))
                (fallback, fallbackName) = (found, name);
            found = null;
        }
        // 各源都没有歌手对得上的：退回备胎。这是为 SMTC 歌手写法和曲库不一致（「Jay Chou」
        // 对「周杰伦」）留的退路，放在各源都问过之后，不再让某一家的翻唱抢在别家的原唱前面
        var usedFallback = found is not { Lines.Count: > 0 } && fallback != null;
        if (usedFallback) (found, sourceName) = (fallback, fallbackName);
        // 网易云只有歌手对不上的版本，等同于「确实没有这首歌」：再抓它也还是那几个翻唱，
        // 调用方不必为它重试。例外是退回备胎时有源请求失败了——重试还有指望从那个源拿到原唱
        if (neteaseMismatch && !(usedFallback && anyFailed)) primaryNotFound = true;
        if (found is not { Lines.Count: > 0 })
            return new FetchResult(null, new Dictionary<int, List<KaraokeWord>>(), "", 0,
                PrimaryNotFound: primaryNotFound);
        var lines = found.Lines;

        // 过滤制作信息行（作词/编曲/制作人等不是歌词，不该占用任务栏），判定见 CreditMask。
        // 滤掉超过六成就认定是正则误伤（正常歌曲的制作信息只占开头几行），整份留原样：
        // 宁可多显示几行制作信息，也不能把歌词本身滤成残缺的
        var credit = CreditMask(lines.Select(l => l.Text).ToList(),
            lines.Select(l => l.Trans).ToList(), title, artist);
        var filtered = lines.Where((_, i) => !credit[i]).ToList();
        if (filtered.Count > 0 && filtered.Count >= lines.Count * 0.4) lines = filtered;

        var karaoke = new Dictionary<int, List<KaraokeWord>>();
        // 退回备胎时有源请求失败：这份翻唱只是「这次没拿到原唱」，同样按降级处理、不进缓存
        var degraded = found.Degraded || (usedFallback && anyFailed);
        if (withKaraoke)
        {
            // 「请求失败」和「酷狗确实没有 / 对不上」必须分开：前者是网络抖动，这份结果
            // 缺了逐字只是暂时的，得标成降级、不进缓存，等下次重抓补上；后者（返回 null、
            // 或抓到了但对齐闸没过）是这首歌的正常结果，可以放心缓存。
            // 原先两者一起被一个 catch 吞掉，一次超时就把「没有逐字」冻结进缓存 30 天
            List<KrcLine>? krc = null;
            try
            {
                krc = await FetchKugouKaraokeAsync(title, artist, KugouDurationOf(durationS, found.DurationS));
            }
            catch
            {
                // 网络异常、非 JSON 应答、状态码不正常（限流/风控）、KRC 解码失败都算「没拿到」而不是「没有」
                degraded = true;
            }
            try
            {
                if (krc is { Count: > 0 })
                {
                    // KRC 侧也得滤制作信息：它开头那几行（「歌手 - 歌名」、「作词：…」、
                    // 「编曲：…」）同样带着逐字时间戳，一旦拿 KRC 当文本主体就会显示到任务栏上
                    var krcCredit = CreditMask(krc.Select(k => k.Plain).ToList(), null, title, artist);
                    var kept = krc.Where((_, i) => !krcCredit[i]).ToList();
                    if (kept.Count > 0 && kept.Count >= krc.Count * 0.4) krc = kept;

                    var al = AlignLines(lines, krc);
                    // 两道闸决定敢不敢反转主从（反转的收益见 BuildFromKrc 的注释）：
                    //   1. 配对覆盖了 KRC 的大半行——覆盖率极低多半是酷狗那边搜错了歌，
                    //      此时用它的文本就是显示错歌词，比逐字不准严重得多；
                    //   2. KRC 行数不明显少于主歌词——只录了副歌的残缺版 KRC 当主体
                    //      会把大段歌词丢掉。
                    var covered = al.Pairs.Count / (double)krc.Count;
                    if (covered >= 0.5 && krc.Count >= lines.Count * 0.6)
                        // 带译文的歌按主歌词的断句重切，译文才能逐句对得上（见 BuildByMainLines）；
                        // 它走不通、或这首歌压根没有译文（KRC 自己的细断句更好读）时照旧按 KRC 的行来
                        (lines, karaoke) = (lines.Any(l => !string.IsNullOrEmpty(l.Trans))
                            ? BuildByMainLines(lines, krc, al) : null) ?? BuildFromKrc(lines, krc, al);
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
                // 对齐本身出异常是算法问题，重抓也还是同一份数据，按「对不上」处理不标降级
                karaoke = new Dictionary<int, List<KaraokeWord>>(); // 逐字失败不影响逐行
            }
        }
        var result = new FetchResult(lines, karaoke, sourceName, found.DurationS, degraded, primaryNotFound,
            usedFallback);
        // 只缓存首选源的完整结果：落到备选源说明首选源当时抓失败了（多半是网络抖动），
        // 那是调用方 5s 后要重试自愈的情况，写进缓存等于把「没有译文的次优结果」
        // 永久冻结，重试也只会一遍遍读到同一份坏缓存。
        // 降级结果同理：网易云首选候选的歌词请求失败而用了次选候选，或逐字请求失败，
        // 都是「这次没拿到」而不是「本来就这样」，同样不能冻结。
        // 已知代价：开着逐字而酷狗长期连不上（它的三个接口都是明文 http，海外网络、拦 http 的
        // 代理/防火墙下每次都超时）时，每首歌都被标成降级——一律不进缓存（切歌瞬间出词、
        // 断网照样有词都不再成立），调用方每首歌还会多重试 2 遍、每遍白等酷狗超时。
        // 这类用户关掉逐字即可恢复缓存。
        // 网易云的备胎（歌手对不上）只有在各源都干净地答过「没有原唱」时才缓存（上面 degraded 已把
        // 「有源请求失败」排除掉）：那是 SMTC 歌手写法与曲库不一致的歌，每次重抓都是同一个结果
        if (useCache && sourceName == "_fetch_netease" && !degraded)
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
    /// <param name="durationS">模拟 SMTC 上报的时长（秒）。省略为 0，相当于网易云客户端（它不报 timeline）；
    /// 会报时长的播放器（QQ 音乐、Spotify）要走时长闸，有的问题只在那条路上出现（「告白氣球」抓到翻唱）。</param>
    public static async Task RunConsoleTestAsync(string title, string artist,
        string secondLine = "translation", double durationS = 0)
    {
        // 诊断入口一律绕过缓存：否则改完匹配算法再来验证，读到的还是上次的结果
        var (found, karaoke, source, songDur, degraded, primaryNotFound, artistMismatch) =
            await FetchAsync(title, artist, durationS, secondLine: secondLine, useCache: false);
        if (found == null)
        {
            Console.WriteLine(primaryNotFound ? "没找到歌词（网易云确实没有这首歌）" : "没找到歌词");
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
        // 这几个标志决定这份结果会不会进缓存、调用方还重不重试，诊断时一并亮出来
        if (degraded) Console.WriteLine("  [降级] 有一步请求失败，这份结果不会写缓存");
        if (primaryNotFound) Console.WriteLine("  [网易云确实没有这首歌，或只有歌手对不上的版本] 调用方不再为它重试");
        if (artistMismatch) Console.WriteLine("  [歌手对不上] 各源都没有这位歌手的版本，退回了同名的备胎（可能是翻唱）");

        // 主歌词侧对照（不抓逐字，拿到的就是网易云原样的行集）：反转成以 KRC 为文本主体后
        // 译文是靠跨源配对挂回来的，某行缺译文有两种完全不同的成因——网易云自己的译文轨
        // 就没对上这行（MergeTranslation 的时间容差），或者跨源配对没把它挂上。两侧一比即分晓
        var (raw, _, _, _, _, _, _) = await FetchAsync(title, artist, durationS, withKaraoke: false,
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
        // 时长取值与正式路径同一个函数：没给时长时相当于网易云客户端 durationS 恒为 0，退回曲库时长
        List<KrcLine>? krc = null;
        try { krc = await FetchKugouKaraokeAsync(title, artist, KugouDurationOf(durationS, songDur)); }
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
