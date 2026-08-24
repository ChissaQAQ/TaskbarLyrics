// 配置读写：config.json 与 Python 版同 schema（移植自 settings.py），存 exe 同目录。
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TaskbarLyrics;

public sealed class AppConfig
{
    // 外观
    [JsonPropertyName("font_family")] public string FontFamily { get; set; } = "Microsoft YaHei UI";
    [JsonPropertyName("font_size")] public int FontSize { get; set; } = 12;            // 原文字号
    [JsonPropertyName("font_bold")] public bool FontBold { get; set; }               // 原文半粗（译文保持常规）
    // auto：跟随系统任务栏明暗自动取色（深色任务栏白字、浅色任务栏近黑字）；
    // custom：用下面两个 hex。默认 auto——浅色任务栏上白字几乎看不见，
    // 而绝大多数人不会主动去设置里改颜色，默认就得是对的
    [JsonPropertyName("text_color_mode")] public string TextColorMode { get; set; } = "auto";
    [JsonPropertyName("text_color")] public string TextColor { get; set; } = "#FFFFFF";
    [JsonPropertyName("trans_color")] public string TransColor { get; set; } = "#C8C8C8";
    [JsonPropertyName("shadow")] public bool Shadow { get; set; } = true;              // 文字阴影
    [JsonPropertyName("width")] public int Width { get; set; } = 280;                  // 歌词区最大宽度（紧凑布局下内容更窄时收缩，超出才缩字号）
    [JsonPropertyName("text_align")] public string TextAlign { get; set; } = "left"; // center | left（配封面时左对齐更整齐）
    [JsonPropertyName("show_controls")] public bool ShowControls { get; set; } = true; // 悬停播放控制按钮
    // 位置
    [JsonPropertyName("mode")] public string Mode { get; set; } = "taskbar";           // taskbar | floating
    [JsonPropertyName("position")] public string Position { get; set; } = "custom";    // tray_left | left | center | right | custom
    [JsonPropertyName("auto_position")] public bool AutoPosition { get; set; } = true; // 自动避让任务栏元素（覆盖 position）
    [JsonPropertyName("auto_side")] public string AutoSide { get; set; } = "right";    // 避让停靠侧：left | right（优先待在哪半边）
    [JsonPropertyName("auto_align")] public string AutoAlign { get; set; } = "left";   // 空档内停靠对齐：left | right | center
    [JsonPropertyName("x_offset")] public int? XOffset { get; set; }                   // position=custom 时任务栏内 x（左缘锚点）
    [JsonPropertyName("x_center")] public int? XCenter { get; set; }                   // 居中对齐时的中心锚点（任务栏内 x）
    [JsonPropertyName("float_x")] public int? FloatX { get; set; }                     // 浮动模式屏幕坐标（左缘锚点）
    [JsonPropertyName("float_cx")] public int? FloatCx { get; set; }                   // 居中对齐时浮动窗中心锚点（屏幕坐标）
    [JsonPropertyName("float_y")] public int? FloatY { get; set; }
    [JsonPropertyName("monitor")] public int Monitor { get; set; } = 0;
    [JsonPropertyName("locked")] public bool Locked { get; set; }                      // 锁定后鼠标穿透
    [JsonPropertyName("show_cover")] public bool ShowCover { get; set; } = true;       // 显示专辑封面
    // 歌词
    [JsonPropertyName("second_line")] public string SecondLine { get; set; } = "translation"; // translation | romaji | off
    [JsonPropertyName("karaoke")] public bool Karaoke { get; set; } = true;            // 逐字歌词
    [JsonPropertyName("offset_ms")] public int OffsetMs { get; set; } = 0;             // 歌词时间偏移（正为提前：posMs 加得多，查到更靠后的行）
    // 行为
    [JsonPropertyName("hide_on_fullscreen")] public bool HideOnFullscreen { get; set; } = true;
    // 更新
    [JsonPropertyName("update_check")] public bool UpdateCheck { get; set; } = true;   // 启动时自动检查新版本
    // 上次成功检查更新的 unix 秒。只用于给启动检查节流：GitHub 匿名接口每小时 60 次
    // 且按出口 IP 算，频繁重启（或共用出口的多台机器）会白白耗掉配额
    [JsonPropertyName("last_update_check")] public long LastUpdateCheck { get; set; }
    // 播放源
    [JsonPropertyName("player_source")] public string PlayerSource { get; set; } = "auto"; // auto | netease | others
    [JsonPropertyName("player_blocklist")] public List<string> PlayerBlocklist { get; set; } = new() { "chrome", "msedge", "firefox" };

    // 保留 Python 版里未知的扩展键，存盘时不丢
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>当前使用的配置文件路径。首选 exe 同目录（便携、删掉即恢复默认），
    /// 那里写不进去时会切到 <see cref="FallbackPath"/>。</summary>
    public static string ConfigPath { get; private set; } = PrimaryPath;

