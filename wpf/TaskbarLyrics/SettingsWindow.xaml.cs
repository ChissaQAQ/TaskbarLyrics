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
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
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
        BlockButton.Content = _app.BlockCurrentLabel();

        foreach (var f in Fonts.SystemFontFamilies)
            FontCombo.Items.Add(f.Source);
        FontCombo.Text = cfg.FontFamily;
        FontSizeBox.Text = cfg.FontSize.ToString();
        FontBoldSwitch.IsChecked = cfg.FontBold;
        AlignCombo.SelectedIndex = cfg.TextAlign == "left" ? 1 : 0;
        WidthCombo.Text = cfg.Width.ToString();
        TextColorBox.Text = cfg.TextColor;
        TransColorBox.Text = cfg.TransColor;
        ShadowSwitch.IsChecked = cfg.Shadow;
        ShowCoverSwitch.IsChecked = cfg.ShowCover;
        ShowControlsSwitch.IsChecked = cfg.ShowControls;
        UpdateSwatches();
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

    private bool Apply()
    {
        if (!int.TryParse(FontSizeBox.Text, out var fontSize) || fontSize < 8 || fontSize > 24)
        {
            MessageBox.Show("字号需为 8~24 的整数", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!int.TryParse(WidthCombo.Text, out var width) || width < 100 || width > 2000)
        {
            MessageBox.Show("最大宽度需为 100~2000 的整数", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!double.TryParse(OffsetBox.Text, out var offsetS) || offsetS < -3 || offsetS > 3)
        {
            MessageBox.Show("歌词偏移需在 -3~3 秒之间", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!TryParseColor(TextColorBox.Text, out _) || !TryParseColor(TransColorBox.Text, out _))
        {
            MessageBox.Show("颜色格式应为 #RRGGBB", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var cfg = Cfg;
        var secondLine = SecondLineValues[Math.Clamp(SecondLineCombo.SelectedIndex, 0, 2)];
        var karaoke = KaraokeSwitch.IsChecked == true;
        var secondLineChanged = cfg.SecondLine != secondLine;
        var karaokeChanged = cfg.Karaoke != karaoke;

        cfg.Mode = ModeCombo.SelectedIndex == 1 ? "floating" : "taskbar";
        if (MonitorCombo.SelectedIndex >= 0) cfg.Monitor = MonitorCombo.SelectedIndex;
        cfg.HideOnFullscreen = HideFullscreenSwitch.IsChecked == true;
        cfg.AutoPosition = AutoPositionSwitch.IsChecked == true;
        cfg.AutoSide = AutoSideCombo.SelectedIndex == 1 ? "left" : "right";
        cfg.AutoAlign = AutoAlignCombo.SelectedIndex switch
        {
            1 => "right",
            2 => "center",
            _ => "left",
        };
        cfg.SecondLine = secondLine;
        cfg.Karaoke = karaoke;
        cfg.OffsetMs = Math.Clamp((int)(offsetS * 1000), -3000, 3000);
        cfg.PlayerSource = SourceValues[Math.Clamp(SourceCombo.SelectedIndex, 0, 2)];
        var family = FontCombo.Text.Trim();
        if (family.Length > 0) cfg.FontFamily = family;
        cfg.FontSize = fontSize;
        cfg.FontBold = FontBoldSwitch.IsChecked == true;
        cfg.Width = width;
        cfg.TextAlign = AlignCombo.SelectedIndex == 1 ? "left" : "center";
        cfg.TextColor = TextColorBox.Text.Trim();
        cfg.TransColor = TransColorBox.Text.Trim();
        cfg.Shadow = ShadowSwitch.IsChecked == true;
        cfg.ShowCover = ShowCoverSwitch.IsChecked == true;
        cfg.ShowControls = ShowControlsSwitch.IsChecked == true;
        cfg.UpdateCheck = UpdateCheckSwitch.IsChecked == true;

        _app.SetLocked(LockedSwitch.IsChecked == true);
        _app.SetAutostart(AutostartSwitch.IsChecked == true);
        _app.SaveCfg();
        _app.ApplySettings(refetchLyrics: secondLineChanged || karaokeChanged);
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

    private void BlockButton_Click(object sender, RoutedEventArgs e)
    {
        _app.ToggleBlockCurrent();
        BlockButton.Content = _app.BlockCurrentLabel();
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
