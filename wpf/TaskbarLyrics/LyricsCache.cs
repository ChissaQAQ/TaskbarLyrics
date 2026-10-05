// 歌词磁盘缓存：抓到的歌词行 + 逐字时间表按歌落盘，切歌时先查本地。
//
// 三个好处：切歌瞬间出词（原先每次都要跑一遍搜索+详情两轮 HTTP）、
// 断网时听过的歌照样有歌词、以及少打三家曲库的公开接口（它们没有义务伺候我们）。
//
// 只缓存首选源（网易云）且未降级的结果，理由见 Lyrics.FetchAsync 里写入处的注释。
// 所以读回来的结果 Degraded / PrimaryNotFound / ArtistMismatch 恒为 false，存储格式里不必带这几个字段
// （歌手对不上的备胎只在各源都干净地答过「没有原唱」时才缓存，那已是最终答案，不需要再重试）。
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskbarLyrics;

public static class LyricsCache
{
    // 键里带版本号：匹配算法或存储格式一变，旧条目自然失联，由容量淘汰慢慢清掉，
    // 不必写迁移代码，也不会拿旧算法的逐字对齐结果糊弄用户
    private const int SchemaVersion = 12; // 2: 逐字改以 KRC 文本为主体；3: 修首句译文被抢；4: 补 rv 让罗马音有内容 + 一句译文对两行时并回一行；5: 候选改按译文覆盖率择优 + 配对空隙夹逼填空；6: 向后吸附找回「一对二只认出一半」的那半；7: 认出「主歌词整份是译文、原文在 KRC 侧」的投稿；8: 候选择优改「歌名分数优先、覆盖率用比率」（旧缓存里可能正躺着抓错的 Live 版）；9: 合并配对从「一对二」放宽到「一对多」（旧缓存里躺着被切成两三行、且只有一行带译文的句子）；10: 歌手匹配做繁简/全角/多歌手拆分归一化 + 歌名分数认出 Live/伴奏/翻唱等版本标签，候选选择变了（旧缓存里可能躺着歌手闸失灵时抓错的翻唱/Live 版）；降级结果（首选候选或逐字请求失败）不再缓存，旧缓存里可能冻结着这种残缺结果；11: 制作信息改按位置放宽（开头/结尾整块的「键：值」行、主歌词的「歌名 - 歌手」标题行）+ 剪掉译文行尾的译者署名，缓存存的是滤完的行，旧条目里冻结着漏网的那一两行；同版还改了歌手闸：网易云只有同名翻唱时先去 QQ/LRCLIB 找原唱（旧缓存里可能躺着带时长播放器抓到的翻唱，如「告白氣球」）；以及版本标签补上 acoustic/弾き語り/piano/变速（旧缓存里可能躺着「晩餐歌」的 acoustic 版）；12: 跨曲库配对的时间容差改按局部偏移算（两侧打轴逐段漂移超过 3s 的歌，旧缓存里整段冻结着没译文）+ 带译文的歌按主歌词的断句重切（旧缓存里两侧断句错开处的译文挂歪了一行）+ 网易云歌曲搜索漏掉的曲目按专辑找回（旧缓存里可能躺着退到 QQ 的无译文版本）
    private const int TtlDays = 30;      // 歌词本身不变，但坏结果不该被永久冻结
    private const int MaxFiles = 400;    // 超出后淘汰最旧的一批（约 3~5 MB）
    private const int EvictBatch = 100;

    /// <summary>缓存目录，跟着配置文件走（exe 同目录只读时配置会落到 %AppData%，
    /// 缓存也一起跟过去）。每次取值都重算：ConfigPath 会在首次存盘失败后切换。</summary>
    private static string Dir =>
        Path.Combine(Path.GetDirectoryName(AppConfig.ConfigPath) ?? AppContext.BaseDirectory,
            "lyrics-cache");

