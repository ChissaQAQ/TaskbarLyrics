// 文字配色：跟随系统任务栏明暗自动取色，或用用户手动指定的颜色。
//
// 单独成文的理由：AppConfig 是纯数据 + JSON 存取，把「读注册表判系统主题」塞进去
// 会混掉职责；而颜色决策要被歌词行、歌曲信息层、逐字两色、阴影四处共用，
// 散在各调用点必然写出四份不一致的默认值。
using Microsoft.Win32;
using System.Windows.Media;

namespace TaskbarLyrics;

internal static class Theme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // 深色任务栏用白字 + 浅灰译文；浅色任务栏换成近黑 + 中灰。
    // 浅底不用纯黑：Win11 浅色任务栏底是 #F3F3F3 一类的浅灰，纯黑压上去对比过硬，
    // 近黑既够清楚又不显脏。译文比原文淡一档，保持两行的主次关系
    private static readonly Color DarkBgText = Colors.White;
    private static readonly Color DarkBgTrans = Color.FromRgb(0xC8, 0xC8, 0xC8);
    private static readonly Color LightBgText = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Color LightBgTrans = Color.FromRgb(0x5A, 0x5A, 0x5A);

    /// <summary>任务栏当前是不是浅色。</summary>
    public static bool TaskbarIsLight { get; private set; } = Read();

    /// <summary>重读系统主题，返回是否变了。
    ///
    /// 变了必须重建行视觉：颜色是建行时冻结进 Brush 的（冻结是为了让渲染层共享资源），
    /// 光改配置不重建，画面上一个字都不会变。</summary>
    public static bool Refresh()
    {
        var now = Read();
        if (now == TaskbarIsLight) return false;
        TaskbarIsLight = now;
        return true;
    }

    /// <summary>任务栏明暗只认 SystemUsesLightTheme，绝不能读 AppsUseLightTheme。
    ///
    /// 这两个键各自独立：实测有机器是「应用浅色 + 任务栏深色」，两个值正好相反，
    /// 读错那个就 100% 判反。键不存在（没设置过主题）时按深色——那是 Win11 的默认任务栏。</summary>
    private static bool Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>原文颜色（歌词当前行、歌名）。</summary>
    public static Color TextColor(AppConfig cfg) => IsCustom(cfg)
        ? Parse(cfg.TextColor, DarkBgText)
        : TaskbarIsLight ? LightBgText : DarkBgText;

    /// <summary>第二行颜色（译文/罗马音、歌手名）。</summary>
    public static Color TransColor(AppConfig cfg) => IsCustom(cfg)
        ? Parse(cfg.TransColor, DarkBgTrans)
        : TaskbarIsLight ? LightBgTrans : DarkBgTrans;

    private static bool IsCustom(AppConfig cfg) => cfg.TextColorMode == "custom";

    /// <summary>阴影该用黑还是白——由文字自身亮度决定，而不是由主题决定。
    ///
    /// 阴影的作用是给笔画描一层反差把字从背景里托出来，深色文字配深色阴影只会更糊
    /// （原先阴影写死是黑的，浅色任务栏上就是这个负优化）。按文字亮度反着取，
    /// 手动指定颜色的用户挑什么色都自动跟上，不必再多一个「阴影颜色」选项。</summary>
    public static bool BlackShadow(Color text) => 0.299 * text.R + 0.587 * text.G + 0.114 * text.B >= 128;

    // 装饰件（悬停遮罩、封面占位）的两套配色。这些跟文字颜色无关、永远跟着任务栏走：
    // 悬停遮罩是在模仿 Win11 任务栏图标的悬停高亮，浅色任务栏上那是一层淡黑而不是淡白；
    // 白遮罩压在浅底上等于什么都没画，封面占位的白音符更是直接消失
    private static readonly Brush DarkHover = Frozen(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LightHover = Frozen(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
    private static readonly Brush DarkHolderBg = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LightHolderBg = Frozen(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
    private static readonly Brush DarkHolderIcon = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LightHolderIcon = Frozen(Color.FromArgb(0x99, 0x00, 0x00, 0x00));

    /// <summary>悬停遮罩底色。</summary>
    public static Brush HoverMask => TaskbarIsLight ? LightHover : DarkHover;

    /// <summary>无封面时占位方块的底色。</summary>
    public static Brush PlaceholderBg => TaskbarIsLight ? LightHolderBg : DarkHolderBg;

    /// <summary>无封面时占位音符的颜色。</summary>
    public static Brush PlaceholderIcon => TaskbarIsLight ? LightHolderIcon : DarkHolderIcon;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Parse(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }
}
