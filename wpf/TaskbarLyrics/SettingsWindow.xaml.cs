// 统一设置窗口（Win11 风格：左侧导航 + 卡片分组；多选项下拉框、布尔项开关）。
// 从歌词条右键菜单或托盘菜单的「打开设置…」进入；确定/应用后写配置并实时生效。
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskbarLyrics;

public partial class SettingsWindow : Window
{
    private readonly MainController _app;

    private sealed record NavItem(string Glyph, string Name);

    public SettingsWindow(MainController app, int page = 0)
    {
        _app = app;
        InitializeComponent();
        NavList.ItemsSource = new[]
        {
            new NavItem("\uE713", "通用"),
            new NavItem("\uE189", "歌词"),
            new NavItem("\uE771", "外观"),
            new NavItem("\uE946", "关于"),
        };
        NavList.SelectedIndex = Math.Clamp(page, 0, 3);
        LoadValues();
        _baseline = ReadForm();
    }

    /// <summary>Win11 22H2+ 启用 Mica 背景材质；不支持时（如 Win10）保留 XAML 里的
    /// 不透明主题底色——那里绝不能填半透明色，没有 Mica 兜底会露出不可控的底。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.TryEnableMica(hwnd))
            Background = Brushes.Transparent; // Mica 从窗口背景透出来
    }

    private AppConfig Cfg => _app.Cfg;

    /// <summary>打开「关于」页里的外链。UseShellExecute 必须为 true：
    /// 默认 false 时 http(s) 地址会被当作可执行文件路径而抛异常。</summary>
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) OpenExternal(url);
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        // exe 同目录写不进去时日志在 %AppData%\TaskbarLyrics，按实际位置打开
        var dir = Log.LogDir();
        if (!string.IsNullOrEmpty(dir)) OpenExternal(dir);
    }

    /// <summary>打不开就只记日志：这是个纯附带的便利入口，不值得弹窗打断。</summary>
    private static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("openexternal", ex); }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PageGeneral.Visibility = NavList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageLyrics.Visibility = NavList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = NavList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    private void LoadValues()
    {
        var cfg = Cfg;
        ModeCombo.SelectedIndex = cfg.Mode == "taskbar" ? 0 : 1;

        var mons = NativeMethods.Monitors();
        for (var i = 0; i < mons.Count; i++)
        {
            var m = mons[i];
            MonitorCombo.Items.Add($"显示器 {i + 1}（{m.Rect.Width}x{m.Rect.Height}）" + (m.Primary ? "（主）" : ""));
        }
        MonitorCombo.SelectedIndex = Math.Clamp(cfg.Monitor, 0, Math.Max(0, mons.Count - 1));

        HideFullscreenSwitch.IsChecked = cfg.HideOnFullscreen;
        AutoPositionSwitch.IsChecked = cfg.AutoPosition;
        AutoSideCombo.SelectedIndex = cfg.AutoSide == "left" ? 1 : 0;
        AutoAlignCombo.SelectedIndex = cfg.AutoAlign == "right" ? 1 : cfg.AutoAlign == "center" ? 2 : 0;
        LockedSwitch.IsChecked = cfg.Locked;
        AutostartSwitch.IsChecked = Autostart.IsEnabled();
        VersionText.Text = $"v{Updater.CurrentVersion}";
        AboutVersionText.Text = $"版本 {Updater.CurrentVersion}";
        UpdateCheckSwitch.IsChecked = cfg.UpdateCheck;
        RefreshUpdateUi();

        SecondLineCombo.SelectedIndex = cfg.SecondLine == "romaji" ? 1 : cfg.SecondLine == "off" ? 2 : 0;
        KaraokeSwitch.IsChecked = cfg.Karaoke;
        OffsetBox.Text = (cfg.OffsetMs / 1000.0).ToString("0.0");

        SourceCombo.SelectedIndex = cfg.PlayerSource == "netease" ? 1 : cfg.PlayerSource == "others" ? 2 : 0;
        RefreshBlockUi();

        foreach (var f in Fonts.SystemFontFamilies)
            FontCombo.Items.Add(f.Source);
        FontCombo.Text = cfg.FontFamily;
        FontSizeBox.Text = cfg.FontSize.ToString();
        FontBoldSwitch.IsChecked = cfg.FontBold;
        AlignCombo.SelectedIndex = cfg.TextAlign == "left" ? 1 : 0;
        WidthCombo.Text = cfg.Width.ToString();
        TextColorBox.Text = cfg.TextColor;
        TransColorBox.Text = cfg.TransColor;
        ColorModeCombo.SelectedIndex = cfg.TextColorMode == "custom" ? 1 : 0;
        ShadowSwitch.IsChecked = cfg.Shadow;
        ShowCoverSwitch.IsChecked = cfg.ShowCover;
        ShowControlsSwitch.IsChecked = cfg.ShowControls;
        UpdateSwatches();
        UpdateColorEnabled();
    }

    /// <summary>跟随系统时把颜色输入框和预设色块置灰：留着能点却不生效最气人。</summary>
    private void UpdateColorEnabled()
    {
        var custom = ColorModeCombo.SelectedIndex == 1;
        foreach (var el in new UIElement[]
                 {
                     TextColorBox, TransColorBox,
                     TextChip1, TextChip2, TextChip3, TextChip4,
                     TransChip1, TransChip2, TransChip3, TransChip4,
                 })
            el.IsEnabled = custom;
        TextColorSwatch.Opacity = custom ? 1 : 0.4;
        TransColorSwatch.Opacity = custom ? 1 : 0.4;
    }

    private void ColorModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateColorEnabled();

    /// <summary>点预设色块：把 hex 填进对应的输入框（色板预览由 TextChanged 顺带刷新）。</summary>
    private void ColorChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex) return;
        var box = b.Name.StartsWith("Text", StringComparison.Ordinal) ? TextColorBox : TransColorBox;
        box.Text = hex;
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (!Regex.IsMatch(hex.Trim(), "^#[0-9A-Fa-f]{6}$")) return false;
        color = (Color)ColorConverter.ConvertFromString(hex.Trim());
        return true;
    }

    private void UpdateSwatches()
    {
        TextColorSwatch.Background = TryParseColor(TextColorBox.Text, out var c1)
            ? new SolidColorBrush(c1) : Brushes.Transparent;
        TransColorSwatch.Background = TryParseColor(TransColorBox.Text, out var c2)
            ? new SolidColorBrush(c2) : Brushes.Transparent;
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSwatches();

    private static readonly string[] SecondLineValues = { "translation", "romaji", "off" };
    private static readonly string[] SourceValues = { "auto", "netease", "others" };

    /// <summary>窗口里各项控件的原样取值（下拉框序号、开关状态、输入框原文，不做校验）。</summary>
    private sealed record FormValues(
        int Mode, int Monitor, bool HideOnFullscreen, bool AutoPosition, int AutoSide, int AutoAlign,
        bool Locked, bool UpdateCheck,
        int SecondLine, bool Karaoke, string Offset, int Source,
        string Font, string FontSize, bool FontBold, int Align, string Width,
        int ColorMode, string TextColor, string TransColor, bool Shadow, bool ShowCover, bool ShowControls);

    /// <summary>窗口打开时（或上次应用成功时）各控件的取值，Apply 拿它判断哪些项用户动过。</summary>
    private FormValues _baseline;

    private FormValues ReadForm() => new(
        ModeCombo.SelectedIndex, MonitorCombo.SelectedIndex,
        HideFullscreenSwitch.IsChecked == true, AutoPositionSwitch.IsChecked == true,
        AutoSideCombo.SelectedIndex, AutoAlignCombo.SelectedIndex,
        LockedSwitch.IsChecked == true, UpdateCheckSwitch.IsChecked == true,
        SecondLineCombo.SelectedIndex, KaraokeSwitch.IsChecked == true, OffsetBox.Text,
        SourceCombo.SelectedIndex,
        FontCombo.Text, FontSizeBox.Text, FontBoldSwitch.IsChecked == true,
        AlignCombo.SelectedIndex, WidthCombo.Text,
        ColorModeCombo.SelectedIndex, TextColorBox.Text, TransColorBox.Text,
        ShadowSwitch.IsChecked == true, ShowCoverSwitch.IsChecked == true, ShowControlsSwitch.IsChecked == true);

    /// <summary>只写回用户在窗口里动过的项（与基准比）。
    ///
    /// 窗口开着时托盘菜单照样能改偏移、模式、锁定、自动避让，原先这里把窗口里每一项
    /// 原样写回，那些改动会在点「应用」时被窗口打开那一刻的旧值悄悄盖掉。
    /// 没动过的项也不校验：不写它，就没理由因为它拦着不让存（比如手改配置留下的越界值）。</summary>
    private bool Apply()
    {
        var now = ReadForm();
        var was = _baseline;
        var fontSize = 0;
        if (now.FontSize != was.FontSize
            && (!int.TryParse(now.FontSize, out fontSize) || fontSize < 8 || fontSize > 24))
        {
            MessageBox.Show("字号需为 8~24 的整数", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var width = 0;
        if (now.Width != was.Width
            && (!int.TryParse(now.Width, out width) || width < 100 || width > 2000))
        {
            MessageBox.Show("最大宽度需为 100~2000 的整数", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var offsetS = 0.0;
        if (now.Offset != was.Offset
            && (!double.TryParse(now.Offset, out offsetS) || offsetS < -3 || offsetS > 3))
        {
            MessageBox.Show("歌词偏移需在 -3~3 秒之间", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var customColor = now.ColorMode == 1;
        // 色值只在「自定义」下才写回，切到自定义或改了色值时才需要校验。
        // 跟随系统时这两个框根本不参与取色，没必要因为里面留着的旧文本拦着人保存
        var colorsChanged = now.ColorMode != was.ColorMode
                            || now.TextColor != was.TextColor || now.TransColor != was.TransColor;
        if (customColor && colorsChanged
            && (!TryParseColor(now.TextColor, out _) || !TryParseColor(now.TransColor, out _)))
        {
            MessageBox.Show("颜色格式应为 #RRGGBB", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var cfg = Cfg;
        var secondLine = SecondLineValues[Math.Clamp(now.SecondLine, 0, 2)];
        var secondLineChanged = now.SecondLine != was.SecondLine && cfg.SecondLine != secondLine;
        var karaokeChanged = now.Karaoke != was.Karaoke && cfg.Karaoke != now.Karaoke;

        if (now.Mode != was.Mode) cfg.Mode = now.Mode == 1 ? "floating" : "taskbar";
        if (now.Monitor != was.Monitor && now.Monitor >= 0) cfg.Monitor = now.Monitor;
        if (now.HideOnFullscreen != was.HideOnFullscreen) cfg.HideOnFullscreen = now.HideOnFullscreen;
        if (now.AutoPosition != was.AutoPosition) cfg.AutoPosition = now.AutoPosition;
        if (now.AutoSide != was.AutoSide) cfg.AutoSide = now.AutoSide == 1 ? "left" : "right";
        if (now.AutoAlign != was.AutoAlign)
            cfg.AutoAlign = now.AutoAlign switch
            {
                1 => "right",
                2 => "center",
                _ => "left",
            };
        if (secondLineChanged) cfg.SecondLine = secondLine;
        if (karaokeChanged) cfg.Karaoke = now.Karaoke;
        if (now.Offset != was.Offset) cfg.OffsetMs = Math.Clamp((int)(offsetS * 1000), -3000, 3000);
        if (now.Source != was.Source) cfg.PlayerSource = SourceValues[Math.Clamp(now.Source, 0, 2)];
        var family = now.Font.Trim();
        if (now.Font != was.Font && family.Length > 0) cfg.FontFamily = family;
        if (now.FontSize != was.FontSize) cfg.FontSize = fontSize;
        if (now.FontBold != was.FontBold) cfg.FontBold = now.FontBold;
        if (now.Width != was.Width) cfg.Width = width;
        if (now.Align != was.Align) cfg.TextAlign = now.Align == 1 ? "left" : "center";
        if (now.ColorMode != was.ColorMode) cfg.TextColorMode = customColor ? "custom" : "auto";
        if (customColor && colorsChanged)
        {
            // 只在自定义模式下回写：跟随系统时框里可能是没校验过的内容，
            // 写进去等用户哪天切到自定义就会拿到一个坏值
            cfg.TextColor = now.TextColor.Trim();
            cfg.TransColor = now.TransColor.Trim();
        }
        if (now.Shadow != was.Shadow) cfg.Shadow = now.Shadow;
        if (now.ShowCover != was.ShowCover) cfg.ShowCover = now.ShowCover;
        if (now.ShowControls != was.ShowControls) cfg.ShowControls = now.ShowControls;
        if (now.UpdateCheck != was.UpdateCheck) cfg.UpdateCheck = now.UpdateCheck;

        if (now.Locked != was.Locked) _app.SetLocked(now.Locked);
        _app.SetAutostart(AutostartSwitch.IsChecked == true);
        _app.SaveCfg();
        _app.ApplySettings(refetchLyrics: secondLineChanged || karaokeChanged);
        // 应用过的就是新基准：否则第二次点「应用」时，第一次改过的项会再被写一遍，
        // 把这期间托盘里对同一项的改动又盖回去
        _baseline = now;
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Apply())
        {
            DialogResult = true;
            Close();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>按钮上写着的那个播放器 id：点击时屏蔽/解除的必须是它，而不是点击那一刻的「当前播放器」。</summary>
    private string _blockTarget = "";

    /// <summary>刷新「屏蔽当前播放器」按钮和下面的已屏蔽名单。</summary>
    private void RefreshBlockUi()
    {
        var (sid, label) = _app.CurrentBlockToggle();
        _blockTarget = sid;
        BlockButton.Content = label;
        BlockedList.ItemsSource = Cfg.PlayerBlocklist.ToList();
        BlockedDescText.Text = Cfg.PlayerBlocklist.Count == 0
            ? "目前没有屏蔽任何播放器"
            : "按关键词匹配播放器来源；取消屏蔽后立即生效";
    }

    private void BlockButton_Click(object sender, RoutedEventArgs e)
    {
        _app.ToggleBlock(_blockTarget);
        RefreshBlockUi();
    }

    private void UnblockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string keyword }) _app.Unblock(keyword);
        RefreshBlockUi();
    }

    /// <summary>歌词抓错了的出路：缓存命中后永远不再联网，不清掉就只能等它 30 天过期。</summary>
    private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        var n = LyricsCache.Clear();
        var what = n > 0 ? $"已清空 {n} 条歌词缓存" : "缓存本来就是空的";
        CacheStatusText.Text = _app.RefetchLyrics()
            ? $"{what}，当前歌曲正在重新获取"
            : $"{what}，从下一首歌起重新获取";
    }

    // ---- 检查更新 ----

    private void RefreshUpdateUi()
    {
        if (_app.PendingUpdate is { } rel)
        {
            UpdateStatusText.Text = $"发现新版本 {rel.Tag}";
            UpdateButton.Content = "立即更新";
        }
        else
        {
            UpdateButton.Content = "检查更新";
        }
        UpdateButton.IsEnabled = true;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        if (_app.PendingUpdate != null)
        {
            UpdateStatusText.Text = "正在下载更新…（完成后自动重启）";
            var msg = await _app.DownloadAndApplyAsync();
            if (msg.Length > 0) // 只有失败才返回文案（成功则进程已退出，新版自动启动）
            {
                UpdateStatusText.Text = msg;
                UpdateButton.IsEnabled = true;
            }
            return;
        }
        UpdateStatusText.Text = "正在检查…";
        var status = await _app.CheckForUpdateAsync();
        RefreshUpdateUi();
        UpdateStatusText.Text = status;
    }
}