    /// <summary>首选位置：exe 同目录。</summary>
    private static string PrimaryPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>exe 同目录只读时（装在 Program Files、或从只读介质运行）的退路。</summary>
    private static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarLyrics", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 对应 Python ensure_ascii=False
    };

    /// <summary>读配置。两处（exe 同目录 / %AppData% 退路）都可能有内容，
    /// 取较新的那份；它坏了再退到另一份。<see cref="ConfigPath"/> 随之定下，
    /// 之后的存盘就写这一处。</summary>
    public static AppConfig Load()
    {
        var first = PickReadPath();
        var second = first == PrimaryPath ? FallbackPath : PrimaryPath;
        if (TryLoadFrom(first, out var cfg)) { ConfigPath = first; return cfg; }
        // 首选那份坏了（已被隔离成 .bad），另一处哪怕旧一点也比全默认值强
        if (TryLoadFrom(second, out cfg)) { ConfigPath = second; return cfg; }
        // 两处都没有（首次运行）或都坏了：用默认值，并按便携优先写回 exe 同目录，
        // 那里写不进去时 Save 会自己切到退路
        ConfigPath = PrimaryPath;
        return new AppConfig();
    }

    /// <summary>决定这次从哪读。
    ///
    /// 必须比较两处的新旧，不能固定优先 exe 同目录：Save 在 exe 同目录写不进去时
    /// 会转存 %AppData%，而原先的 Load 只在 exe 同目录「不存在」文件时才看退路。
    /// 读写规则一不对称，存进去的和读回来的就不是同一个文件——现象是
    /// 「设置改完当场生效、电脑重启后又变回旧值」，而且全程不留痕迹
    /// （转存退路成功时不提示，写失败的日志也只记第一次）。
    ///
    /// 触发它不需要权限长期不足：OneDrive 同步、杀软扫描、备份工具短暂锁住
    /// config.json 就够了。而 ConfigPath 一旦切到退路，本次运行就不再回头试
    /// exe 同目录，于是那一次抖动之后的所有改动都注定在重启时丢失。</summary>
    private static string PickReadPath()
    {
        var primary = PrimaryPath;
        var fallback = FallbackPath;
        try
        {
            var ff = new FileInfo(fallback);
            if (!ff.Exists) return primary;
            var pf = new FileInfo(primary);
            if (!pf.Exists) return fallback;
            // 都有：谁新用谁。一样新时选 exe 同目录，保持便携语义
            return ff.LastWriteTimeUtc > pf.LastWriteTimeUtc ? fallback : primary;
        }
        catch
        {
            return primary; // 取不到时间戳（路径异常）就按首选来
        }
    }

    /// <summary>试着从一处读。文件不存在返回 false（不算故障）；
    /// 内容坏了则记日志并把它隔离成 .bad 再返回 false。
    ///
    /// 隔离这一步是必须的：解析失败原先只是静默回到默认值，而回默认只发生在内存里，
    /// 紧接着任何一次存盘（拖动窗口松手、检查更新写时间戳）就会把整份默认值
    /// 覆盖到原文件上，用户攒下的设置从此不可恢复。改名留证之后，
    /// 坏文件既不会被覆盖，也让下一次 Load 有机会退到另一处那份旧配置。</summary>
    private static bool TryLoadFrom(string path, out AppConfig cfg)
    {
        cfg = null!;
        if (!File.Exists(path)) return false;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidDataException("配置内容不是 JSON 对象");
            // 旧配置迁移：show_translation 布尔 → second_line 枚举
            if (node.ContainsKey("show_translation") && !node.ContainsKey("second_line"))
            {
                var old = node["show_translation"];
                node["second_line"] = old is JsonValue v && v.TryGetValue<bool>(out var b) && b
                    ? "translation" : "off";
            }
            node.Remove("show_translation");
            cfg = node.Deserialize<AppConfig>()
                ?? throw new InvalidDataException("配置反序列化结果为空");
            return true;
        }
        catch (Exception ex)
        {
            // 不记的话，用户看到的只是「设置全变回默认了」，事后无从下手
            Log.Error("config-load", ex);
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch { /* 挪不动就算了，日志里已经留了一条 */ }
            return false;
        }
    }

    private static bool _writeLogged;

    /// <summary>存盘，返回是否成功（连退路都写不进去才算失败，调用方据此提示一次）。
    ///
    /// 原先整体吞掉异常：exe 装在 Program Files 之类只读位置时，设置在内存里当场生效、
    /// 一重启全丢，而且没有任何提示。现在先试 exe 同目录，不行就改存 %AppData%。</summary>
    public bool Save()
    {
        if (TryWrite(ConfigPath)) return true;
        var fallback = FallbackPath;
        if (!string.Equals(ConfigPath, fallback, StringComparison.OrdinalIgnoreCase)
            && TryWrite(fallback))
        {
            // 留一条：这条路以前完全静默，而它意味着配置从此存在 %AppData%，
            // 用户以为「删掉 exe 同目录的 config.json 就恢复默认」时会对不上账。
            // 不弹窗——设置确实存住了，功能正常，Load 现在也会跟着读较新的那份
            Log.Note("config-save", $"exe 同目录写不进去，配置已转存到 {fallback}");
            ConfigPath = fallback; // 之后一直用退路，下次 Load 也会从那里读
            return true;
        }
        return false;
    }

    private bool TryWrite(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 先写临时文件再原子改名。直接覆写的话，写一半遇上关机/断电/杀进程
            // 就留下一份半截 JSON，而解析失败的代价是整份设置回默认值
            // （LyricsCache 那边踩过同一个坑，解法也是这个）
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // 只记第一次：拖动窗口每次松手都会存盘，失败时会把 error.log 刷满
            if (!_writeLogged)
            {
                _writeLogged = true;
                Log.Error("config-save", ex);
            }
            return false;
        }
    }
}
