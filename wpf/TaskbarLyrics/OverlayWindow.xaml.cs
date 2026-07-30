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
    private static readonly TimeSpan AnimLine = TimeSpan.FromMilliseconds(320); // 切行动画
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
    // 居中对齐时窗口以「中心点」为锚：每行歌词宽度自适应变化时文字中心不左右漂移
    private bool CenterAnchored => Cfg.TextAlign != "left";
    private double _lastCoverZoneDip;     // Dock 时记录的封面区宽度（中心补偿用）

    // 悬停判定：100ms 轮询光标是否在窗口矩形内。
    // 不用 MouseEnter/Leave 事件——透明分层窗里移到无内容区域会误触发 Leave，
    // 悬停引起的窗口尺寸变化又会触发假 Enter，事件抖动在浮动模式下形成来回闪烁；
    // 轮询下悬停态只由光标几何位置决定，不可能来回跳变
    private readonly System.Windows.Threading.DispatcherTimer _hoverPoll =
        new() { Interval = TimeSpan.FromMilliseconds(100) };

    private double _displayWidthDip = 120; // 当前实际窗口宽
    private int _lastHeightDip = 48;
    private (int L, int R)? _autoGap;      // 自动避让算出的任务栏空档（client 像素坐标）

    // WinEvent 前台钩子（切前台时即时重摆，不等 1.5s 周期）
    private NativeMethods.WinEventDelegate? _winEventProc;
    private IntPtr _winEventHook;

    private bool _dragging;
    private NativeMethods.POINT _dragCursor0;
    private int _dragWinX0, _dragWinY0;
    // 浮动模式上次摆放的位置尺寸：没变就不调 SetWindowPos（防分层窗重合成闪烁）
    private int _lastFloatX = int.MinValue, _lastFloatY, _lastFloatW, _lastFloatH;

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
            ApplyTextRendering();
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
        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonUp += (_, _) => { if (!Cfg.Locked) _app.ShowContextMenu(); };
        _hoverPoll.Tick += (_, _) => PollHover();
        _hoverPoll.Start();
    }

    /// <summary>悬停轮询：光标在窗口矩形内即视为悬停（窗口整体检测）。
    /// 锁定（鼠标穿透）或窗口隐藏时不存在悬停。</summary>
    private void PollHover()
    {
        if (_hwnd == IntPtr.Zero) return;
        var inside = false;
        if (!Cfg.Locked
            && (NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE)
                & NativeMethods.WS_VISIBLE) != 0)
        {
            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.GetWindowRect(_hwnd, out var rc);
            // 退出带 4px 滞后：边界附近/窗口几何变化时悬停态不闪变
            const int hys = 4;
            inside = _hover
                ? pt.X >= rc.Left - hys && pt.X <= rc.Right + hys
                  && pt.Y >= rc.Top - hys && pt.Y <= rc.Bottom + hys
                : pt.X >= rc.Left && pt.X <= rc.Right && pt.Y >= rc.Top && pt.Y <= rc.Bottom;
        }
        if (inside != _hover)
        {
            _hover = inside;
            UpdateDisplayMode();
        }
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

        // 切行先清掉上一行的译文滚动状态：新行无译文时，
        // 残留状态会让跟随滚动逻辑在已移除的旧元素上空跑
        _transTb = null;
        _transOverflow = 0;
        _lastTransScroll = 0;

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
                // 新块同速从下方进入——旧下行与新上行是同一句，看起来就是它补位上去。
                // 行距必须取第二行的实际偏移（含两行间距）：只算第一行高度会差 3px，
                // 旧下行和新上行永远错开，动画全程重影、结尾还跳一下（「残留」的根因）
                var osp2 = (StackPanel)oldLine;
                var pitch = ((FrameworkElement)osp2.Children[1])
                    .TransformToAncestor(osp2).Transform(new Point(0, 0)).Y;
                if (pitch < 4) // 兜底：测量失败退回第一行高度
                    pitch = ((FrameworkElement)osp2.Children[0]).ActualHeight;
                oldLine.RenderTransform = new TranslateTransform();
                visual.RenderTransform = new TranslateTransform(0, pitch);
                LinesHost.Children.Add(visual);

                var sb = new Storyboard();
                AddAnim(sb, oldLine, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"), -pitch, easingIn: false);
                AddAnim(sb, visual, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"), 0, easingIn: false);
                // 共享句 morph：旧「下一句」是小号灰字、新「当前句」是大号白字，
                // 刚性平移会让两种渲染全程叠影（「残留」的根因）——
                // 旧下行在滑行中淡出、新上行淡入，小灰字滑上去的同时变成大白字
                var oldBottom = (FrameworkElement)osp2.Children[1];
                AddAnim(sb, oldBottom, new PropertyPath("Opacity"), 0, easingIn: false);
                if (visual is StackPanel nsp && nsp.Children.Count > 1)
                {
                    var top = (FrameworkElement)nsp.Children[0];
                    top.Opacity = 0;
                    AddAnim(sb, top, new PropertyPath("Opacity"), 1, easingIn: false);
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
        TitleText.FontWeight = Cfg.FontBold ? FontWeights.SemiBold : FontWeights.Normal; // 与歌词原文一致
        TitleText.LineHeight = Math.Ceiling(OrigFontDip * 1.3); // 与歌词行同规则固定行高
        TitleText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        TitleText.Foreground = new SolidColorBrush(ParseColor(Cfg.TextColor, Colors.White));
        TitleText.HorizontalAlignment = align;
        ArtistText.Text = artist;
        ArtistText.FontFamily = family;
        ArtistText.FontSize = TransFontDip;
        ArtistText.LineHeight = Math.Ceiling(TransFontDip * 1.3);
        ArtistText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        ArtistText.Margin = new Thickness(0, 3, 0, 0); // 与歌词行相同的上下间距
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
        var weight = Cfg.FontBold ? FontWeights.SemiBold : FontWeights.Normal; // 原文半粗更清晰，译文保持常规
        var karaoke = new KaraokeText();
        karaoke.SetLine(original, words, new FontFamily(Cfg.FontFamily), OrigFontDip, bright, pending,
            Cfg.Shadow, align, weight);

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
        _lastCoverZoneDip = coverZone;
        var buttonsZone = _showingButtons ? ButtonsWidth : 0;
        var contentW = _showingInfo ? _infoWidthDip : _lyricsWidthDip;
        // 悬停时窗口只许变宽不许变窄：否则光标会被收缩的窗口“甩”出去，悬停态来回抖动
        if (_showingButtons)
            contentW = Math.Max(contentW, _lyricsWidthDip);
        var targetWidth = contentW + coverZone + buttonsZone;

        // 自动避让任务栏元素：窗口宽度跟随空档——空档内尽量用满
        // （上限 = 设置的最大宽度 + 封面 + 按钮），空档收窄时同步收窄，
        // 空间怎么变窗口就怎么变；UIA 失败时沿用上次成功的空档
        if (Cfg.Mode == "taskbar" && Cfg.AutoPosition)
        {
            var (trayHwnd, _) = NativeMethods.ResolveTaskbar(Cfg.Monitor);
            if (trayHwnd != IntPtr.Zero)
            {
                var dpi = DpiScaleX();
                var wantDip = Cfg.Width + coverZone + buttonsZone;
                var gap = TaskbarFreeSpace.FindBestGap(
                    trayHwnd, _hwnd, (int)Math.Round(wantDip * dpi), CurrentTrayX(trayHwnd), Cfg.AutoSide);
                if (gap.HasValue) _autoGap = gap.Value; // 失败则沿用旧空档，不回退跳位
                if (_autoGap.HasValue)
                {
                    var g = _autoGap.Value;
                    var gapDip = (g.R - g.L - 8) / dpi; // 两侧各留 4px，不与图标贴边
                    var want = Math.Min(gapDip, wantDip);
                    if (want < 40) want = Math.Max(24, gapDip);
                    contentW = Math.Max(24, want - coverZone - buttonsZone);
                    targetWidth = contentW + coverZone + buttonsZone;
                }
            }
        }
        else
        {
            _autoGap = null;
        }

        // 视觉布局同步
        Height = heightDip;
        CoverZone.Visibility = Cfg.ShowCover ? Visibility.Visible : Visibility.Collapsed;
        CoverZone.Width = Math.Max(0, heightDip - 12);  // 显式指定宽高 + 垂直居中
        CoverZone.Height = Math.Max(0, heightDip - 12);
        BodyGrid.Margin = new Thickness(coverZone, 0, 0, 0);
        ButtonsHost.Width = _showingButtons ? ButtonsWidth : 0;

        _displayWidthDip = targetWidth;
        Width = targetWidth;
        FloatBg.Visibility = Cfg.Mode == "taskbar" ? Visibility.Collapsed : Visibility.Visible;
        // 悬停遮罩只包实际内容（封面+按钮+当前显示的文字）：窗口为防甩动会保留
        // 歌词宽度（长歌词时=最大宽度），遮罩若按窗口铺会显得比文字宽一截；
        // 居中对齐时文字在加宽内容区里居中，遮罩要加上这段偏移
        var displayedW = _showingInfo ? _infoWidthDip : _lyricsWidthDip;
        var textOffset = IsLeftAlign ? 0 : Math.Max(0, (contentW - displayedW) / 2);
        HoverMask.Width = Math.Max(24, coverZone + buttonsZone + textOffset + displayedW - 4);
        ApplyPosition();
    }

    /// <summary>窗口当前在任务栏 client 坐标系里的 x（自动避让就近选档用）。</summary>
    private int CurrentTrayX(IntPtr trayHwnd)
    {
        NativeMethods.GetWindowRect(_hwnd, out var wrc);
        NativeMethods.GetWindowRect(trayHwnd, out var trc);
        return wrc.Left - trc.Left;
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
            _lastFloatX = int.MinValue; // 模式切回浮动时强制重新摆放（父窗口/样式都变过）
            var (tray, notify) = NativeMethods.ResolveTaskbar(Cfg.Monitor);
            if (tray == IntPtr.Zero) return;
            NativeMethods.MakeChildOf(_hwnd, tray);
            NativeMethods.GetClientRect(tray, out var rc);

            int x;
            var coverZonePx = (int)Math.Round(_lastCoverZoneDip * DpiScaleX());
            if (_autoGap.HasValue)
            {
                // 自动避让：按「停靠对齐」钉空档的边缘/居中（各留 4px）。
                // 默认左对齐钉左缘：悬停加按钮时窗口向右扩，封面不推图标
                var g = _autoGap.Value;
                x = Cfg.AutoAlign switch
                {
                    "right" => g.R - 4 - widthPx,
                    "center" => (g.L + g.R - widthPx) / 2,
                    _ => g.L + 4,
                };
            }
            else if (Cfg.Position == "custom" && (Cfg.XOffset.HasValue || Cfg.XCenter.HasValue))
            {
                if (CenterAnchored)
                {
                    // 旧配置迁移：只有左缘锚点时按当前窗口宽折算中心点，此刻视觉位置不变，
                    // 之后每行宽度自适应变化都以中心点为锚（文字中心不漂移）
                    Cfg.XCenter ??= (Cfg.XOffset ?? 0) + widthPx / 2;
                    x = Cfg.XCenter.Value - widthPx / 2;
                }
                else
                {
                    x = Cfg.XOffset ?? Cfg.XCenter!.Value - widthPx / 2;
                }
            }
            else if (Cfg.Position == "left")
            {
                x = 8;
            }
            else if (Cfg.Position == "center")
            {
                // 居中锚文字内容而非整窗：补偿左侧封面区，否则文字偏右半个封面宽
                x = CenterAnchored
                    ? (rc.Right - widthPx - coverZonePx) / 2
                    : (rc.Right - widthPx) / 2;
            }
            else if (Cfg.Position == "right")
            {
                x = rc.Right - widthPx - 8;
            }
            else // tray_left：托盘通知区左边
            {
                var rightEdge = rc.Right;
                if (notify != IntPtr.Zero)
                {
                    NativeMethods.GetWindowRect(notify, out var nrc);
                    var pt = new NativeMethods.POINT { X = nrc.Left, Y = nrc.Top };
                    NativeMethods.ScreenToClient(tray, ref pt);
                    rightEdge = pt.X;
                }
                x = rightEdge - 12 - widthPx;
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
            // 居中对齐的中心锚点迁移（同任务栏 custom）：旧 float_x 左缘折算为中心点
            if (CenterAnchored && !Cfg.FloatCx.HasValue && Cfg.FloatX.HasValue)
                Cfg.FloatCx = Cfg.FloatX.Value + widthPx / 2;
            if (Cfg.FloatY.HasValue && (Cfg.FloatX.HasValue || Cfg.FloatCx.HasValue))
            {
                x = CenterAnchored && Cfg.FloatCx.HasValue
                    ? Cfg.FloatCx.Value - widthPx / 2
                    : (Cfg.FloatX ?? Cfg.FloatCx!.Value - widthPx / 2);
                y = Cfg.FloatY.Value;
            }
            else
            {
                var mons = NativeMethods.Monitors();
                var rect = Cfg.Monitor >= 0 && Cfg.Monitor < mons.Count ? mons[Cfg.Monitor].Rect : mons[0].Rect;
                x = (rect.Left + rect.Right - widthPx) / 2;
                y = rect.Bottom - heightPx - 80;
            }
            // 防拖丢：窗口至少保留 48px 在虚拟屏幕内（拖出屏幕外就找不回来了）
            var (vx, vy, vw, vh) = NativeMethods.VirtualScreenRect();
            x = Math.Clamp(x, vx - widthPx + 48, vx + vw - 48);
            y = Math.Clamp(y, vy, vy + vh - 48);
            // 位置尺寸没变就不动窗口：分层透明窗每次 SetWindowPos 都会整张重合成，
            // 周期重摆/前台切换钩子触发的无谓调用会表现为一闪一闪；
            // 也不需要 FRAMECHANGED（样式由 MakePopup 自己负责）
            if (x == _lastFloatX && y == _lastFloatY && widthPx == _lastFloatW && heightPx == _lastFloatH)
                return;
            _lastFloatX = x;
            _lastFloatY = y;
            _lastFloatW = widthPx;
            _lastFloatH = heightPx;
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, widthPx, heightPx,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        }
    }

    /// <summary>外观/尺寸类设置变化后调用：重新摆放并按当前歌词重建行视觉。</summary>
    public void ApplyLayout(string? currentOriginal, string currentTranslation,
        IReadOnlyList<KaraokeWord>? words, double lineElapsedMs, bool playing)
    {
        ApplyTextRendering();
        Dock();
        if (currentOriginal != null)
            SetLine(currentOriginal, currentTranslation, words, lineElapsedMs, playing);
    }

    /// <summary>按字重选择文字渲染：粗笔画在分层透明窗上用 ClearType 子像素渲染
    /// 会在边缘留下彩色毛边（加粗后「毛刺感」的来源），灰阶抗锯齿配粗笔画更干净；
    /// 细字重则仍需 ClearType 保持锐利。</summary>
    private void ApplyTextRendering()
    {
        RenderOptions.SetClearTypeHint(Root,
            Cfg.FontBold ? ClearTypeHint.Auto : ClearTypeHint.Enabled);
    }

    // ---- 拖动 / 右键 ----

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (Cfg.Locked || _hwnd == IntPtr.Zero) return;
        if (Cfg.Mode == "taskbar" && Cfg.AutoPosition) return; // 自动避让模式下拖动停用
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
        var widthPx = (int)Math.Round(_displayWidthDip * DpiScaleX());
        if (Cfg.Mode == "taskbar")
        {
            var (tray, _) = NativeMethods.ResolveTaskbar(Cfg.Monitor);
            if (tray == IntPtr.Zero) return;
            NativeMethods.GetWindowRect(tray, out var trc);
            Cfg.Position = "custom";
            var left = _dragWinX0 - trc.Left + dx;
            if (CenterAnchored)
            {
                Cfg.XCenter = left + widthPx / 2; // 居中模式锚中心点
                Cfg.XOffset = null;
            }
            else
            {
                Cfg.XOffset = left;
                Cfg.XCenter = null;
            }
        }
        else
        {
            var left = _dragWinX0 + dx;
            if (CenterAnchored)
            {
                Cfg.FloatCx = left + widthPx / 2;
                Cfg.FloatX = null;
            }
            else
            {
                Cfg.FloatX = left;
                Cfg.FloatCx = null;
            }
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
