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
    [JsonPropertyName("font_size")] public int FontSize { get; set; } = 13;            // 原文字号
    [JsonPropertyName("text_color")] public string TextColor { get; set; } = "#FFFFFF";
    [JsonPropertyName("trans_color")] public string TransColor { get; set; } = "#C8C8C8";
    [JsonPropertyName("highlight_color")] public string? HighlightColor { get; set; }  // 预留，None = 跟随系统强调色
    [JsonPropertyName("shadow")] public bool Shadow { get; set; } = true;              // 文字阴影
    [JsonPropertyName("width")] public int Width { get; set; } = 560;                  // 歌词区最大宽度（紧凑布局下内容更窄时收缩，超出才缩字号）
    [JsonPropertyName("text_align")] public string TextAlign { get; set; } = "center"; // center | left（配封面时左对齐更整齐）
    [JsonPropertyName("show_controls")] public bool ShowControls { get; set; } = true; // 悬停播放控制按钮
    // 位置
    [JsonPropertyName("mode")] public string Mode { get; set; } = "taskbar";           // taskbar | floating
    [JsonPropertyName("position")] public string Position { get; set; } = "tray_left"; // tray_left | left | center | right | custom
    [JsonPropertyName("x_offset")] public int? XOffset { get; set; }                   // position=custom 时任务栏内 x
    [JsonPropertyName("float_x")] public int? FloatX { get; set; }                     // 浮动模式屏幕坐标
    [JsonPropertyName("float_y")] public int? FloatY { get; set; }
    [JsonPropertyName("monitor")] public int Monitor { get; set; } = 0;
    [JsonPropertyName("locked")] public bool Locked { get; set; }                      // 锁定后鼠标穿透
    [JsonPropertyName("show_cover")] public bool ShowCover { get; set; } = true;       // 显示专辑封面
    // 歌词
    [JsonPropertyName("second_line")] public string SecondLine { get; set; } = "translation"; // translation | romaji | off
    [JsonPropertyName("karaoke")] public bool Karaoke { get; set; } = true;            // 逐字歌词
    [JsonPropertyName("offset_ms")] public int OffsetMs { get; set; } = 0;             // 歌词时间偏移（提前为负）
    // 行为
    [JsonPropertyName("hide_on_fullscreen")] public bool HideOnFullscreen { get; set; } = true;
    // 播放源
    [JsonPropertyName("player_source")] public string PlayerSource { get; set; } = "auto"; // auto | netease | others
    [JsonPropertyName("player_blocklist")] public List<string> PlayerBlocklist { get; set; } = new() { "chrome", "msedge", "firefox" };

    // 保留 Python 版里未知的扩展键，存盘时不丢
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 对应 Python ensure_ascii=False
    };

    public static AppConfig Load()
    {
        AppConfig? cfg = null;
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

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 设置存不上不影响使用
        }
    }
}
