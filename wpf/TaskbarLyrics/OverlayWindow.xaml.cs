// 歌词覆盖窗口（移植自 overlay.py 的窗口行为；渲染改为 WPF 合成器）。
//
// 两种模式：
// - taskbar：挂进任务栏（Shell_TrayWnd/副屏 Shell_SecondaryTrayWnd）成为子窗口
// - floating：独立置顶悬浮窗，可拖到屏幕任意位置
// 左键拖动、右键弹出菜单、锁定时整窗鼠标穿透。
// 悬停时播放控制按钮淡入浮现（离开时淡出），切行有滑动淡入动画，
// 逐字扫过由 Storyboard 驱动 KaraokeText.PositionMs（GPU 合成，不逐帧重绘）。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics;

public partial class OverlayWindow : Window
{
    private static readonly TimeSpan AnimLine = TimeSpan.FromMilliseconds(280); // 切行动画
    private static readonly TimeSpan AnimFade = TimeSpan.FromMilliseconds(150); // 按钮/遮罩浮现淡出
    private const double ButtonsWidth = 96;                                     // 悬停按钮区占位宽度
    private const double AnimWidthSec = 0.22;                                   // 悬停宽度过渡时长

    private readonly MainController _app;
    private IntPtr _hwnd;

    private string _original = "";
    private string _translation = "";
    private string _title = "";
    private string _artist = "";
    private FrameworkElement? _currentLine;
    private KaraokeText? _karaoke;
    private Storyboard? _karaokeStoryboard;
    private double _karaokeTotalMs;
    private bool _sbPlaying;
    private bool _hasContent;
    private bool _fsHidden;
    private double _lyricsWidthDip = 120; // 紧凑布局：歌词层宽度随当前行文本自适应
    private double _infoWidthDip = 120;   // 歌曲信息层宽度（歌名/歌手）
    private bool _infoMode;               // 控制器要求显示歌曲信息（暂停超时）
    private bool _hover;
    private bool _showingInfo;            // 当前视觉状态：信息层在显示
    private bool _showingButtons;         // 当前视觉状态：按钮在显示
    private bool _maskShown;              // 当前视觉状态：悬停遮罩在显示
    private bool IsLeftAlign => Cfg.TextAlign == "left";

    // 悬停复查：窗口尺寸/位置变化会让 MouseLeave 误触发，按光标是否仍在窗口矩形内判定
    private readonly System.Windows.Threading.DispatcherTimer _hoverRecheck =
        new() { Interval = TimeSpan.FromMilliseconds(150) };

    private double _displayWidthDip = 120; // 当前实际窗口宽
    private int _lastHeightDip = 48;

    // WinEvent 前台钩子（切前台时即时重摆，不等 1.5s 周期）
    private NativeMethods.WinEventDelegate? _winEventProc;
    private IntPtr _winEventHook;

    private bool _dragging;
    private NativeMethods.POINT _dragCursor0;
    private int _dragWinX0, _dragWinY0;

    private AppConfig Cfg => _app.Cfg;

    /// <summary>原文字号（DIP，对应 Python pt_to_px）。</summary>
    private double OrigFontDip => Cfg.FontSize * 96.0 / 72.0;
    private double TransFontDip => Math.Max(7, Cfg.FontSize - 4) * 96.0 / 72.0;

    public OverlayWindow(MainController app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetLocked(Cfg.Locked);
            Dock();
            // 前台切换即时重摆：任务栏 XAML 层在前台变化瞬间会盖住嵌入窗口，
            // 不等 1.5s 周期兜底，收到事件立刻重断言位置与 z-order
            _winEventProc = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(Dock);
            _winEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        };
        Closed += (_, _) =>
        {
            if (_winEventHook != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(_winEventHook);
        };
        MouseEnter += (_, _) => { _hover = true; UpdateDisplayMode(); };
        MouseLeave += (_, _) => RecheckHover();
        _hoverRecheck.Tick += (_, _) => RecheckHover();
        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonUp += (_, _) => { if (!Cfg.Locked) _app.ShowContextMenu(); };
    }

