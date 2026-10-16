// 听歌统计的控制台自测（TaskbarLyrics.exe --stats-test）：
//   不带参数              用假时钟把记录器和汇总的各种场景跑一遍，逐项断言，有一项不符就以退出码 1 结束
//   report                对本机的真实记录输出文字版报告，跟自己的直觉对账
//   shot <目录> [real]    把统计窗口、分享图、年度报告渲染成 PNG（浅色、深色）；默认用合成数据，real 用真实记录
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TaskbarLyrics;

internal static class StatsSelfTest
{
    private static int _fails;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "  ok  " : "  FAIL")} {what}");
        if (!ok) _fails++;
    }

    private static bool Near(double a, double b, double tol = 0.01) => Math.Abs(a - b) <= tol;

    private static string Dump(IEnumerable<ListenSegment> segs)
        => string.Join("; ", segs.Select(s => $"{s.Ti} S={s.S} P={s.P}"));

    private static PlaybackState Song(string title, string artist, double durS, bool playing = true,
        string src = "cloudmusic.exe")
        => new() { Title = title, Artist = artist, DurationS = durS, Playing = playing, SourceId = src };

    private static PlaybackState Paused(PlaybackState s) => Song(s.Title, s.Artist, s.DurationS, false, s.SourceId);

    /// <summary>假时钟驱动的记录器：单调钟、墙钟一起走，也可以单独拨墙钟模拟休眠。</summary>
    private sealed class Rig
    {
        public readonly string Dir;
        public double Mono = 1000;
        public DateTimeOffset Wall;
        public readonly ListenRecorder Rec;

        public Rig(string root, string name, DateTimeOffset start)
        {
            Dir = Path.Combine(root, name);
            Wall = start;
            Rec = new ListenRecorder(() => Dir, () => Mono, () => Wall);
        }

        /// <summary>每 0.5 秒喂一次同一个状态，共 seconds 秒。与真实轮询一样，
        /// 最后这半秒要等下一次 Observe 才记上。</summary>
        public void Feed(PlaybackState? s, double seconds, LyricsMatch? lyrics = null)
        {
            for (var t = 0.0; t < seconds - 1e-9; t += 0.5)
            {
                Rec.Observe(s, lyrics);
                Mono += 0.5;
                Wall = Wall.AddSeconds(0.5);
            }
        }

        public List<ListenSegment> Disk() => StatsStore.ReadAll(Dir);
    }

    public static int Run(string[] args)
    {
        if (args.Length >= 1 && args[0] == "report") return Report();

        var root = Path.Combine(Path.GetTempPath(), $"TaskbarLyrics-stats-test-{Environment.ProcessId}");
        try
        {
            RecorderTests(root);
            V2Tests(root);
            StoreTests(root);
            ReportTests();
            AlbumTests();
            ArtTests(root);
            FilterTests();
            YearTests();
            HelperTests();
            ShareImageTests();
            PerfTest();
        }
        catch (Exception ex)
        {
            // 断言之外的崩溃（取不到预期的卡片之类）也算不通过，把堆栈打出来
            Console.WriteLine(ex);
            _fails++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 临时目录，删不掉无所谓 */ }
        }
        Console.WriteLine(_fails == 0 ? "全部通过" : $"{_fails} 项不符");
        return _fails == 0 ? 0 : 1;
    }

    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 20, 0, 0, TimeSpan.FromHours(8));

    private static void RecorderTests(string root)
    {
        Console.WriteLine("[记录器]");
        var qingtian = Song("晴天", "周杰伦", 270);

        {
            var r = new Rig(root, "basic", T0);
            r.Feed(qingtian, 120);
            r.Feed(Paused(qingtian), 5);
            var d = r.Disk();
            Check(d.Count == 1 && Near(d[0].S, 120) && d[0].P == 1, $"听 120 秒后暂停 → 1 段、120 秒、1 次（{Dump(d)}）");
            Check(d.Count == 1 && d[0].T == T0.ToUnixTimeSeconds() && d[0].O == 480 && d[0].Src == "cloudmusic.exe",
                "片段记下开始时刻、时区偏移和播放器");
        }
        {
            var r = new Rig(root, "short", T0);
            r.Feed(qingtian, 25);
            r.Feed(Paused(qingtian), 1);
            var d = r.Disk();
            Check(d.Count == 1 && Near(d[0].S, 25) && d[0].P == 0, $"只听 25 秒 → 记时长不记次数（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "loop", T0);
            r.Feed(Song("Lemon", "米津玄師", 60), 200);
            r.Rec.Flush();
            var d = r.Disk();
            Check(d.Sum(s => s.P) == 3, $"60 秒的歌单曲循环 200 秒 → 3 次（{Dump(d)}）");
            Check(d.Count > 0 && d[^1].E == "exit", "Flush 落盘的那段结束原因记 exit");
        }
        {
            var r = new Rig(root, "pause", T0);
            r.Feed(qingtian, 40);
            r.Feed(Paused(qingtian), 100);
            r.Feed(qingtian, 40);
            r.Feed(Paused(qingtian), 1);
            var d = r.Disk();
            Check(d.Count == 2 && Near(d.Sum(s => s.S), 80) && d.Sum(s => s.P) == 1,
                $"听 40 秒、暂停 100 秒、再听 40 秒 → 2 段、共 80 秒、1 次（{Dump(d)}）");
        }
        {
            // 自动选源：正在放的播放器一暂停，显示被另一个停着的播放器顶走，过会儿又切回来
            var r = new Rig(root, "multi", T0);
            var qq = Song("七里香", "周杰伦", 300, src: "QQMusic.exe");
            r.Feed(qq, 20);
            r.Feed(Song("Lemon", "米津玄師", 250, playing: false), 10);
            r.Feed(qq, 20);
            r.Feed(Paused(qq), 1);
            var d = r.Disk();
            Check(d.Count == 2 && d.Sum(s => s.P) == 1 && Near(d.Sum(s => s.S), 40),
                $"被别的播放器临时顶走不打断计数：20 + 20 秒 → 1 次（{Dump(d)}）");
            Check(d[0].E == "pause", $"顶上来的是停着的播放器：按暂停记，不算换播放器（{d[0].E}）");
        }
        {
            var r = new Rig(root, "null", T0);
            r.Feed(qingtian, 10);
            r.Feed(null, 2);
            r.Feed(qingtian, 10);
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d.Count == 2 && Near(d[0].S, 10) && Near(d[1].S, 10) && d[0].E == "none" && d[1].E == "none",
                $"中途收到 null（读失败、记录关闭）→ 切成两段，结束原因 none（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "sleep", T0);
            r.Feed(qingtian, 10);
            r.Mono += 0.5;
            r.Wall = r.Wall.AddHours(1); // 睡了一小时：单调钟不走，墙钟走了
            r.Feed(qingtian, 10);
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d.Count == 2 && Near(d[0].S, 9.5) && Near(d[1].S, 10)
                  && d[1].T == T0.AddSeconds(3610).ToUnixTimeSeconds() && d[0].E == "jump",
                $"休眠醒来 → 睡着那段不计，醒来另起一段、开始时刻是醒来的时刻，结束原因 jump（{Dump(d)}）");
        }
        {
            // 听着歌换了时区（出差落地、手动改时区）：同一时刻，只是偏移变了
            var r = new Rig(root, "tz", T0);
            r.Feed(qingtian, 10);
            r.Wall = r.Wall.ToOffset(TimeSpan.FromHours(9));
            r.Feed(qingtian, 10);
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d.Count == 2 && d[0].O == 480 && d[1].O == 540 && Near(d[0].S, 10) && Near(d[1].S, 10)
                  && d[1].T == T0.AddSeconds(10).ToUnixTimeSeconds() && d[0].E == "tz",
                $"播放中换了时区 → 切成两段，各记各的偏移，时长一秒不少（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "cap", T0);
            r.Rec.Observe(qingtian, null);
            r.Mono += 10;
            r.Wall = r.Wall.AddSeconds(10);
            r.Rec.Observe(qingtian, null);
            r.Rec.Observe(null, null);
            var d = r.Disk();
            Check(d.Count == 1 && Near(d[0].S, 3), $"两次回调隔了 10 秒 → 最多记 3 秒（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "nodur", T0);
            var s = Song("某首歌", "某人", 0);
            r.Feed(s, 100);
            r.Feed(Paused(s), 1);
            var d = r.Disk();
            Check(d.Sum(x => x.P) == 1, $"时长未知、听了 100 秒 → 只记 1 次（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "fallback", T0);
            var s = Song("某首歌", "某人", 0);
            r.Feed(s, 200, new LyricsMatch(s.Key, 60, null, null));
            r.Rec.Flush();
            var d = r.Disk();
            Check(d.Sum(x => x.P) == 3 && d.All(x => x.D == 60 && x.Dl),
                $"SMTC 不报时长时用歌词接口的时长 60 秒，循环 200 秒 → 3 次，记下时长来自歌词库（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "tinydur", T0);
            var s = Song("某首歌", "某人", 5);
            r.Feed(s, 25);
            r.Feed(Paused(s), 1);
            var d = r.Disk();
            Check(d.Sum(x => x.P) == 0, $"报的时长只有 5 秒（不可信）→ 按未知算，25 秒不够 1 次（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "cut", T0);
            r.Feed(qingtian, 700);
            r.Feed(Paused(qingtian), 1);
            var d = r.Disk();
            Check(d.Count == 3 && Near(d[0].S, 300) && Near(d[1].S, 300) && Near(d.Sum(s => s.S), 700)
                  && d[1].T - d[0].T == 300 && d.Sum(s => s.P) == 3
                  && d[0].E == "cap" && d[1].E == "cap" && d[2].E == "pause",
                $"连续听 700 秒 → 每满 300 秒切一段（cap），共 3 段、3 次（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "snapshot", T0);
            r.Feed(qingtian, 50);
            var snap = r.Rec.Snapshot([r.Dir]);
            Check(r.Disk().Count == 0 && snap.Count == 1 && Near(snap[0].S, 49.5) && snap[0].E == null && snap[0].V == 2,
                $"统计快照带上还没落盘的那段，结束原因留空（{Dump(snap)}）");
        }
        {
            var r = new Rig(root, "close", T0);
            r.Feed(qingtian, 50);
            r.Rec.Close();
            r.Feed(qingtian, 50);
            r.Rec.Flush();
            var d = r.Disk();
            Check(d.Count == 1 && Near(d[0].S, 49.5), $"Close 时落盘，之后的回调一律忽略（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "clear", T0);
            r.Feed(qingtian, 40);
            r.Feed(Paused(qingtian), 1);
            r.Feed(qingtian, 20);
            var ok = r.Rec.ClearAll([r.Dir]);
            var empty = r.Disk().Count == 0 && r.Rec.Snapshot([r.Dir]).Count == 0;
            r.Feed(qingtian, 20);
            r.Feed(Paused(qingtian), 1);
            var d = r.Disk();
            // 清空时手里那段也一起扔掉，计数从头来：清空后只听 20 秒不该记次数
            Check(ok && empty && d.Count == 1 && Near(d[0].S, 20) && d[0].P == 0,
                $"清空后盘上、手里都没了，之后照常记录（{Dump(d)}）");
        }
    }

    /// <summary>带进度和元数据的状态。BaseTime 取假时钟当前值，进度才会跟着 Feed 往前走。</summary>
    private static PlaybackState Full(Rig r, string title, double pos, bool playing = true, string album = "叶惠美",
        double durS = 270, string src = "cloudmusic.exe")
        => new()
        {
            Title = title, Artist = "周杰伦", DurationS = durS, Playing = playing, SourceId = src,
            BasePositionS = pos, BaseTime = r.Mono,
            Album = album, AlbumArtist = album.Length > 0 ? "周杰伦" : "", TrackNumber = album.Length > 0 ? 3 : 0,
            Genres = album.Length > 0 ? ["Pop"] : [], PlaybackType = "music", Shuffle = false, Repeat = "list",
        };

    private static readonly System.Text.Json.JsonSerializerOptions ReadableJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(object? x) => x == null ? "null" : System.Text.Json.JsonSerializer.Serialize(x, ReadableJson);

    private static void V2Tests(string root)
    {
        Console.WriteLine("[v2 字段]");
        {
            var r = new Rig(root, "v2", T0);
            var s = Full(r, "晴天", 30);
            // 歌词库报的 269 秒顶不掉播放器报的 270 秒
            r.Feed(s, 40, new LyricsMatch(s.Key, 269, "ne:186016", "叶惠美"));
            r.Feed(Full(r, "晴天", 70, playing: false), 1);
            var d = r.Disk();
            var x = d.Count == 1 ? d[0] : null;
            Check(x is { V: 2, Al: "叶惠美", Aa: "周杰伦", Tn: 3, Ty: "music", Sh: false, Rp: "list", D: 270, Dl: false,
                      E: "pause", Lid: "ne:186016", Lal: "叶惠美", Ge: ["Pop"] } && Near(x.Ps, 30) && Near(x.Pe, 70),
                $"v2 全字段落盘再读回（{Json(x)}）");
        }
        {
            var r = new Rig(root, "switch", T0);
            r.Feed(Song("晴天", "周杰伦", 270), 10);
            r.Feed(Song("晴天", "周杰伦", 270, src: "QQMusic.exe"), 10);
            r.Feed(Song("Lemon", "米津玄師", 250, src: "QQMusic.exe"), 10);
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d.Select(x => x.E).SequenceEqual(["src", "song", "none"]),
                $"换播放器、换歌各记各的结束原因（{string.Join("、", d.Select(x => x.E))}）");
        }
        {
            // 不是歌的不记：网页里刷的视频（类型报的是 music、歌手为空）、播放器标明是视频的。
            // 网页里有歌手的、音乐软件里没打标签的照记
            var r = new Rig(root, "notsong", T0);
            r.Feed(Song("突然涌现的回忆 - 小红书", "", 60, src: "WeChatAppEx.exe"), 40);
            r.Feed(new PlaybackState { Title = "某个 MV", Artist = "某人", DurationS = 200, Playing = true,
                SourceId = "cloudmusic.exe", PlaybackType = "video" }, 40);
            r.Feed(Song("没打标签的本地文件", "", 200, src: "foobar2000.exe"), 40);
            r.Feed(Song("Lemon", "米津玄師", 250, src: "MSEdge"), 40);
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d.Select(x => x.Ti).SequenceEqual(["没打标签的本地文件", "Lemon"]) && d.All(x => x.P == 1),
                $"网页视频、MV 不记，网页里的歌和没标签的本地文件照记（{Dump(d)}）");
        }
        {
            var r = new Rig(root, "latealbum", T0);
            r.Feed(Full(r, "晴天", 0, album: "上一张"), 2);  // 切歌头两秒还挂着上一首的专辑
            r.Feed(Full(r, "晴天", 2, album: "叶惠美"), 68);
            r.Feed(Full(r, "晴天", 70, album: "下一张"), 5);  // 一分多钟后冒出来的不算
            r.Feed(Full(r, "晴天", 75, playing: false, album: "下一张"), 3);
            r.Feed(Full(r, "晴天", 75, album: ""), 10);        // 续播时播放器没报专辑
            r.Feed(Full(r, "七里香", 0, album: "七里香"), 10);
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d.Select(x => x.Al).SequenceEqual(["叶惠美", "叶惠美", "七里香"]) && d.Count == 3 && d[1].E == "song",
                $"专辑：头几秒以最新的为准、之后只补空、续播沿用、切歌时上一首最后一段仍是自己的（{string.Join("、", d.Select(x => $"{x.Ti}《{x.Al}》{x.E}"))}）");
        }
        {
            var r = new Rig(root, "fillalbum", T0);
            r.Feed(Full(r, "晴天", 0, album: ""), 8);
            r.Feed(Full(r, "晴天", 8), 10);
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d is [{ Al: "叶惠美", Aa: "周杰伦", Tn: 3 }], $"开头没报专辑、过了几秒才报 → 补上（{Json(d.FirstOrDefault())}）");
        }
        {
            var r = new Rig(root, "latematch", T0);
            var s = Full(r, "某首歌", 0, album: "", durS: 0);
            r.Feed(s, 10);
            r.Feed(s, 10, new LyricsMatch(s.Key, 200, "ne:1", "专辑甲"));
            r.Feed(s, 10, new LyricsMatch("别的歌｜别人", 300, "ne:2", "专辑乙")); // 切歌后还挂着的旧结果
            r.Feed(s, 10, new LyricsMatch(s.Key, 200, null, null));             // 后来的 null 不擦掉
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d is [{ Lid: "ne:1", Lal: "专辑甲", D: 200, Dl: true, Al: null }],
                $"歌词匹配晚到也记上，对不上这首歌的不用（{Json(d.FirstOrDefault())}）");
        }
        {
            var r = new Rig(root, "mismatch", T0);
            var s = Full(r, "某首歌", 0, album: "", durS: 0);
            r.Feed(s, 40, new LyricsMatch(s.Key, 60, null, null));
            r.Feed(null, 1);
            var d = r.Disk();
            Check(d is [{ Lid: null, Lal: null, D: 60, Dl: true, P: 1 }],
                $"歌手对不上的备胎：不记 id，时长照用（{Json(d.FirstOrDefault())}）");
        }
        Check(SmtcListener.TypeName(Windows.Media.MediaPlaybackType.Music) == "music"
              && SmtcListener.TypeName(Windows.Media.MediaPlaybackType.Video) == "video"
              && SmtcListener.TypeName(null) == ""
              && SmtcListener.RepeatName(Windows.Media.MediaPlaybackAutoRepeatMode.None) == "none"
              && SmtcListener.RepeatName(Windows.Media.MediaPlaybackAutoRepeatMode.Track) == "track"
              && SmtcListener.RepeatName(Windows.Media.MediaPlaybackAutoRepeatMode.List) == "list"
              && SmtcListener.RepeatName(null) == "",
            "TypeName / RepeatName 映射");
    }

    private static void StoreTests(string root)
    {
        Console.WriteLine("[存储]");
        {
            // 断电留下的坏尾巴：半行 + 一串 \0。下一次追加要先收拾好，不能把新的一行也拖成坏行
            var r = new Rig(root, "tail", T0);
            Directory.CreateDirectory(r.Dir);
            var path = Path.Combine(r.Dir, "2026-03.jsonl");
            var old = $"{{\"t\":{T0.AddHours(-1).ToUnixTimeSeconds()},\"o\":480,\"s\":100,\"ti\":\"旧记录\",\"ar\":\"某人\",\"p\":1}}\n";
            File.WriteAllBytes(path, [.. System.Text.Encoding.UTF8.GetBytes(old + "{\"t\":17"), 0, 0, 0, 0]);
            var s = Song("晴天", "周杰伦", 270);
            r.Feed(s, 40);
            r.Feed(Paused(s), 1);
            var d = r.Disk();
            var bytes = File.ReadAllBytes(path);
            Check(d.Count == 2 && d[0].Ti == "旧记录" && d[1].Ti == "晴天" && !bytes.Contains((byte)0),
                $"文件尾的半行和 \\0 被收拾掉，新旧记录都读得出（{Dump(d)}）");
        }
        {
            var dir = Path.Combine(root, "validate");
            Directory.CreateDirectory(dir);
            var t = T0.ToUnixTimeSeconds();
            File.WriteAllLines(Path.Combine(dir, "2026-03.jsonl"),
            [
                $"{{\"t\":{t},\"s\":100,\"ti\":\"好的\",\"ar\":\"某人\",\"p\":1}}",
                $"{{\"t\":0,\"s\":100,\"ti\":\"没时刻\"}}",
                $"{{\"t\":{t},\"s\":-5,\"ti\":\"负时长\"}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"\"}}",
                $"{{\"t\":{t},\"s\":100,\"o\":9999,\"ti\":\"时区越界\"}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"负次数\",\"p\":-1}}",
                $"{{\"t\":{t * 1000},\"s\":100,\"ti\":\"毫秒时间戳\"}}",
                "不是 JSON",
                $"{{\"t\":{t},\"s\":50,\"ti\":\"歌手为空\",\"ar\":null,\"src\":null}}",
                "",
            ]);
            var d = StatsStore.ReadAll(dir);
            Check(d.Count == 2 && d[0].Ti == "好的" && d[1].Ti == "歌手为空" && d[1].Ar == "" && d[1].Src == "",
                $"坏行逐行跳过，null 字段补成空串（{Dump(d)}）");
            Check(d.Count == 2 && d[0] is { V: 0, Al: null, D: 0, E: null, Lid: null, Ge: null },
                "v1 的行照常读，v2 字段都是默认值");
        }
        {
            // 早先没筛、已经记在盘上的视频，读的时候滤掉
            var dir = Path.Combine(root, "notsong-disk");
            Directory.CreateDirectory(dir);
            var t = T0.ToUnixTimeSeconds();
            File.WriteAllLines(Path.Combine(dir, "2026-03.jsonl"),
            [
                $"{{\"t\":{t},\"s\":100,\"ti\":\"小红书视频\",\"ar\":\"\",\"src\":\"WeChatAppEx.exe\",\"ty\":\"music\",\"p\":1}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"火狐里的视频\",\"src\":\"308046B0AF4A39CB\",\"p\":1}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"MV\",\"ar\":\"某人\",\"src\":\"cloudmusic.exe\",\"ty\":\"video\",\"p\":1}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"晴天\",\"ar\":\"周杰伦\",\"src\":\"cloudmusic.exe\",\"p\":1}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"Lemon\",\"ar\":\"米津玄師\",\"src\":\"chrome.exe\",\"ty\":\"music\",\"p\":1}}",
            ]);
            var d = StatsStore.ReadAll(dir);
            Check(d.Select(x => x.Ti).SequenceEqual(["晴天", "Lemon"]), $"盘上的网页视频、MV 读的时候滤掉（{Dump(d)}）");
        }
        {
            // v2 字段坏了只清那个字段，不丢整行；类型都不对的（"d":"abc"）跟旧字段一样整行丢
            var dir = Path.Combine(root, "sanitize");
            Directory.CreateDirectory(dir);
            var t = T0.ToUnixTimeSeconds();
            File.WriteAllLines(Path.Combine(dir, "2026-03.jsonl"),
            [
                $"{{\"t\":{t},\"s\":100,\"ti\":\"负数\",\"d\":-5,\"ps\":-1,\"pe\":-2,\"tn\":-3}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"空白\",\"al\":\"  \",\"aa\":\"\",\"lid\":\" \",\"e\":\"\",\"ge\":[\"\",\"Pop\",\" \",null]}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"超长\",\"d\":100000,\"ge\":[\" \"]}}",
                $"{{\"t\":{t},\"s\":100,\"ti\":\"类型错\",\"d\":\"abc\"}}",
            ]);
            var d = StatsStore.ReadAll(dir);
            Check(d.Count == 3 && d[0] is { D: 0, Ps: 0, Pe: 0, Tn: 0 }
                  && d[1] is { Al: null, Aa: null, Lid: null, E: null, Ge: ["Pop"] } && d[2] is { D: 0, Ge: null },
                $"v2 字段宽松清洗（{string.Join("; ", d.Select(Json))}）");
        }
        {
            var d = StatsStore.ReadAll(Path.Combine(root, "不存在的目录"));
            Check(d.Count == 0 && StatsStore.Clear(Path.Combine(root, "不存在的目录")), "目录不存在时读出空、清空算成功");
        }
        {
            // config 存不进程序目录时会转存到 %AppData%：两处都可能有记录，读要合起来读，清要一起清
            string a = Path.Combine(root, "multi-a"), b = Path.Combine(root, "multi-b");
            var t = T0.ToUnixTimeSeconds();
            foreach (var (dir, ti) in new[] { (a, "程序目录里的"), (b, "AppData 里的") })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "2026-03.jsonl"), $"{{\"t\":{t},\"s\":100,\"ti\":\"{ti}\"}}\n");
            }
            var both = StatsStore.ReadAll([a, b]);
            var ok = StatsStore.Clear([a, b]);
            Check(both.Count == 2 && ok && StatsStore.ReadAll([a, b]).Count == 0,
                $"多个目录合起来读、一起清空（{Dump(both)}）");

            var dirs = StatsStore.AllDirs;
            var full = dirs.Select(x => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x))).ToList();
            Check(dirs[0] == StatsStore.DefaultDir && full.Distinct(StringComparer.OrdinalIgnoreCase).Count() == full.Count,
                $"AllDirs 以当前目录打头、没有重复（{string.Join("；", dirs)}）");
        }
    }

    private static ListenSegment Seg(DateTimeOffset at, double s, string ti, string ar, int p = 0,
        string src = "cloudmusic.exe", string? al = null, string? aa = null, string? lal = null)
        => new()
        {
            T = at.ToUnixTimeSeconds(),
            O = (int)at.Offset.TotalMinutes,
            S = s,
            Ti = ti,
            Ar = ar,
            Src = src,
            P = p,
            Al = al,
            Aa = aa,
            Lal = lal,
        };

    private static readonly TimeSpan Cst = TimeSpan.FromHours(8);

    private static DateTimeOffset At(int month, int day, int hour, int minute)
        => new(2026, month, day, hour, minute, 0, Cst);

    /// <summary>汇总、年报共用的一份小样本（「现在」是 2026-03-11 12:00，周三）。</summary>
    private static List<ListenSegment> Sample() =>
    [
        Seg(At(1, 5, 20, 0), 600, "晴天", "周杰伦", 1),
        Seg(At(3, 8, 21, 0), 900, "晴天", "周杰伦", 1),
        Seg(At(3, 9, 21, 30), 900, "晴天", "周杰伦", 1),
        Seg(At(3, 10, 2, 10), 600, "夜に駆ける", "YOASOBI", 1),
        Seg(At(3, 10, 22, 0), 1000, "Lemon", "米津玄師", 4),
        Seg(At(3, 10, 23, 50), 1200, "七里香", "周杰伦", 1),
        Seg(At(3, 11, 8, 0), 30, "稻香", "周杰伦"),
        Seg(At(3, 20, 10, 0), 600, "未来的歌", "某人"), // 系统时间曾被调到将来时留下的
    ];

    private static readonly DateTime SampleNow = new(2026, 3, 11, 12, 0, 0);

    private static string Rows(IEnumerable<StatsRankRow> rows) => string.Join("、", rows.Select(r => $"{r.Title} {r.Right}"));

    private static void ReportTests()
    {
        Console.WriteLine("[汇总]");
        var now = SampleNow;
        var segs = Sample();
        // 打乱顺序：汇总不该依赖片段在文件里的先后
        segs.Reverse();
        var snap = StatsReport.Build(segs, now);
        var week = snap.Ranges[StatsRange.Week];
        var month = snap.Ranges[StatsRange.Month];
        var all = snap.Ranges[StatsRange.All];

        Check(Near(snap.Days[28].Value, 2200) && Near(snap.Days[29].Value, 630),
            $"跨午夜的片段拆给前后两天（3/10 = {snap.Days[28].Value}，3/11 = {snap.Days[29].Value}）");
        Check(Near(all.Hours[23].Value, 600) && Near(all.Hours[0].Value, 600) && Near(all.Hours[21].Value, 1800),
            "24 小时分布按实际钟点落位");
        Check(week.Summary == "这 7 天你听了 1 小时 17 分，播放 8 次，共 5 首歌，其中 4 首是第一次听。", $"近 7 天小结：{week.Summary}");
        Check(all.Summary == "从 2026 年 1 月 5 日开始，你一共听了 1 小时 37 分，播放 9 次，共 6 首歌、4 位歌手。",
            $"全部小结：{all.Summary}");
        Check(!week.Songs.ByTime.Any(s => s.Title == "未来的歌") && all.Songs.ByTime.Any(s => s.Title == "未来的歌"),
            "将来日期的记录只进「全部」");
        Check(week.Songs.ByPlays[0].Title == "Lemon" && week.Songs.ByPlays[0].Right == "4 次 · 16 分"
              && week.Songs.ByPlays[1].Title == "晴天" && week.Songs.ByPlays[2].Title == "七里香",
            $"歌曲榜按次数排、同次数按时长（{Rows(week.Songs.ByPlays)}）");
        Check(week.Songs.ByTime[0].Title == "晴天" && week.Songs.ByTime[0].Right == "30 分 · 2 次",
            $"歌曲榜按时长排，右侧先写时长（{Rows(week.Songs.ByTime)}）");
        Check(week.Artists.ByPlays[0].Title == "米津玄師" && week.Artists.ByTime[0].Title == "周杰伦"
              && week.Artists.ByTime[0].Sub == "3 首歌",
            $"歌手榜两种排法、并数歌（按次数 {Rows(week.Artists.ByPlays)}；按时长 {Rows(week.Artists.ByTime)}）");
        Check(month.PeakText == "你最常在夜晚听歌：21:00–00:00 这三个小时占了 73%。", $"高峰时段：{month.PeakText}");
        Check(month.Hours.Count(h => h.Highlight) == 3 && month.Hours[21].Highlight && month.Hours[23].Highlight,
            "高峰三小时的柱子高亮");

        // 2026-03-10 是周二：Lemon 22 点那 1000 秒落在「周二 22 点」，七里香 23:50 起的 20 分钟周二 23 点、周三 0 点各 600 秒
        Check(week.Heat.Count == 168 && Near(week.Heat[1 * 24 + 22].Value, 1000) && Near(week.Heat[1 * 24 + 23].Value, 600)
              && Near(week.Heat[2 * 24].Value, 600) && week.Heat[1 * 24 + 22].Tip.StartsWith("周二 22:00–23:00", StringComparison.Ordinal),
            $"热力图按星期、钟点落位（{week.Heat[1 * 24 + 22].Tip}）");
        Check(Near(week.Heat.Sum(c => c.Value), 4630) && Near(all.Heat.Sum(c => c.Value), snap.TotalS),
            "热力图各格加起来等于总时长");
        Check(week.HeatText == "最集中的一格是周二 22:00–23:00，占了 22%。", $"热力图小结：{week.HeatText}");

        Check(week.NewSongs.ByPlays.Count == 4 && !week.NewSongs.ByPlays.Any(r => r.Title == "晴天") && week.NewSongsNote == ""
              && week.NewSongs.ByPlays[0] is { Title: "Lemon", Sub: "米津玄師 · 3/10 第一次听" },
            $"新发现的歌：近 7 天第一次听的 4 首，与小结一致（{Rows(week.NewSongs.ByPlays)}）");
        Check(all.NewSongs.ByPlays.Count == 0 && all.NewSongsNote.Length > 0, "「全部」不列新歌，给一句说明");
        Check(all.Albums.ByPlays.Count == 0 && all.AlbumNote == "这段时间的记录里没有专辑信息：旧版本不记专辑，有的播放器也不报。",
            "全是旧版本的记录（没有专辑）时，专辑榜说明原因");

        var o = snap.Overview.ToDictionary(f => f.Title);
        Check(o["累计听歌"].Value == "1 小时 37 分" && o["累计听歌"].Detail == "9 次播放 · 6 首歌", "概览：累计");
        Check(o["今天"].Value == "10 分" && o["今天"].Detail == "0 次播放", $"概览：今天 {o["今天"].Value}（{o["今天"].Detail}）");
        Check(o["日均"].Value == "2 分" && o["日均"].Detail == "近 30 天", $"概览：日均 {o["日均"].Value}（{o["日均"].Detail}）");

        var f = snap.Facts.ToDictionary(x => x.Title);
        Check(f.TryGetValue("单日循环纪录", out var loop) && loop.Value == "4 遍" && loop.Detail.Contains("《Lemon》"),
            "趣味：单日循环纪录");
        Check(f.TryGetValue("最晚的一次夜猫子", out var owl) && owl.Value == "凌晨 2:20" && owl.Detail.Contains("《夜に駆ける》"),
            $"趣味：夜猫子 {owl?.Value}");
        Check(f["最长连续听歌"].Value == "4 天" && f["最长连续听歌"].Detail == "3/8 – 3/11", "趣味：最长连续");
        Check(f["当前连续听歌"].Value == "4 天" && f["当前连续听歌"].Detail == "今天也听过了", "趣味：当前连续");
        Check(f["一起听歌的第一天"].Detail == "第一首是《晴天》，今天是第 66 天", $"趣味：第一天 {f["一起听歌的第一天"].Detail}");
        Check(Near(snap.Weeks[^1].Value, snap.Days.TakeLast(3).Sum(d => d.Value))
              && Near(snap.Months[^1].Value, snap.Days.TakeLast(11).Sum(d => d.Value)),
            $"本周、本月的柱子截到今天，不含将来日期的记录（本月 {snap.Months[^1].Value} 秒）");

        // 开头几秒就切走的碎片不算「第一首」
        List<ListenSegment> skip =
        [
            Seg(At(3, 1, 8, 0), 3, "キリトリセン", "某人"),
            Seg(At(3, 1, 8, 1), 300, "晴天", "周杰伦", 1),
        ];
        Check(StatsReport.Build(skip, now).Facts.Single(x => x.Title == "一起听歌的第一天").Detail.StartsWith("第一首是《晴天》", StringComparison.Ordinal)
              && StatsReport.BuildYear(skip, 2026, now).Highlights.Single(h => h.Title == "这一年的第一首歌").Detail.StartsWith("《晴天》", StringComparison.Ordinal),
            "第一首取算过一次播放的那段，跳过的碎片不算");

        // 同一首歌的不同写法合并：繁简、歌手顺序与分隔符；「&」不拆
        var merge = StatsReport.Build(
        [
            Seg(new DateTimeOffset(2026, 3, 11, 10, 45, 0, new TimeSpan(5, 30, 0)), 1800, "告白氣球", "周杰倫", 1),
            Seg(At(3, 11, 14, 0), 300, "告白气球", "周杰伦", 1),
            Seg(At(3, 11, 14, 10), 200, "Señorita", "Shawn Mendes / Camila Cabello", 1),
            Seg(At(3, 11, 14, 20), 200, "Señorita", "Camila Cabello、Shawn Mendes", 1),
            Seg(At(3, 11, 14, 30), 200, "The Sound of Silence", "Simon & Garfunkel", 1),
        ], now);
        var ma = merge.Ranges[StatsRange.All];
        Check(ma.Songs.ByPlays.Count == 3 && ma.Songs.ByPlays[0].Right.StartsWith("2 次", StringComparison.Ordinal)
              && ma.Artists.ByPlays.Count == 4,
            $"繁简、歌手顺序不同的同一首歌合并（{ma.Songs.ByPlays.Count} 首、{ma.Artists.ByPlays.Count} 位歌手）");
        // 3/11 是周三（第 2 行）
        Check(Near(ma.Hours[10].Value, 900) && Near(ma.Hours[11].Value, 900)
              && Near(ma.Heat[2 * 24 + 10].Value, 900) && Near(ma.Heat[2 * 24 + 11].Value, 900),
            "+5:30 时区的片段按当时当地的钟点拆（10:45 起听 30 分钟 → 10 点、11 点各 15 分钟，热力图同）");

        // 只在 19 点听过：17、18、19 起头的三段一样多，要取以 19 点居中的 18:00–21:00
        var loneSnap = StatsReport.Build([Seg(At(3, 11, 19, 10), 600, "晴天", "周杰伦", 1)], now);
        var lone = loneSnap.Ranges[StatsRange.All];
        Check(lone.PeakText == "你最常在夜晚听歌：18:00–21:00 这三个小时占了 100%。"
              && lone.Hours.Select((h, i) => h.Highlight ? i : -1).Where(i => i >= 0).SequenceEqual([18, 19, 20]),
            $"高峰时段打平时取中间那小时听得多的一段：{lone.PeakText}");
        var loneWeek = loneSnap.Ranges[StatsRange.Week];
        Check(loneWeek.NewSongs.ByPlays.Count == 0
              && loneWeek.NewSongsNote == "记录是从 3 月 11 日才开始的，这段时间里每首歌都是第一次听。",
            $"记录在这段时间里才开始 → 不列新歌（{loneWeek.NewSongsNote}）");

        var empty = StatsReport.Build([], now);
        Check(empty.Empty && empty.Facts.Count == 0 && empty.Ranges[StatsRange.All].Songs.ByPlays.Count == 0, "没有记录时是空快照");
    }

    private static void AlbumTests()
    {
        Console.WriteLine("[专辑]");
        var v = StatsReport.Build(
        [
            Seg(At(3, 10, 8, 0), 300, "晴天", "周杰伦", 1, al: "叶惠美", aa: "周杰伦"),
            Seg(At(3, 10, 9, 0), 200, "东风破", "周杰伦", 1, lal: "叶惠美"),               // 播放器没报，歌词库登记了
            Seg(At(3, 10, 10, 0), 250, "千里之外", "周杰伦/费玉清", 1, al: "依然范特西"),   // 没报专辑歌手、是合唱
            Seg(At(3, 10, 11, 0), 100, "听妈妈的话", "周杰伦", 1, al: "依然范特西", aa: "周杰伦"),
            Seg(At(3, 10, 12, 0), 400, "Bohemian Rhapsody", "Queen", 1, al: "Greatest Hits", aa: "Queen"),
            Seg(At(3, 10, 13, 0), 150, "Dancing Queen", "ABBA", 1, al: "Greatest Hits", aa: "ABBA"),
            Seg(At(3, 10, 14, 0), 120, "稻香", "周杰伦", 1, al: "魔杰座", lal: "稻香"),
            Seg(At(3, 10, 15, 0), 500, "某首歌", "某人", 1, al: "未知专辑"),
        ], SampleNow).Ranges[StatsRange.All];
        var rows = v.Albums.ByPlays;
        var list = string.Join("、", rows.Select(r => $"{r.Title}（{r.Sub}）{r.Right}"));
        Check(rows.Count == 5, $"共 5 张专辑（{list}）");
        Check(rows.Count > 0 && rows[0] is { Title: "叶惠美", Sub: "周杰伦 · 2 首歌" } && rows[0].Right.StartsWith("2 次", StringComparison.Ordinal),
            "播放器报的和歌词库登记的同名同歌手 → 同一张");
        Check(rows.Where(r => r.Title == "依然范特西").Select(r => r.Sub).SequenceEqual(["周杰伦 · 2 首歌"]),
            "合唱（周杰伦/费玉清）按第一位歌手归进同一张");
        Check(rows.Where(r => r.Title == "Greatest Hits").Select(r => r.Sub).SequenceEqual(["Queen · 1 首歌", "ABBA · 1 首歌"]),
            "不同歌手的同名专辑分开算");
        Check(rows.Any(r => r.Title == "魔杰座") && !rows.Any(r => r.Title == "稻香"), "播放器报的专辑优先于歌词库登记的");
        Check(!rows.Any(r => r.Title == "未知专辑") && v.AlbumNote == "有 25% 的时长不知道是哪张专辑，没算进来。",
            $"「未知专辑」当作没有，缺得多时注明（{v.AlbumNote}）");
        Check(v.Albums.ByTime.Select(r => r.Title).SequenceEqual(["叶惠美", "Greatest Hits", "依然范特西", "Greatest Hits", "魔杰座"]),
            $"专辑榜按时长排（{Rows(v.Albums.ByTime)}）");
    }

    private static void ArtTests(string root)
    {
        Console.WriteLine("[配图]");
        var url = Lyrics.NeteaseCoverUrl(109951169217536854);
        Check(url == "https://p1.music.126.net/GZERNplXUdzTPkKqo2F4tA==/109951169217536854.jpg?param=300y300",
            $"网易云封面地址按 picId 算出来（{url}）");
        Check(Lyrics.NeteaseCoverUrl(0) == null && Lyrics.NeteaseCoverUrl(5639395138885805) == null, "picId 为 0、是占位图 → 没有封面");
        Check(Lyrics.IsNeteaseDefaultPic("https://p2.music.126.net/VnZiScyynLG7atLIZ2YPkw==/18686200114669622.jpg")
            && Lyrics.IsNeteaseDefaultPic("http://p1.music.126.net/6y-UleORITEDbvrOLV0Q8A==/5639395138885805.jpg?param=300y300")
            && !Lyrics.IsNeteaseDefaultPic(url) && !Lyrics.IsNeteaseDefaultPic(null),
            "网易云没传头像的剪影、白底占位图认作没有图");
        Check(ArtCollector.NoneKey("ar|x", "ne:1") != ArtCollector.NoneKey("ar|x", "ne:2"), "「没有」按来源分开记");

        Check(ArtCollector.NameKey("Ai") != ArtCollector.NameKey("Aimer"), "认歌手比相等不比包含：Ai 不是 Aimer");
        Check(ArtCollector.NameKey("YOASOBI (ヨアソビ)") == ArtCollector.NameKey("YOASOBI")
            && ArtCollector.NameKey("周杰伦（Jay Chou）") == ArtCollector.NameKey("周杰伦"), "QQ 歌手名末尾的括号注音去掉再比");
        Check(ArtCollector.NameKey("周杰倫") == ArtCollector.NameKey("周杰伦"), "繁简当同一个名字");

        // 榜单行的键必须和收集器存图用的一致，否则存了也显示不出来
        var v = StatsReport.Build(
        [
            Seg(At(3, 10, 8, 0), 300, "晴天", "周杰伦", 1, al: "叶惠美", aa: "周杰伦"),
            Seg(At(3, 10, 9, 0), 200, "东风破", "周杰伦", 1, lal: "叶惠美"),
            Seg(At(3, 10, 10, 0), 250, "千里之外", "周杰伦/费玉清", 1, al: "依然范特西"),
            Seg(At(3, 10, 11, 0), 100, "Lemon", "米津玄師", 1),
        ], SampleNow).Ranges[StatsRange.All];
        StatsRankRow SongRow(string t) => v.Songs.ByPlays.Single(r => r.Title == t);
        var yeHuiMei = StatsReport.AlbumArtKey("叶惠美", null, "周杰伦", "周杰伦");
        Check(SongRow("晴天").Art == yeHuiMei && SongRow("东风破").Art == StatsReport.AlbumArtKey(null, "叶惠美", null, "周杰伦")
            && SongRow("晴天").Art == SongRow("东风破").Art,
            $"有专辑的歌用专辑封面的键，播放器报的和歌词库登记的同一张（{SongRow("晴天").Art}）");
        Check(SongRow("千里之外").Art == StatsReport.AlbumArtKey("依然范特西", null, null, "周杰伦/费玉清"),
            "合唱歌的专辑键和收集器算的一致");
        Check(SongRow("Lemon").Art == StatsReport.SongArtKey("Lemon", "米津玄師") && SongRow("Lemon").Kind == StatsArtKind.Song,
            "没专辑的歌按歌存封面");
        Check(v.Albums.ByPlays.Single(r => r.Title == "叶惠美") is { Kind: StatsArtKind.Album } al && al.Art == yeHuiMei,
            "专辑榜的键同歌曲榜");
        var fei = v.Artists.ByPlays.Single(r => r.Title == "费玉清");
        Check(fei.Kind == StatsArtKind.Artist && StatsReport.SplitArtists("周杰伦/费玉清").Select(StatsReport.ArtistArtKey).Contains(fei.Art),
            "歌手榜的键和收集器拆出来的歌手一致");
        Check(v.Players.All(r => r.Kind == StatsArtKind.None && r.Art == null), "播放器榜不配图");

        var dir = Path.Combine(root, "art");
        ArtCache.DirOverride = dir;
        try
        {
            var png = SwatchPng(Colors.SteelBlue, 400, 200);
            Check(!ArtCache.Save("k1", "{\"code\":200}"u8.ToArray()) && !ArtCache.Has("k1"), "下回来的是 JSON → 不存");
            Check(ArtCache.Save("k1", png) && ArtCache.Has("k1"), "存一张 PNG");
            var back = ArtCache.Load("k1", 64);
            Check(back is { PixelWidth: 64, IsFrozen: true }, $"按需要的宽度读回、已冻结（{back?.PixelWidth}）");
            var big = ArtCache.Load("k1", 0);
            Check(big is { PixelWidth: 300, PixelHeight: 150 }, $"存的时候缩到 300px 以内（{big?.PixelWidth}×{big?.PixelHeight}）");
            ArtCache.MarkNone("k2");
            Check(ArtCache.IsNone("k2") && !ArtCache.IsNone("k1"), "记下「查过没有」");

            // 读到就算用过一次（淘汰按最久没用）；存图时顺手清掉过期的「没有」和残留的临时文件
            var jpg = Directory.GetFiles(dir, "*.jpg").Single();
            var old = DateTime.UtcNow.AddDays(-30);
            File.SetLastWriteTimeUtc(jpg, old);
            ArtCache.Load("k1", 64);
            Check(File.GetLastWriteTimeUtc(jpg) > old.AddDays(1), "读一次图更新它的修改时间");
            var none = Directory.GetFiles(dir, "*.none").Single();
            File.SetLastWriteTimeUtc(none, old);
            var tmp = Path.Combine(dir, "stale.jpg.tmp");
            File.WriteAllBytes(tmp, [1]);
            File.SetLastWriteTimeUtc(tmp, old);
            ArtCache.Save("k3", png);
            Check(!File.Exists(none) && !File.Exists(tmp) && ArtCache.Has("k1") && ArtCache.Has("k3"),
                "存图时清掉过期的「没有」和残留的临时文件，图留着");
            ArtCache.Clear();
            ArtCache.Save("k1", png);
            File.WriteAllBytes(Directory.GetFiles(dir, "*.jpg").Single(), [0xFF, 0xD8, 1, 2, 3, 4, 5, 6, 7, 8]);
            Check(ArtCache.Load("k1", 64) == null && !ArtCache.Has("k1"), "坏文件读不出来就删掉");
            ArtCache.Save("k1", png);
            ArtCache.Clear();
            Check(Directory.GetFiles(dir).Length == 0, "清空");
        }
        finally
        {
            ArtCache.DirOverride = null;
        }
    }

    /// <summary>程序画一张 PNG 当作下回来的图：左上原色、往右下渐暗，截图里看得出模糊底的效果。</summary>
    private static byte[] SwatchPng(Color c, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var k = 1 - 0.6 * (x + y) / (w + h);
                var i = (y * w + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = ((byte)(c.B * k), (byte)(c.G * k), (byte)(c.R * k), 255);
            }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static void FilterTests()
    {
        Console.WriteLine("[播放器筛选]");
        List<ListenSegment> segs =
        [
            Seg(At(3, 10, 20, 0), 600, "晴天", "周杰伦", 1),
            Seg(At(3, 10, 21, 0), 300, "Lemon", "米津玄師", 1, src: "QQMusic.exe"),
            Seg(At(3, 11, 8, 0), 200, "七里香", "周杰伦", 1, src: "903DB504.QQMusic_a99ra4d2cbcxa!App"), // 商店版
            Seg(At(3, 11, 9, 0), 100, "Lemon", "米津玄師", 1, src: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
        ];
        var all = StatsReport.Build(segs, SampleNow);
        var qq = StatsReport.Build(segs, SampleNow, "QQ 音乐");
        var unknown = StatsReport.Build(segs, SampleNow, "不存在的播放器");
        Check(all.PlayerOptions.SequenceEqual(["网易云音乐", "QQ 音乐", "Spotify"]) && qq.PlayerOptions.SequenceEqual(all.PlayerOptions),
            $"播放器选项来自全部记录、按时长排，筛选后不变（{string.Join("、", qq.PlayerOptions)}）");
        Check(qq.Player == "QQ 音乐" && qq.SegmentCount == 2 && Near(qq.TotalS, 500)
              && qq.Ranges[StatsRange.All].Songs.ByPlays.Select(r => r.Title).ToHashSet().SetEquals(["Lemon", "七里香"])
              && qq.FirstDay == new DateTime(2026, 3, 10),
            $"「QQ 音乐」把桌面版和商店版合起来算（{qq.SegmentCount} 段、{qq.TotalS} 秒）");
        Check(unknown.Player == null && Near(unknown.TotalS, 1200), "认不出的播放器名当没筛选");
    }

    private static void YearTests()
    {
        Console.WriteLine("[年度报告]");
        var now = SampleNow;
        var segs = Sample();
        var y = StatsReport.BuildYear(segs, 2026, now);
        var yr = StatsReport.Build(segs, now).Ranges[StatsRange.Year];
        Check(Near(y.TotalS, 5230) && y.Plays == 9 && y.Songs == 5 && y.Artists == 3 && y.ActiveDays == 5 && y.Albums == 0
              && y.NewSongs == null,
            $"2026 年：{y.TotalS} 秒、{y.Plays} 次、{y.Songs} 首、{y.Artists} 位歌手、{y.ActiveDays} 天，新歌 {y.NewSongs?.ToString() ?? "不算"}（记录今年才开始）");
        Check(Rows(y.TopSongs) == Rows(yr.Songs.ByPlays.Take(5)) && Rows(y.TopArtists) == Rows(yr.Artists.ByPlays.Take(5))
              && y.PeakText == yr.PeakText && y.HeatText == yr.HeatText
              && yr.Summary.StartsWith($"今年你听了 {StatsReport.FormatDuration(y.TotalS)}，播放 {y.Plays} 次，共 {y.Songs} 首歌", StringComparison.Ordinal),
            "年报与首页「今年」的数字一致");
        Check(y.Months.Count == 12 && Near(y.Months[0].Value, 600) && Near(y.Months[2].Value, 4630)
              && y.Months.Count(m => m.Highlight) == 1 && y.Months[2].Highlight,
            "12 个月的柱子，听得最多的月份高亮");
        Check(y.Highlights.Select(h => h.Title).SequenceEqual(["听歌最多的一天", "单日循环纪录", "最晚的一次夜猫子", "最长连续听歌", "这一年的第一首歌"])
              && y.Highlights[^1] is { Value: "1 月 5 日 周一", Detail: "《晴天》 · 周杰伦" },
            $"亮点（{string.Join("；", y.Highlights.Select(h => $"{h.Title} {h.Value} {h.Detail}"))}）");
        Check(StatsReport.BuildYear(segs, 2025, now).Empty, "没有记录的年份是空报告");

        List<ListenSegment> cross =
        [
            Seg(new DateTimeOffset(2025, 6, 1, 20, 0, 0, Cst), 300, "晴天", "周杰伦", 1),
            Seg(new DateTimeOffset(2025, 12, 31, 23, 50, 0, Cst), 1200, "七里香", "周杰伦", 1),
            Seg(At(1, 2, 20, 0), 300, "晴天", "周杰伦", 1),
            Seg(At(1, 3, 20, 0), 200, "Lemon", "米津玄師", 1),
            Seg(new DateTimeOffset(2027, 1, 1, 20, 0, 0, Cst), 600, "未来的歌", "某人", 1),
        ];
        var y25 = StatsReport.BuildYear(cross, 2025, now);
        var y26 = StatsReport.BuildYear(cross, 2026, now);
        Check(Near(y25.TotalS, 900) && y25.Plays == 2 && Near(y26.TotalS, 1100) && y26.Plays == 2 && y26.Songs == 3,
            $"跨年夜的片段按钟点拆给两年，次数归开始的那年（2025：{y25.TotalS} 秒 {y25.Plays} 次；2026：{y26.TotalS} 秒 {y26.Plays} 次）");
        Check(y25.NewSongs == null && y26.NewSongs == 1 && y26.Highlights.Any(h => h is { Title: "新发现", Value: "1 首" }),
            $"新歌只算这一年第一次听的，跨年夜那首去年就听过了（{y26.NewSongs}）");
        Check(StatsReport.Years(cross, now).SequenceEqual([2026, 2025]), "Years：新的在前，不含将来的年份");

        Check(YearReportWindow.DefaultYear([2026, 2025], new DateTime(2026, 1, 10)) == 2025
              && YearReportWindow.DefaultYear([2026, 2025], now) == 2026
              && YearReportWindow.DefaultYear([2026], new DateTime(2026, 1, 10)) == 2026
              && YearReportWindow.DefaultYear([2024], now) == 2024
              && YearReportWindow.DefaultYear([], now) == 2026,
            "默认年份：一月看去年、否则看今年、今年没记录看最近有记录的一年");
    }

    private static void HelperTests()
    {
        Console.WriteLine("[小工具]");
        Check(ListenRecorder.PlaysFor(29.9, 0) == 0 && ListenRecorder.PlaysFor(30, 0) == 1 && ListenRecorder.PlaysFor(5000, 0) == 1,
            "PlaysFor：时长未知时满 30 秒记 1 次，之后不再加");
        Check(ListenRecorder.PlaysFor(19.9, 40) == 0 && ListenRecorder.PlaysFor(20, 40) == 1 && ListenRecorder.PlaysFor(60, 40) == 2,
            "PlaysFor：短歌听满半首算 1 次，每多一整首再加 1");
        Check(ListenRecorder.PlaysFor(29, 8) == 0 && ListenRecorder.PlaysFor(30, 8) == 1, "PlaysFor：短于 10 秒的时长当未知");

        string[] got =
        [
            StatsReport.FormatDuration(0), StatsReport.FormatDuration(30), StatsReport.FormatDuration(60),
            StatsReport.FormatDuration(3600), StatsReport.FormatDuration(3660), StatsReport.FormatDuration(100 * 3600 + 60),
        ];
        Check(got.SequenceEqual(["0 分", "不到 1 分钟", "1 分", "1 小时", "1 小时 1 分", "100 小时"]),
            $"FormatDuration：{string.Join(" / ", got)}");

        string[] names =
        [
            StatsReport.PlayerName("cloudmusic.exe"),
            StatsReport.PlayerName("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"),
            StatsReport.PlayerName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            StatsReport.PlayerName("Contoso.CoolPlayer_abc123!App"),
            StatsReport.PlayerName("SomePlayer.exe"),
            StatsReport.PlayerName(""),
            StatsReport.PlayerName("308046B0AF4A39CB"),
        ];
        Check(names.SequenceEqual(["网易云音乐", "媒体播放器", "Spotify", "CoolPlayer", "SomePlayer", "未知播放器", "Firefox"]),
            $"PlayerName：{string.Join(" / ", names)}");

        (string Src, string Ar, string? Ty, bool Want)[] songs =
        [
            ("cloudmusic.exe", "周杰伦", null, true),   // 旧版网易云不报类型
            ("cloudmusic.exe", "周杰伦", "video", false),
            ("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "某人", "image", false),
            ("WeChatAppEx.exe", "", "music", false),
            ("WeChatAppEx.exe", "  ", "music", false),
            ("Chrome", "", "", false),
            ("Brave", "", "music", false),
            ("QQBrowser.exe", "", "music", false),
            ("308046B0AF4A39CB", "", "music", false),
            ("MSEdge", "米津玄師", "music", true),
            ("foobar2000.exe", "", "music", true),
            ("", "", null, true),
        ];
        var wrong = songs.Where(x => StatsReport.IsSong(x.Src, x.Ar, x.Ty) != x.Want).Select(x => $"{x.Src}/{x.Ar}/{x.Ty}").ToList();
        Check(wrong.Count == 0, $"IsSong：网页里歌手为空的、标明视频图片的不算歌（判错的：{string.Join("、", wrong)}）");
    }

    private static void ShareImageTests()
    {
        Console.WriteLine("[分享图]");
        // 红块带 Margin 放进 Grid 里排好版：VisualBrush 要从这个偏移处取，否则整张图错开一个 Margin
        static Border Block(double w, double h, Thickness margin)
        {
            var block = new Border
            {
                Width = w,
                Height = h,
                Margin = margin,
                Background = Brushes.Red,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var host = new Grid();
            host.Children.Add(block);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            return block;
        }
        static bool Red(BitmapSource img, int x, int y)
        {
            var px = new byte[4];
            img.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            return px[2] > 240 && px[1] < 15 && px[0] < 15; // Pbgra32：B、G、R、A
        }

        var block = Block(100, 50, new Thickness(30, 20, 30, 20));
        var img = ShareImage.Render(block, 2, 10);
        Check(img.PixelWidth == 240 && img.PixelHeight == 140, $"尺寸 =（内容 + 两边留白）× 倍率（{img.PixelWidth}×{img.PixelHeight}）");
        Check(Red(img, 21, 21) && Red(img, 218, 118) && !Red(img, 18, 18) && !Red(img, 222, 122) && !Red(img, 5, 5),
            "内容不随元素自身的 Margin 错位：左上、右下角都落在留白内侧");
        var titled = ShareImage.Render(block, 2, 10, "标题", "副标题");
        var h = titled.PixelHeight;
        Check(titled.PixelWidth == 240 && h > 140 && Red(titled, 218, h - 24) && !Red(titled, 218, h - 17) && !Red(titled, 218, 22),
            $"有标题时内容往下让，底边留白不变（{titled.PixelWidth}×{h}）");
        Check(ShareImage.EncodePng(img).Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "编码成 PNG");
        var tall = ShareImage.Render(Block(100, 5000, new Thickness(0)), 2, 10);
        var side = Math.Max(tall.PixelWidth, tall.PixelHeight);
        Check(side is >= 7990 and <= 8001, $"太长的图按最长边约 8000 像素缩小（{tall.PixelWidth}×{tall.PixelHeight}）");
    }

    private static void PerfTest()
    {
        Console.WriteLine("[性能]");
        var now = SampleNow;
        var segs = Synthetic(now, days: 3650);
        var sw = Stopwatch.StartNew();
        var snap = StatsReport.Build(segs, now);
        var buildMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var qq = StatsReport.Build(segs, now, "QQ 音乐");
        var filterMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var year = StatsReport.BuildYear(segs, now.Year, now);
        var yearMs = sw.ElapsedMilliseconds;
        Check(!snap.Empty && buildMs < 3000, $"十年合成数据 {segs.Count} 段，汇总用时 {buildMs} ms");
        Check(qq.Player == "QQ 音乐" && !qq.Empty && filterMs < 3000, $"只看 QQ 音乐，汇总用时 {filterMs} ms");
        Check(!year.Empty && yearMs < 3000, $"年度报告用时 {yearMs} ms");
    }

    /// <summary>合成一份像样的听歌记录：晚上听得多、几首歌特别常听、偶尔单曲循环、偶尔熬夜。
    /// 固定种子，每次生成的都一样。</summary>
    internal static List<ListenSegment> Synthetic(DateTime now, int days = 75)
    {
        var rng = new Random(42);
        (string Ti, string Ar, double Dur, string Al)[] songs =
        [
            ("晴天", "周杰伦", 269, "叶惠美"), ("Lemon", "米津玄師", 255, "Lemon"), ("夜に駆ける", "YOASOBI", 261, "THE BOOK"),
            ("富士山下", "陈奕迅", 259, "What's Going On…?"), ("七里香", "周杰伦", 299, "七里香"),
            ("Blinding Lights", "The Weeknd", 200, "After Hours"), ("海阔天空", "Beyond", 326, "乐与怒"),
            ("アイドル", "YOASOBI", 213, "アイドル"), ("小幸运", "田馥甄", 265, "小幸运"),
            ("Señorita", "Shawn Mendes / Camila Cabello", 191, "Señorita"), ("平凡之路", "朴树", 301, "猎户星座"),
            ("稻香", "周杰伦", 223, "魔杰座"), ("Viva La Vida", "Coldplay", 242, "Viva La Vida or Death and All His Friends"),
            ("后来", "刘若英", 341, "我等你"), ("光年之外", "G.E.M.邓紫棋", 235, "光年之外"),
            ("Someone Like You", "Adele", 285, "21"), ("孤勇者", "陈奕迅", 256, "孤勇者"),
            ("The Sound of Silence", "Simon & Garfunkel", 185, "Sounds of Silence"), ("起风了", "买辣椒也用券", 325, "起风了"),
            ("Counting Stars", "OneRepublic", 257, "Native"),
        ];
        (string Src, double Weight)[] players =
        [
            ("cloudmusic.exe", 60), ("QQMusic.exe", 25), ("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", 10), ("msedge.exe", 5),
        ];
        // 各钟点开始听歌的权重：午休和晚上多，凌晨两三点偶尔
        double[] hourWeight = [3, 2, 1, 0, 0, 0, 0, 2, 3, 3, 3, 4, 5, 4, 3, 3, 4, 4, 5, 6, 8, 9, 8, 5];
        var songWeight = songs.Select((_, i) => 1 / Math.Pow(i + 1, 0.9)).ToArray();
        var playerWeight = players.Select(p => p.Weight).ToArray();

        static int Pick(Random rng, IReadOnlyList<double> weights)
        {
            var x = rng.NextDouble() * weights.Sum();
            for (var i = 0; i < weights.Count; i++)
                if ((x -= weights[i]) < 0) return i;
            return weights.Count - 1;
        }

        var list = new List<ListenSegment>();
        for (var d = days - 1; d >= 0; d--)
        {
            var day = now.Date.AddDays(-d);
            if (rng.NextDouble() < 0.15) continue; // 有些天不听
            var sessions = 1 + rng.Next(3);
            for (var k = 0; k < sessions; k++)
            {
                var at = day.AddHours(Pick(rng, hourWeight)).AddMinutes(rng.Next(60));
                var src = players[Pick(rng, playerWeight)].Src;
                var n = 2 + rng.Next(12);
                for (var i = 0; i < n && at < now; i++)
                {
                    var song = songs[Pick(rng, songWeight)];
                    var loops = rng.NextDouble() < 0.05 ? 3 + rng.Next(4) : 1;
                    var listened = song.Dur * loops * (rng.NextDouble() < 0.15 ? 0.3 : 1); // 有时听一半就切走
                    var start = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Unspecified),
                        TimeZoneInfo.Local.GetUtcOffset(at));
                    // 近 60 天按新版本记：网易云、Spotify 报专辑和专辑歌手，QQ 音乐只报专辑，
                    // 浏览器什么都不报、只有歌词库匹配到的专辑；更早的按旧版本记，没有专辑。
                    // 专辑只由日期和播放器推出来、不另取随机数，随机序列和以前一样
                    var v2 = d < 60;
                    var reportsAlbum = v2 && src != "msedge.exe";
                    list.Add(new ListenSegment
                    {
                        T = start.ToUnixTimeSeconds(),
                        O = (int)start.Offset.TotalMinutes,
                        S = Math.Round(listened, 1),
                        Ti = song.Ti,
                        Ar = song.Ar,
                        Src = src,
                        P = ListenRecorder.PlaysFor(listened, song.Dur),
                        V = v2 ? 2 : 0,
                        Al = reportsAlbum ? song.Al : null,
                        Aa = reportsAlbum && src != "QQMusic.exe" ? song.Ar : null,
                        Lal = v2 && src == "msedge.exe" ? song.Al : null,
                    });
                    at = at.AddSeconds(listened + rng.Next(2, 10));
                }
            }
        }
        return list;
    }

    /// <summary>对真实记录输出文字版报告。</summary>
    private static int Report()
    {
        var sw = Stopwatch.StartNew();
        var segs = StatsStore.ReadAll(StatsStore.AllDirs);
        var readMs = sw.ElapsedMilliseconds;
        var now = DateTime.Now;
        var snap = StatsReport.Build(segs, now);
        Console.WriteLine($"目录：{string.Join("；", StatsStore.AllDirs)}");
        Console.WriteLine($"片段 {segs.Count} 段，读盘 {readMs} ms，汇总 {sw.ElapsedMilliseconds - readMs} ms");
        // v2 字段的覆盖情况：部署后拿真实数据对账，看哪些播放器报了专辑、歌词 id 拿到没有
        var v2 = segs.Where(s => s.V >= 2).ToList();
        if (v2.Count > 0)
        {
            string Pct(Func<ListenSegment, bool> f) => $"{v2.Count(f) * 100 / v2.Count}%";
            Console.WriteLine($"v2 片段 {v2.Count} 段：带专辑 {Pct(s => s.Al != null)}，带歌词库专辑 {Pct(s => s.Lal != null)}，"
                + $"带歌词 id {Pct(s => s.Lid != null)}，时长已知 {Pct(s => s.D > 0)}（其中来自歌词库 {Pct(s => s.Dl)}）");
            Console.WriteLine("结束原因：" + string.Join("、", v2.GroupBy(s => s.E ?? "累计中")
                .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
            foreach (var g in v2.GroupBy(s => StatsReport.PlayerName(s.Src)))
                Console.WriteLine($"  {g.Key}：{g.Count()} 段，专辑 {g.Count(s => s.Al != null)}，类型 {string.Join("/", g.Select(s => s.Ty ?? "-").Distinct())}，"
                    + $"随机 {string.Join("/", g.Select(s => s.Sh?.ToString() ?? "-").Distinct())}，循环 {string.Join("/", g.Select(s => s.Rp ?? "-").Distinct())}");
        }
        if (snap.Empty)
        {
            Console.WriteLine("还没有听歌记录");
            return 0;
        }
        Console.WriteLine();
        foreach (var f in snap.Overview) Console.WriteLine($"{f.Title}：{f.Value}（{f.Detail}）");
        Console.WriteLine($"播放器：{string.Join("、", snap.PlayerOptions)}");
        foreach (var (range, v) in snap.Ranges)
        {
            Console.WriteLine($"\n== {range} ==\n{v.Summary}");
            if (v.PeakText.Length > 0) Console.WriteLine(v.PeakText);
            if (v.HeatText.Length > 0) Console.WriteLine(v.HeatText);
            foreach (var r in v.Songs.ByPlays.Take(5)) Console.WriteLine($"  歌 {r.Rank}. {r.Title} - {r.Sub}  {r.Right}");
            foreach (var r in v.Artists.ByPlays.Take(5)) Console.WriteLine($"  手 {r.Rank}. {r.Title}（{r.Sub}）  {r.Right}");
            foreach (var r in v.Albums.ByPlays.Take(5)) Console.WriteLine($"  专 {r.Rank}. {r.Title}（{r.Sub}）  {r.Right}");
            if (v.AlbumNote.Length > 0) Console.WriteLine($"  专辑：{v.AlbumNote}");
            foreach (var r in v.NewSongs.ByPlays.Take(5)) Console.WriteLine($"  新 {r.Rank}. {r.Title} - {r.Sub}  {r.Right}");
            if (v.NewSongsNote.Length > 0) Console.WriteLine($"  新歌：{v.NewSongsNote}");
            foreach (var r in v.Players) Console.WriteLine($"  器 {r.Title}  {r.Right}");
        }
        Console.WriteLine();
        foreach (var f in snap.Facts) Console.WriteLine($"{f.Title}：{f.Value}（{f.Detail}）");
        foreach (var year in StatsReport.Years(segs, now))
        {
            var y = StatsReport.BuildYear(segs, year, now);
            Console.WriteLine($"\n== {year} 年度报告 ==\n{StatsReport.FormatDuration(y.TotalS)}，播放 {y.Plays} 次，"
                + $"{y.Songs} 首歌、{y.Artists} 位歌手、{y.Albums} 张专辑，听歌 {y.ActiveDays} 天，新歌 {y.NewSongs?.ToString() ?? "不算"}");
            foreach (var h in y.Highlights) Console.WriteLine($"  {h.Title}：{h.Value}（{h.Detail}）");
        }
        return 0;
    }

#pragma warning disable WPF0001 // Application.ThemeMode 在 .NET 10 里仍标着实验性
    /// <summary>统计窗口、分享图、年度报告各出浅色、深色，外加一张播放器筛选。窗口放在屏幕外、不激活不进任务栏；
    /// 每一步都等到 ContextIdle（布局、渲染都做完）再截。异常要自己兜：
    /// App 的全局兜底会把它吞掉，没人调 Shutdown 进程就挂在那里。</summary>
    public static void Shot(string[] args)
    {
        var app = Application.Current;
        void Fail(Exception ex)
        {
            Console.WriteLine(ex);
            app.Shutdown(1);
        }
        try
        {
            if (args.Length < 1)
            {
                Console.WriteLine("用法：--stats-test shot <目录> [real]");
                app.Shutdown(1);
                return;
            }
            var dir = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(dir);
            string Png(string name) => Path.Combine(dir, name + ".png");
            var now = DateTime.Now;
            var real = args.Length >= 2 && args[1] == "real";
            // 合成 400 天：年度报告有两个年份可选，近 60 天带专辑
            IReadOnlyList<ListenSegment> segs = real ? StatsStore.ReadAll(StatsStore.AllDirs) : Synthetic(now, days: 400);
            var options = StatsReport.Build(segs, now).PlayerOptions;
            var filter = options.Count > 1 ? options[1] : null; // 筛选截图看排第二的播放器
            var year = YearReportWindow.DefaultYear(StatsReport.Years(segs, now), now);
            // 合成数据的配图也是合成的：放在临时目录，三行里两行给色块、一行留着看占位
            string? artDir = null;
            if (!real)
            {
                artDir = Path.Combine(Path.GetTempPath(), $"TaskbarLyrics-art-shot-{Environment.ProcessId}");
                ArtCache.DirOverride = artDir;
                SeedArt(segs, now, year);
            }
            void Done(int code)
            {
                ArtCache.DirOverride = null;
                if (artDir != null)
                    try { Directory.Delete(artDir, true); } catch { /* 临时目录，删不掉无所谓 */ }
                app.Shutdown(code);
            }

            app.ThemeMode = ThemeMode.Light;
            var win = Offscreen(new StatsWindow(null));
            var yearWin = Offscreen(new YearReportWindow());
            win.Load(segs, now);
            yearWin.Load(segs, now, year);

            void Later(Action step) => win.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                try { step(); }
                catch (Exception ex) { Fail(ex); }
            });
            Later(() =>
            {
                win.SaveShot(Png("stats-light"));
                win.SaveShareShot(Png("stats-share-light"));
                yearWin.SaveShot(Png("year-light"));
                yearWin.SaveWindowShot(Png("year-window-light"));
                if (filter != null) win.Load(segs, now, filter);
                Later(() =>
                {
                    if (filter != null) win.SaveShot(Png("stats-filtered-light"));
                    win.Load(segs, now);
                    app.ThemeMode = ThemeMode.Dark;
                    Later(() =>
                    {
                        win.SaveShot(Png("stats-dark"));
                        yearWin.SaveShot(Png("year-dark"));
                        yearWin.SaveWindowShot(Png("year-window-dark"));
                        Console.WriteLine($"已保存到 {dir}（年度报告是 {year} 年{(filter == null ? "，只有一个播放器、没出筛选图" : $"，筛选的是「{filter}」")}）");
                        yearWin.Close();
                        win.Close();
                        Done(0);
                    });
                });
            });
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
#pragma warning restore WPF0001

    private static void SeedArt(IReadOnlyList<ListenSegment> segs, DateTime now, int year)
    {
        Color[] palette =
        [
            Color.FromRgb(0xE0, 0x6C, 0x4F), Color.FromRgb(0x3A, 0x7C, 0xA5), Color.FromRgb(0x6A, 0x9F, 0x58),
            Color.FromRgb(0x9B, 0x5D, 0xB0), Color.FromRgb(0xD9, 0xA4, 0x41), Color.FromRgb(0x45, 0x4E, 0x6B),
        ];
        var snap = StatsReport.Build(segs, now);
        var y = StatsReport.BuildYear(segs, year, now);
        var keys = y.TopSongs.Concat(y.TopArtists).Concat(y.TopAlbums)
            .Concat(snap.Ranges.Values.SelectMany(v => v.Songs.ByPlays.Concat(v.Songs.ByTime).Concat(v.Artists.ByPlays)
                .Concat(v.Artists.ByTime).Concat(v.Albums.ByPlays).Concat(v.Albums.ByTime).Concat(v.NewSongs.ByPlays)))
            .Select(r => r.Art).OfType<string>().Distinct().ToList();
        for (var i = 0; i < keys.Count; i++)
            if (i % 3 != 2) ArtCache.Save(keys[i], SwatchPng(palette[i % palette.Length], 300, 300));
    }

    private static T Offscreen<T>(T w) where T : Window
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000;
        w.Top = -32000;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        return w;
    }
}
