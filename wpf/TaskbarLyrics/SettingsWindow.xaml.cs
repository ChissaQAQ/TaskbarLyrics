// 统一设置窗口（移植自 settings_window.py）：所有选项集中管理。
// 从歌词条右键菜单或托盘菜单的「打开设置…」进入；确定/应用后写配置并实时生效。
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskbarLyrics;

public partial class SettingsWindow : Window
{
    private readonly MainController _app;

    public SettingsWindow(MainController app)
    {
        _app = app;
        InitializeComponent();
        LoadValues();
    }

    private AppConfig Cfg => _app.Cfg;

    private void LoadValues()
    {
        var cfg = Cfg;
        ModeTaskbar.IsChecked = cfg.Mode == "taskbar";
        ModeFloating.IsChecked = cfg.Mode != "taskbar";

        var mons = NativeMethods.Monitors();
        for (var i = 0; i < mons.Count; i++)
        {
            var m = mons[i];
            MonitorBox.Items.Add($"显示器 {i + 1}（{m.Rect.Width}x{m.Rect.Height}）" + (m.Primary ? "（主）" : ""));
        }
        MonitorBox.SelectedIndex = Math.Clamp(cfg.Monitor, 0, Math.Max(0, mons.Count - 1));

        LockedCheck.IsChecked = cfg.Locked;
        HideFullscreenCheck.IsChecked = cfg.HideOnFullscreen;
        AutostartCheck.IsChecked = Autostart.IsEnabled();

        SecondTranslation.IsChecked = cfg.SecondLine == "translation";
        SecondRomaji.IsChecked = cfg.SecondLine == "romaji";
        SecondOff.IsChecked = cfg.SecondLine != "translation" && cfg.SecondLine != "romaji";
        KaraokeCheck.IsChecked = cfg.Karaoke;
        ShowCoverCheck.IsChecked = cfg.ShowCover;
        ShowControlsCheck.IsChecked = cfg.ShowControls;
        OffsetBox.Text = (cfg.OffsetMs / 1000.0).ToString("0.0");

        SourceAuto.IsChecked = cfg.PlayerSource == "auto";
        SourceNetease.IsChecked = cfg.PlayerSource == "netease";
        SourceOthers.IsChecked = cfg.PlayerSource == "others";
        BlockButton.Content = _app.BlockCurrentLabel();

        foreach (var f in Fonts.SystemFontFamilies)
            FontBox.Items.Add(f.Source);
        FontBox.Text = cfg.FontFamily;
        FontSizeBox.Text = cfg.FontSize.ToString();
        AlignCenter.IsChecked = cfg.TextAlign != "left";
        AlignLeft.IsChecked = cfg.TextAlign == "left";
        foreach (var w in new[] { 420, 560, 700 })
            WidthBox.Items.Add(w.ToString());
        WidthBox.Text = cfg.Width.ToString();
        TextColorBox.Text = cfg.TextColor;
        TransColorBox.Text = cfg.TransColor;
        ShadowCheck.IsChecked = cfg.Shadow;
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

    private string SelectedSecondLine() =>
        SecondRomaji.IsChecked == true ? "romaji" : SecondOff.IsChecked == true ? "off" : "translation";

    private string SelectedSource() =>
        SourceNetease.IsChecked == true ? "netease" : SourceOthers.IsChecked == true ? "others" : "auto";

    private bool Apply()
    {
        if (!int.TryParse(FontSizeBox.Text, out var fontSize) || fontSize < 8 || fontSize > 24)
        {
            MessageBox.Show("字号需为 8~24 的整数", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!int.TryParse(WidthBox.Text, out var width) || width < 200 || width > 2000)
        {
            MessageBox.Show("最大宽度需为 200~2000 的整数", "任务栏歌词", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var secondLineChanged = cfg.SecondLine != SelectedSecondLine();
        var karaokeChanged = cfg.Karaoke != (KaraokeCheck.IsChecked == true);

        cfg.Mode = ModeFloating.IsChecked == true ? "floating" : "taskbar";
        if (MonitorBox.SelectedIndex >= 0) cfg.Monitor = MonitorBox.SelectedIndex;
        cfg.HideOnFullscreen = HideFullscreenCheck.IsChecked == true;
        cfg.SecondLine = SelectedSecondLine();
        cfg.Karaoke = KaraokeCheck.IsChecked == true;
        cfg.ShowCover = ShowCoverCheck.IsChecked == true;
        cfg.ShowControls = ShowControlsCheck.IsChecked == true;
        cfg.OffsetMs = Math.Clamp((int)(offsetS * 1000), -3000, 3000);
        cfg.PlayerSource = SelectedSource();
        var family = FontBox.Text.Trim();
        if (family.Length > 0) cfg.FontFamily = family;
        cfg.FontSize = fontSize;
        cfg.Width = width;
        cfg.TextAlign = AlignLeft.IsChecked == true ? "left" : "center";
        cfg.TextColor = TextColorBox.Text.Trim();
        cfg.TransColor = TransColorBox.Text.Trim();
        cfg.Shadow = ShadowCheck.IsChecked == true;

        _app.SetLocked(LockedCheck.IsChecked == true);
        _app.SetAutostart(AutostartCheck.IsChecked == true);
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