    private double DpiScaleX() => VisualTreeHelper.GetDpi(this).DpiScaleX;
    private double DpiScaleY() => VisualTreeHelper.GetDpi(this).DpiScaleY;

    // ---- 内容 ----

    /// <summary>切行/切歌时调用：重建行视觉并做滑动动画。
    /// nextLineMode=true（第二行是“下一句”而非译文）时用传送带动画：
    /// 旧块上移一个行距，下一句补位到上行，新的下一句从下面进入。</summary>
    public void SetLine(string original, string translation, IReadOnlyList<KaraokeWord>? words,
        double lineElapsedMs, bool playing, bool nextLineMode = false)
    {
        var animate = original.Length > 0 && _original.Length > 0 && original != _original;
        _original = original;
        _translation = translation;
        SetHasContent(original.Length > 0);

        var oldLine = _currentLine;
        var oldSb = _karaokeStoryboard;
        _karaokeStoryboard = null;

        var (visual, karaoke) = BuildLineVisual(original, translation, words);
        _currentLine = visual;
        _karaoke = karaoke;

        if (animate && oldLine != null)
        {
            var conveyor = nextLineMode
                           && oldLine is StackPanel osp && osp.Children.Count > 1
                           && ((FrameworkElement)osp.Children[0]).ActualHeight > 4;
            if (conveyor)
            {
                // 传送带切行（第二行是下一句）：旧块整体上移一个行距（不淡出），
                // 新块同速从下方进入——旧下行与新上行是同一句，看起来就是它补位上去
                var pitch = ((FrameworkElement)((StackPanel)oldLine).Children[0]).ActualHeight;
                oldLine.RenderTransform = new TranslateTransform();
                visual.RenderTransform = new TranslateTransform(0, pitch);
                LinesHost.Children.Add(visual);

                var sb = new Storyboard();
                AddAnim(sb, oldLine, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"), -pitch, easingIn: false);
                AddAnim(sb, visual, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"), 0, easingIn: false);
                if (visual is StackPanel nsp && nsp.Children.Count > 1)
                {
                    // 新的“下一句”淡入
                    var bottom = (FrameworkElement)nsp.Children[1];
                    bottom.Opacity = 0;
                    AddAnim(sb, bottom, new PropertyPath("Opacity"), 1, easingIn: false);
                }
                var oldRef = oldLine;
                sb.Completed += (_, _) =>
                {
                    LinesHost.Children.Remove(oldRef);
                    oldSb?.Stop(this);
                };
                sb.Begin(this);
            }
            else
            {
                // 整行滚动切行：旧行整体上移一个行块高度淡出，新行从下方滚入（三次缓出）
                var dist = oldLine.ActualHeight > 4 ? oldLine.ActualHeight : 24;
                oldLine.RenderTransform = new TranslateTransform();
                visual.RenderTransform = new TranslateTransform(0, dist);
                visual.Opacity = 0;
                LinesHost.Children.Add(visual);

                var sb = new Storyboard();
                AddAnim(sb, oldLine, new PropertyPath("Opacity"), 0, easingIn: false);
                AddAnim(sb, oldLine, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"), -dist, easingIn: false);
                AddAnim(sb, visual, new PropertyPath("Opacity"), 1, easingIn: false);
                AddAnim(sb, visual, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"), 0, easingIn: false);
                var oldRef = oldLine;
                sb.Completed += (_, _) =>
                {
                    LinesHost.Children.Remove(oldRef);
                    oldSb?.Stop(this); // 旧行的逐字动画随淡出结束停止
                };
                sb.Begin(this);
            }
        }
        else
        {
            if (oldLine != null) LinesHost.Children.Remove(oldLine);
            oldSb?.Stop(this);
            LinesHost.Children.Clear();
            LinesHost.Children.Add(visual);
        }

        StartKaraoke(karaoke, words, lineElapsedMs, playing);

        // 紧凑布局：宽度随当前行文本自适应（Cfg.Width 作为最大宽度，超出由 FitFont 缩字号）
        var origW = karaoke.MeasureLineWidth();
        var transW = translation.Length > 0 && Cfg.SecondLine != "off"
            ? KaraokeText.MeasureTextWidth(translation, new FontFamily(Cfg.FontFamily), TransFontDip, DpiScaleX())
            : 0;
        _lyricsWidthDip = Math.Clamp(Math.Max(origW, transW) + (IsLeftAlign ? 16 : 28), 80, Cfg.Width);
        Dock();
    }

    /// <summary>更新当前媒体信息（每拍调用，内部去重）。悬停/暂停超时时代替歌词显示。</summary>
    public void SetMedia(string title, string artist, bool force = false)
    {
        if (!force && title == _title && artist == _artist) return;
        _title = title;
        _artist = artist;

        var family = new FontFamily(Cfg.FontFamily);
        var align = IsLeftAlign ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        TitleText.Text = title;
        TitleText.FontFamily = family;
        TitleText.FontSize = OrigFontDip;
        TitleText.LineHeight = Math.Ceiling(OrigFontDip * 1.1);
        TitleText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        TitleText.Foreground = new SolidColorBrush(ParseColor(Cfg.TextColor, Colors.White));
        TitleText.HorizontalAlignment = align;
        ArtistText.Text = artist;
        ArtistText.FontFamily = family;
        ArtistText.FontSize = TransFontDip;
        ArtistText.LineHeight = Math.Ceiling(TransFontDip * 1.1);
        ArtistText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        ArtistText.Foreground = new SolidColorBrush(ParseColor(Cfg.TransColor, Color.FromRgb(0xC8, 0xC8, 0xC8)));
        ArtistText.HorizontalAlignment = align;
        ArtistText.Visibility = artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var dpi = DpiScaleX();
        var tw = KaraokeText.MeasureTextWidth(title, family, OrigFontDip, dpi);
        var aw = artist.Length > 0 ? KaraokeText.MeasureTextWidth(artist, family, TransFontDip, dpi) : 0;
        _infoWidthDip = Math.Clamp(Math.Max(tw, aw) + (IsLeftAlign ? 16 : 28), 80, Cfg.Width);
        Dock();
    }

    /// <summary>控制器驱动的信息显示模式（暂停一段时间后为 true，悬停时无视此标志强制信息层）。</summary>
    public void SetInfoMode(bool info)
    {
        if (info == _infoMode) return;
        _infoMode = info;
        UpdateDisplayMode();
    }

    /// <summary>按悬停/信息模式切换显示层：歌词 ⇄ 歌曲信息交叉淡变 + 遮罩淡入，
    /// 按钮列宽度做 0→96 展开动画、内容列随之平滑右移（Lyricify 式连贯展开）。
    /// 窗口尺寸瞬时到位（透明区不可见），不做 Win32 逐帧缩放（会掉帧）。</summary>
    private void UpdateDisplayMode()
    {
        var showInfo = _hover || _infoMode;
        var showButtons = _hover && Cfg.ShowControls;
        if (showInfo == _showingInfo && showButtons == _showingButtons && _hover == _maskShown) return;
        var wasButtons = _showingButtons;
        _showingInfo = showInfo;
        _showingButtons = showButtons;
        _maskShown = _hover;
        UpdateVisibility(); // 信息显示模式也决定可见性（无歌词行时仍显示歌名/歌手）

        FadeTo(LinesHost, showInfo ? 0.0 : 1.0);
        FadeTo(InfoPanel, showInfo ? 1.0 : 0.0);
        FadeTo(HoverMask, _hover ? 1.0 : 0.0); // 悬停遮罩（对标 Win11 任务栏图标）
        ButtonsPanel.IsHitTestVisible = showButtons;
        FadeTo(ButtonsPanel, showButtons ? 1.0 : 0.0);
        if (showButtons)
        {
            // 按钮轻微右滑入场
            var tt = new TranslateTransform(-6, 0);
            ButtonsPanel.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, AnimFade)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        if (_hover)
        {
            Dock(); // 尺寸瞬时到位，透明区看不出变化
            if (showButtons)
            {
                // 按钮列 0→96 展开，内容列随之平滑右移（Lyricify 式连贯展开）
                ButtonsHost.Width = 0;
                AnimateButtonsHost(ButtonsWidth, snapDockOnComplete: false);
            }
        }
        else if (wasButtons)
        {
            // 收起：按钮列收拢动画结束后再瞬时还原窗口尺寸，避免可见跳变
            AnimateButtonsHost(0, snapDockOnComplete: true);
        }
        else
        {
            Dock();
        }
    }

    /// <summary>按钮列宽度展开/收拢动画（200ms 三次缓出）。</summary>
    private void AnimateButtonsHost(double to, bool snapDockOnComplete)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (snapDockOnComplete)
            anim.Completed += (_, _) => Dock();
        ButtonsHost.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>悬停判定：以光标是否仍在窗口矩形内为准（窗口整体检测，
    /// 尺寸/位置变化造成的 MouseLeave 不误判为移出）。</summary>
    private void RecheckHover()
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hoverRecheck.Stop();
            _hover = false;
            UpdateDisplayMode();
            return;
        }
        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.GetWindowRect(_hwnd, out var rc);
        if (pt.X >= rc.Left - 2 && pt.X <= rc.Right + 2 && pt.Y >= rc.Top - 2 && pt.Y <= rc.Bottom + 2)
        {
            _hoverRecheck.Start(); // 仍在窗口内：周期复查直到真正移出
            return;
        }
        _hoverRecheck.Stop();
        _hover = false;
        UpdateDisplayMode();
    }

    private static void FadeTo(UIElement el, double target)
        => el.BeginAnimation(OpacityProperty, new DoubleAnimation(target, AnimFade));

    private static void AddAnim(Storyboard sb, FrameworkElement target, PropertyPath path, double to, bool easingIn)
    {
        var anim = new DoubleAnimation(to, AnimLine)
        {
            EasingFunction = easingIn
                ? new QuadraticEase { EasingMode = EasingMode.EaseIn }
                : new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        sb.Children.Add(anim);
    }

    private (StackPanel Visual, KaraokeText Karaoke) BuildLineVisual(
        string original, string translation, IReadOnlyList<KaraokeWord>? words)
    {
        var textColor = ParseColor(Cfg.TextColor, Colors.White);
        var bright = Freeze(new SolidColorBrush(textColor));
        var pending = Freeze(new SolidColorBrush(Color.FromArgb(140, textColor.R, textColor.G, textColor.B)));

        var align = IsLeftAlign ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        var karaoke = new KaraokeText();
        karaoke.SetLine(original, words, new FontFamily(Cfg.FontFamily), OrigFontDip, bright, pending,
            Cfg.Shadow, align);

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(karaoke);
        if (translation.Length > 0 && Cfg.SecondLine != "off")
        {
            var transBrush = Freeze(new SolidColorBrush(ParseColor(Cfg.TransColor, Color.FromRgb(0xC8, 0xC8, 0xC8))));
            var transFamily = new FontFamily(Cfg.FontFamily);
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = translation,
                Foreground = transBrush,
                FontFamily = transFamily,
                FontSize = TransFontDip,
                HorizontalAlignment = align,
                Margin = new Thickness(0, 3, 0, 0), // 与原文之间的行距
                LineHeight = Math.Ceiling(TransFontDip * 1.3), // 与原文同规则固定行高，保留行间距
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            if (Cfg.Shadow) // 小字号用更轻的阴影，避免发虚
                tb.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 0, Opacity = 0.5 };
            var hasWords = words != null;
            tb.SizeChanged += (_, _) => UpdateTextScroll(tb, translation, transFamily, align, hasWords);
            sp.Children.Add(tb);
        }
        return (sp, karaoke);
    }

    // 当前译文行的滚动状态（供 SyncProgress 跟随原文进度同步滚动）
    private System.Windows.Controls.TextBlock? _transTb;
    private double _transOverflow;
    private double _lastTransScroll;

    /// <summary>译文明显超宽时左对齐 + 横向滚动：有逐字数据时由 SyncProgress 跟随原文
    /// 进度同步滚动，无逐字数据时往返走马灯兜底（不缩字号）。</summary>
    private void UpdateTextScroll(System.Windows.Controls.TextBlock tb, string text, FontFamily family,
        HorizontalAlignment align, bool hasWords)
    {
        var dpi = VisualTreeHelper.GetDpi(tb).PixelsPerDip;
        var natural = KaraokeText.MeasureTextWidth(text, family, TransFontDip, dpi);
        var avail = tb.ActualWidth - 4;
        var overflow = natural - avail;
        _transTb = tb;
        if (overflow > 10 && avail > 0) // 阈值 10px：微小测量误差不触发，避免无故左右晃
        {
            _transOverflow = overflow;
            tb.Width = natural; // 显式自然宽度，防止被容器裁剪导致滚动看不到尾部
            tb.HorizontalAlignment = HorizontalAlignment.Left;
            if (hasWords) Marquee.Clear(tb); // 跟随模式：由 SyncProgress 驱动
            else Marquee.Apply(tb, overflow); // 无逐字：往返滚动兜底
        }
        else
        {
            _transOverflow = 0;
            tb.Width = double.NaN;
            tb.HorizontalAlignment = align;
            Marquee.Clear(tb);
            tb.RenderTransform = null;
        }
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    // ---- 逐字动画编排 ----

    private void StartKaraoke(KaraokeText? karaoke, IReadOnlyList<KaraokeWord>? words,
        double elapsedMs, bool playing)
    {
        if (karaoke == null || words == null || words.Count == 0) return;
        var last = words[^1];
        _karaokeTotalMs = last.OffsetMs + last.DurationMs + 500; // 结尾保持全亮
        var anim = new DoubleAnimation(0, _karaokeTotalMs, TimeSpan.FromMilliseconds(_karaokeTotalMs));
        Storyboard.SetTarget(anim, karaoke);
        Storyboard.SetTargetProperty(anim, new PropertyPath(KaraokeText.PositionMsProperty));
        _karaokeStoryboard = new Storyboard();
        _karaokeStoryboard.Children.Add(anim);
        _karaokeStoryboard.Begin(this, true);
        _sbPlaying = true;
        SyncProgress(elapsedMs, playing);
    }

    /// <summary>~50ms 周期调用：用 SMTC 本地插值进度 Seek 逐字动画（暂停时冻结）。</summary>
    public void SyncProgress(double lineElapsedMs, bool playing)
    {
        if (_karaokeStoryboard == null) return;
        var t = TimeSpan.FromMilliseconds(Math.Clamp(lineElapsedMs, 0, _karaokeTotalMs));
        _karaokeStoryboard.Seek(this, t, TimeSeekOrigin.BeginTime);
        if (playing != _sbPlaying)
        {
            _sbPlaying = playing;
            if (playing) _karaokeStoryboard.Resume(this);
            else _karaokeStoryboard.Pause(this);
        }
        // 译文跟随原文逐字进度同步横向滚动（重定目标短动画，平滑跟随）
        if (_transTb != null && _transOverflow > 10 && _karaoke != null)
        {
            var target = -_transOverflow * _karaoke.ScrollFraction;
            if (Math.Abs(target - _lastTransScroll) > 0.5)
            {
                _lastTransScroll = target;
                if (_transTb.RenderTransform is not TranslateTransform tt)
                {
                    tt = new TranslateTransform();
                    _transTb.RenderTransform = tt;
                }
                tt.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(120))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    });
            }
        }
    }