    /// <summary>缓存键：凡是会影响抓取结果的入参都得进去。
    /// secondLine 决定挂译文还是罗马音、withKaraoke 决定有没有逐字表，
    /// 漏掉任何一个都会让用户改了设置却读到旧结果。</summary>
    public static string KeyFor(string title, string artist, double durationS,
        bool withKaraoke, string secondLine)
        => $"v{SchemaVersion}|{title}|{artist}|{(int)durationS}|{(withKaraoke ? 1 : 0)}|{secondLine}";

    private static string PathFor(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        // 取前 16 字节做文件名：歌名歌手可能含 \ / : 等非法字符，没法直接当文件名
        return Path.Combine(Dir, Convert.ToHexString(hash, 0, 16) + ".json");
    }

    public static Lyrics.FetchResult? TryLoad(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var f = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (f == null || f.V != SchemaVersion || f.L.Count == 0) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - f.At > TtlDays * 86400L) return null;
            var lines = f.L.Select(l => new LyricLine(l.Ms, l.T, l.Tr)).ToList();
            var karaoke = f.K.ToDictionary(
                k => k.Ms,
                k => k.W.Select(w => new KaraokeWord(w.O, w.D, w.T)).ToList());
            return new Lyrics.FetchResult(lines, karaoke, f.Src, f.Dur);
        }
        catch
        {
            return null; // 读坏了就当没缓存，重抓一遍会把它覆盖掉
        }
    }

    public static void Save(string key, Lyrics.FetchResult result)
    {
        if (result.Lines is not { Count: > 0 }) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var f = new CacheFile
            {
                V = SchemaVersion,
                Src = result.Source,
                At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Dur = result.SongDurationS,
                L = result.Lines.Select(l => new CachedLine { Ms = l.Ms, T = l.Text, Tr = l.Trans }).ToList(),
                K = result.Karaoke.Select(kv => new CachedKLine
                {
                    Ms = kv.Key,
                    W = kv.Value.Select(w => new CachedWord { O = w.OffsetMs, D = w.DurationMs, T = w.Text }).ToList(),
                }).ToList(),
            };
            // 先写临时文件再改名：直接覆写的话，写一半被断电/杀进程会留下一份
            // 半截 JSON，之后每次都要解析失败再重抓
            var path = PathFor(key);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(f, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            Evict();
        }
        catch
        {
            // 目录只读、磁盘满：缓存是纯加速手段，写不进去不影响功能
        }
    }

    /// <summary>超量时删掉最旧的一批（按最后写入时间）。</summary>
    private static void Evict()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.json");
            if (files.Length <= MaxFiles) return;
            foreach (var fi in files.OrderBy(f => f.LastWriteTimeUtc)
                         .Take(files.Length - MaxFiles + EvictBatch))
                fi.Delete();
        }
        catch
        {
            // 删不掉就下次再说，不影响本次写入
        }
    }

    /// <summary>清空缓存（设置页「清空歌词缓存」用）。返回删掉的条目数。</summary>
    public static int Clear()
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(Dir)) return 0;
            foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json"))
            {
                try { f.Delete(); n++; }
                catch { /* 单个文件占用中，跳过 */ }
            }
        }
        catch
        {
            // 目录读不了，当作没清到
        }
        return n;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- 存储格式（字段名刻意取短，一首歌能省下几 KB）----
    // 用专门的 DTO 而不是直接序列化 LyricLine/KaraokeWord：那两个是位置参数的
    // record struct，属性只读，System.Text.Json 反序列化值类型时会优先挑无参构造，
    // 结果是一份字段全为默认值的空壳——静默失效比编译不过更难查

    private sealed class CacheFile
    {
        public int V { get; set; }
        public string Src { get; set; } = "";
        public long At { get; set; }
        public double Dur { get; set; }
        public List<CachedLine> L { get; set; } = new();
        public List<CachedKLine> K { get; set; } = new();
    }

    private sealed class CachedLine
    {
        public int Ms { get; set; }
        public string T { get; set; } = "";
        public string? Tr { get; set; }
    }

    private sealed class CachedKLine
    {
        public int Ms { get; set; }
        public List<CachedWord> W { get; set; } = new();
    }

    private sealed class CachedWord
    {
        public int O { get; set; }
        public int D { get; set; }
        public string T { get; set; } = "";
    }
}
