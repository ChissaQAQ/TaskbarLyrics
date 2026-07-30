// 统一设置窗口（Win11 风格：左侧导航 + 卡片分组；多选项下拉框、布尔项开关）。
// 从歌词条右键菜单或托盘菜单的「打开设置…」进入；确定/应用后写配置并实时生效。
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
        };
        NavList.SelectedIndex = Math.Clamp(page, 0, 2);
        LoadValues();
    }

    /// <summary>Win11 22H2+ 启用 Mica 背景材质（不支持时保留 XAML 里的微灰底色）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.TryEnableMica(hwnd))
            Background = Brushes.Transparent; // Mica 从窗口背景透出来
    }

    private AppConfig Cfg => _app.Cfg;

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PageGeneral.Visibility = NavList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageLyrics.Visibility = NavList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = NavList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
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
        LockedSwitch.IsChecked = cfg.Locked;
        AutostartSwitch.IsChecked = Autostart.IsEnabled();

        SecondLineCombo.SelectedIndex = cfg.SecondLine == "romaji" ? 1 : cfg.SecondLine == "off" ? 2 : 0;
        KaraokeSwitch.IsChecked = cfg.Karaoke;
        OffsetBox.Text = (cfg.OffsetMs / 1000.0).ToString("0.0");

        SourceCombo.SelectedIndex = cfg.PlayerSource == "netease" ? 1 : cfg.PlayerSource == "others" ? 2 : 0;
        BlockButton.Content = _app.BlockCurrentLabel();

        foreach (var f in Fonts.SystemFontFamilies)
            FontCombo.Items.Add(f.Source);
        FontCombo.Text = cfg.FontFamily;
        FontSizeBox.Text = cfg.FontSize.ToString();
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
        cfg.SecondLine = secondLine;
        cfg.Karaoke = karaoke;
        cfg.OffsetMs = Math.Clamp((int)(offsetS * 1000), -3000, 3000);
        cfg.PlayerSource = SourceValues[Math.Clamp(SourceCombo.SelectedIndex, 0, 2)];
        var family = FontCombo.Text.Trim();
        if (family.Length > 0) cfg.FontFamily = family;
        cfg.FontSize = fontSize;
        cfg.Width = width;
        cfg.TextAlign = AlignCombo.SelectedIndex == 1 ? "left" : "center";
        cfg.TextColor = TextColorBox.Text.Trim();
        cfg.TransColor = TransColorBox.Text.Trim();
        cfg.Shadow = ShadowSwitch.IsChecked == true;
        cfg.ShowCover = ShowCoverSwitch.IsChecked == true;
        cfg.ShowControls = ShowControlsSwitch.IsChecked == true;

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
}