    /// <summary>更新播放/暂停按钮字形。</summary>
    public void SetPlaying(bool playing)
    {
        BtnPlayPause.Content = playing ? "\uE769" : "\uE768"; // 暂停 / 播放
    }

    // ---- 封面 ----

    public void SetCover(BitmapSource? image)
    {
        CoverRect.Fill = image != null ? new ImageBrush(image) : null;
    }

    // ---- 可见性 ----

    private void SetHasContent(bool has)
    {
        if (has == _hasContent) return;
        _hasContent = has;
        UpdateVisibility();
        if (has) Dock();
    }

    public void UpdateFullscreen()
    {
        var hidden = Cfg.HideOnFullscreen
                     && _hwnd != IntPtr.Zero
                     && NativeMethods.IsFullscreenForeground(_hwnd, _hwnd);
        if (hidden != _fsHidden)
        {
            _fsHidden = hidden;
            UpdateVisibility();
        }
    }

    private void UpdateVisibility()
    {
        // 暂停超时时即使还没到第一句歌词，也显示歌曲信息（歌名/歌手）
        var show = (_hasContent || _infoMode) && !_fsHidden;
        // 用 Win32 ShowWindow 直接控制：手动 SetParent 后 WPF 的 Visibility
        // 属性与 Win32 的 WS_VISIBLE 会脱钩（WPF 认为可见但窗口实际不显示）
        if (_hwnd != IntPtr.Zero)
            NativeMethods.ShowWindow(_hwnd, show ? NativeMethods.SW_SHOWNA : NativeMethods.SW_HIDE);
        else
            Visibility = show ? Visibility.Visible : Visibility.Hidden; // 句柄创建前的启动期
    }

