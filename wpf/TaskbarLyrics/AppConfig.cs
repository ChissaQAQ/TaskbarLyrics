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
    // 播放源
    [JsonPropertyName("player_source")] public string PlayerSource { get; set; } = "auto"; // auto | netease | others
    [JsonPropertyName("player_blocklist")] public List<string> PlayerBlocklist { get; set; } = new() { "chrome", "msedge", "firefox" };

    // 保留 Python 版里未知的扩展键，存盘时不丢
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>当前使用的配置文件路径。首选 exe 同目录（便携、删掉即恢复默认），
    /// 那里写不进去时会切到 <see cref="FallbackPath"/>。</summary>
    public static string ConfigPath { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>exe 同目录只读时（装在 Program Files、或从只读介质运行）的退路。</summary>
    private static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarLyrics", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 对应 Python ensure_ascii=False
    };

    public static AppConfig Load()
    {
        AppConfig? cfg = null;
        // exe 同目录优先；没有就看退路——上次可能因为目录只读存到那边去了
        if (!File.Exists(ConfigPath) && File.Exists(FallbackPath)) ConfigPath = FallbackPath;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                var node = JsonNode.Parse(text)?.AsObject();
                if (node != null)
                {
                    // 旧配置迁移：show_translation 布尔 → second_line 枚举
                    if (node.ContainsKey("show_translation") && !node.ContainsKey("second_line"))
                    {
                        var old = node["show_translation"];
                        node["second_line"] = old is JsonValue v && v.TryGetValue<bool>(out var b) && b
                            ? "translation" : "off";
                    }
                    node.Remove("show_translation");
                    cfg = node.Deserialize<AppConfig>();
                }
            }
        }
        catch
        {
            cfg = null; // 配置损坏时用默认值
        }
        return cfg ?? new AppConfig();
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
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
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