    // ---- 锁定 ----

    public void SetLocked(bool locked)
    {
        if (_hwnd != IntPtr.Zero)
            NativeMethods.SetClickThrough(_hwnd, locked);
    }

    // ---- 布局 ----

    private int CurrentHeightDip()
    {
        if (Cfg.Mode == "taskbar")
        {
            var (tray, _) = NativeMethods.ResolveTaskbar(Cfg.Monitor);
            if (tray != IntPtr.Zero && NativeMethods.GetClientRect(tray, out var rc))
                return (int)Math.Max(24, rc.Height / DpiScaleY());
            return 48;
        }
        // 浮动模式：按文字行数自适应
        var h = OrigFontDip + 10;
        if (_translation.Length > 0 && Cfg.SecondLine != "off")
            h += TransFontDip + 2;
        return (int)Math.Ceiling(h);
    }

    /// <summary>按当前模式与配置摆放窗口（周期调用以跟随任务栏变化/重建）。</summary>
    public void Dock()
    {
        if (_hwnd == IntPtr.Zero) return;
        var heightDip = CurrentHeightDip();
        _lastHeightDip = heightDip;
        var coverZone = Cfg.ShowCover ? heightDip : 0;
        var buttonsZone = _showingButtons ? ButtonsWidth : 0;
        var contentW = _showingInfo ? _infoWidthDip : _lyricsWidthDip;
        // 悬停时窗口只许变宽不许变窄：否则光标会被收缩的窗口“甩”出去，悬停态来回抖动
        if (_showingButtons)
            contentW = Math.Max(contentW, _lyricsWidthDip);
        var targetWidth = contentW + coverZone + buttonsZone;

        // 视觉布局同步
        Height = heightDip;
        CoverZone.Visibility = Cfg.ShowCover ? Visibility.Visible : Visibility.Collapsed;
        CoverZone.Width = Math.Max(0, heightDip - 12);  // 显式指定宽高 + 垂直居中
        CoverZone.Height = Math.Max(0, heightDip - 12);
        BodyGrid.Margin = new Thickness(coverZone, 0, 0, 0);
        ButtonsHost.Width = _showingButtons ? ButtonsWidth : 0;

        _displayWidthDip = targetWidth;
        Width = targetWidth;
        ApplyPosition();
    }

    /// <summary>按 _displayWidthDip 摆放窗口位置（任务栏挂靠或浮动）。</summary>
    private void ApplyPosition()
    {
        var heightDip = _lastHeightDip;
        var widthPx = (int)Math.Round(_displayWidthDip * DpiScaleX());
        var heightPx = (int)Math.Round(heightDip * DpiScaleY());

        if (Cfg.Mode == "taskbar")
        {
            Topmost = false;
            var (tray, notify) = NativeMethods.ResolveTaskbar(Cfg.Monitor);
            if (tray == IntPtr.Zero) return;
            NativeMethods.MakeChildOf(_hwnd, tray);
            NativeMethods.GetClientRect(tray, out var rc);

            int x;
            switch (Cfg.Position)
            {
                case "custom" when Cfg.XOffset.HasValue:
                    x = Cfg.XOffset.Value;
                    break;
                case "left":
                    x = 8;
                    break;
                case "center":
                    x = (rc.Right - widthPx) / 2;
                    break;
                case "right":
                    x = rc.Right - widthPx - 8;
                    break;
                default: // tray_left：托盘通知区左边
                    var rightEdge = rc.Right;
                    if (notify != IntPtr.Zero)
                    {
                        NativeMethods.GetWindowRect(notify, out var nrc);
                        var pt = new NativeMethods.POINT { X = nrc.Left, Y = nrc.Top };
                        NativeMethods.ScreenToClient(tray, ref pt);
                        rightEdge = pt.X;
                    }
                    x = rightEdge - 12 - widthPx;
                    break;
            }
            x = Math.Clamp(x, 0, Math.Max(0, rc.Right - widthPx));
            // 不带 SWP_NOZORDER：断言为任务栏子窗口最顶层，
            // 任务栏内部重排（如悬停图标弹出预览）后仍保持可见
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, 0, widthPx, heightPx,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
        else // floating
        {
            NativeMethods.MakePopup(_hwnd, topmost: true);
            Topmost = true;
            int x, y;
            if (Cfg.FloatX.HasValue && Cfg.FloatY.HasValue)
            {
                x = Cfg.FloatX.Value;
                y = Cfg.FloatY.Value;
            }
            else
            {
                var mons = NativeMethods.Monitors();
                var rect = Cfg.Monitor >= 0 && Cfg.Monitor < mons.Count ? mons[Cfg.Monitor].Rect : mons[0].Rect;
                x = (rect.Left + rect.Right - widthPx) / 2;
                y = rect.Bottom - heightPx - 80;
            }
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, widthPx, heightPx,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
        }
    }

    /// <summary>外观/尺寸类设置变化后调用：重新摆放并按当前歌词重建行视觉。</summary>
    public void ApplyLayout(string? currentOriginal, string currentTranslation,
        IReadOnlyList<KaraokeWord>? words, double lineElapsedMs, bool playing)
    {
        Dock();
        if (currentOriginal != null)
            SetLine(currentOriginal, currentTranslation, words, lineElapsedMs, playing);
    }

    // ---- 拖动 / 右键 ----

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (Cfg.Locked || _hwnd == IntPtr.Zero) return;
        NativeMethods.GetCursorPos(out _dragCursor0);
        NativeMethods.GetWindowRect(_hwnd, out var rc);
        _dragWinX0 = rc.Left;
        _dragWinY0 = rc.Top;
        _dragging = true;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _hwnd == IntPtr.Zero) return;
        NativeMethods.GetCursorPos(out var pt);
        var dx = pt.X - _dragCursor0.X;
        var dy = pt.Y - _dragCursor0.Y;
        if (Cfg.Mode == "taskbar")
        {
            var (tray, _) = NativeMethods.ResolveTaskbar(Cfg.Monitor);
            if (tray == IntPtr.Zero) return;
            NativeMethods.GetWindowRect(tray, out var trc);
            Cfg.Position = "custom";
            Cfg.XOffset = _dragWinX0 - trc.Left + dx;
        }
        else
        {
            Cfg.FloatX = _dragWinX0 + dx;
            Cfg.FloatY = _dragWinY0 + dy;
        }
        Dock();
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        _app.SaveCfg();
    }

    // ---- 播放控制按钮 ----

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => _app.Control("prev");
    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => _app.Control("play_pause");
    private void BtnNext_Click(object sender, RoutedEventArgs e) => _app.Control("next");
}
