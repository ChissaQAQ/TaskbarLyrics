// 逐字歌词控件：暗色 TextBlock（底层，未唱）+ 亮色 TextBlock（上层，唱过），
// 上层用 Clip 矩形裁剪。PositionMs 依赖属性由 Storyboard 按逐字时间戳驱动，
// OnPropertyChanged 时按「唱过的字全亮 + 正在唱的字按比例」重算 Clip 边界
// （对应 Python render.py 的 _karaoke_boundary_x）。
// 超长歌词不缩字号，由横向滚动（有逐字数据时跟随进度，否则走马灯兜底）呈现。
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TaskbarLyrics;

/// <summary>按无限宽度测量子文本的容器。
/// WPF 的 TextBlock 按「测量宽度」排版文本：父容器按容器宽测量时，
/// 超宽部分在排版阶段就被丢弃——即使之后给足排列宽度、加滚动变换也救不回来
/// （超长歌词滚动后尾部不显示的根因）。这里让子文本始终按自然宽度排版，
/// 自身宽度仍服从容器（供 StackPanel 拉伸、滚动裁剪逻辑测量视口宽）。</summary>
public class NaturalMeasureGrid : Grid
{
    protected override Size MeasureOverride(Size constraint)
    {
        var childConstraint = new Size(double.PositiveInfinity, constraint.Height);
        var maxH = 0.0;
        var maxW = 0.0;
        foreach (UIElement child in Children)
        {
            child.Measure(childConstraint);
            maxH = Math.Max(maxH, child.DesiredSize.Height);
            maxW = Math.Max(maxW, child.DesiredSize.Width);
        }
        // 宽度服从容器上限（不撑开父级），高度取子元素最大
        var w = double.IsInfinity(constraint.Width) ? maxW : Math.Min(constraint.Width, maxW);
        return new Size(w, maxH);
    }
}

/// <summary>垂直堆叠容器，子元素按自然宽度测量并排列（原文/译文两行的宿主）。
///
/// 必须自己实现而不能用 StackPanel：StackPanel 按容器宽（视口）测量子元素，
/// WPF 的 MeasureCore 会把超宽子元素的 DesiredSize 裁到容器宽并记为 clipped，
/// 于是排列阶段给它补一个到「布局槽」的 layout clip——而 layout clip 在
/// RenderTransform 之前生效：内容先被裁成视口宽，再整体左移，滚过头就整行移出视口
/// （「滚到后半段整行不见了」的根因，用 LayoutInformation.GetLayoutClip 可直接看到）。
/// 这里给子元素无限宽测量、按自然宽度排列，让它们不带 layout clip；
/// 视口裁剪由外层 ClipToBounds 容器统一负责。</summary>
public class NaturalMeasureStack : Panel
{
    protected override Size MeasureOverride(Size constraint)
    {
        var childConstraint = new Size(double.PositiveInfinity, constraint.Height);
        var w = 0.0;
        var h = 0.0;
        foreach (UIElement child in Children)
        {
            child.Measure(childConstraint);
            w = Math.Max(w, child.DesiredSize.Width);
            h += child.DesiredSize.Height;
        }
        // 自身宽度服从容器（自身被裁无妨——不带滚动变换），高度为各行之和
        return new Size(double.IsInfinity(constraint.Width) ? w : Math.Min(constraint.Width, w), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        foreach (UIElement child in Children)
        {
            var h = child.DesiredSize.Height;
            // 排列宽度取自然宽度：小于 DesiredSize 就会触发 WPF 的 layout clip
            child.Arrange(new Rect(0, y, Math.Max(finalSize.Width, child.DesiredSize.Width), h));
            y += h;
        }
        return finalSize;
    }
}

/// <summary>横向走马灯：超长文本在可用宽度内往返滚动（正弦缓动，两端自然减速）。</summary>
internal static class Marquee
{
    public static void Apply(FrameworkElement el, double overflow)
    {
        if (el.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }
        var seconds = Math.Clamp(overflow / 28.0, 2.0, 10.0); // 28px/s，限幅
        var anim = new DoubleAnimation(0, -overflow, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    public static void Clear(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.XProperty, null); // 只停动画，保留变换供跟随滚动复用
    }
}

/// <summary>文字阴影的共享实例（原文常规 / 原文加粗 / 第二行）。
///
/// 每切一行都 new 一个 Effect 的话，未冻结的 Freezable 要挂一整套变更通知，
/// 还得让渲染层为每个新实例重新建资源；参数只有三种取值，冻结后共享即可。</summary>
internal static class TextShadow
{
    public static readonly DropShadowEffect Thin = Make(6, 0.6);
    public static readonly DropShadowEffect Bold = Make(3.5, 0.45);
    public static readonly DropShadowEffect Second = Make(3, 0.5);

    public static DropShadowEffect For(bool bold) => bold ? Bold : Thin;

    private static DropShadowEffect Make(double blur, double opacity)
    {
        var e = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = blur,
            ShadowDepth = 0,
            Opacity = opacity,
        };
        e.Freeze();
        return e;
    }
}

/// <summary>可横向滚动的文本宿主：文本按自然宽度排版并溢出渲染（见 NaturalMeasureGrid），
/// 超宽时切左对齐 + 平移滚动，由外层的 ClipToBounds 容器裁成视口。
///
/// 视口宽度必须由容器显式告知（SetViewportWidth），不能从自身 ActualWidth 反推：
/// 溢出时子文本会把自身撑到自然宽度，反推得到的「视口」就是文本自己的宽度，
/// 于是 overflow 掉到阈值以下 → 还原布局 → 宽度缩回视口 → 又判定溢出，
/// 在 SizeChanged 里无限自激振荡（长歌词/长译文忽滚忽停、尾部忽有忽无的根因）。</summary>
public abstract class ScrollingTextHost : NaturalMeasureGrid
{
    /// <summary>视口两侧留白：贴着裁剪边缘的字会被切半个笔画。</summary>
    protected const double EdgePad = 4;

    /// <summary>跟随滚动的时间常数（秒）：每帧向目标逼近 1-e^(-dt/Tau)。
    /// 稳态滞后 ≈ 滚动速度 × Tau（约 60px/s × 0.15 ≈ 9px），焦点在视口 35% 处看不出来；
    /// 再大就跟不上快歌，再小则滤不掉逐字边界的速度突变。</summary>
    private const double Tau = 0.15;

    private double _viewportW;
    private double _naturalW;
    private double _targetX;              // 目标左移量（px，非负）
    private double _curX;                 // 当前左移量（每帧向目标逼近）
    private TranslateTransform? _tt;
    private bool _following;              // 已订阅渲染帧
    private double _lastFrameMs = double.NaN;

    /// <summary>可用视口宽度（DIP，已扣除两侧留白）。</summary>
    protected double Avail => _viewportW - EdgePad;
    /// <summary>文本按目标字号的自然排版宽度（DIP）。</summary>
    protected double NaturalWidth => _naturalW;
    /// <summary>当前是否处于滚动状态。</summary>
    public bool Overflowing { get; private set; }
    /// <summary>需要滚动的距离（DIP）；不滚动时为 0。
    /// 按 Avail 算而非视口宽：滚到底时尾字停在距右缘 EdgePad 处，不贴边。</summary>
    public double Overflow => Overflowing ? _naturalW - Avail : 0;
    /// <summary>当前横向滚动比例（0~1；供译文与原文同步滚动）。
    /// 取目标值而非当前视觉值：译文自己也做平滑，两级平滑会累积滞后。</summary>
    public double ScrollFraction { get; private set; }

    protected ScrollingTextHost()
    {
        // 行元素会被切行动画换掉（传送带/淡出后 Remove）。CompositionTarget.Rendering
        // 是静态事件，漏退订会让整行连同文本、阴影一直被根引用着不回收。
        // 走马灯也必须一并停：它是 RepeatBehavior.Forever，元素移出视觉树后
        // 那个 clock 仍挂在 WPF 的 timing tree 上逐帧 tick——无逐字数据的长歌词
        // 每切一行就留下一个，一首歌下来攒几十个白跑的动画
        Unloaded += (_, _) =>
        {
            StopFollowing();
            Marquee.Clear(this);
        };
    }

    /// <summary>容器告知可用视口宽度（DIP）。窗口宽度随歌词长度/任务栏空档变化，
    /// 每次变化都要重算溢出：否则滚动距离一直用切行那一刻的旧视口值，
    /// 窗口变宽后仍按旧值滚动，尾部被推出视口（「滚动后半段不显示」的根因）。</summary>
    public void SetViewportWidth(double dip)
    {
        if (Math.Abs(dip - _viewportW) < 0.5) return;
        _viewportW = dip;
        RefreshOverflow();
    }

    /// <summary>子类在文本/字号变化后告知新的自然宽度。</summary>
    protected void SetNaturalWidth(double natural)
    {
        _naturalW = natural;
        RefreshOverflow();
    }

    private void RefreshOverflow()
    {
        // 只要超出真实视口就滚动。不留「误差容忍阈值」：测量已与排版一致（见 MeasureTextWidth），
        // 阈值只会变成死区——文字确实超了视口却不滚，尾部被裁掉那一点（阈值有多大就能裁多少）。
        // 视口宽由窗口宽决定、与本元素是否滚动无关，所以不存在状态来回翻转的反馈回路
        Overflowing = _viewportW > 0 && _naturalW > _viewportW;
        if (!Overflowing)
        {
            ScrollFraction = 0;
            StopFollowing();
            _targetX = 0;
            _curX = 0;
            _tt = null;
            RenderTransform = null;
        }
        else
        {
            // 视口/文本变了，滚动行程也变了：把当前位移按新行程夹住，避免超出后被拉回
            _targetX = Math.Clamp(_targetX, 0, Overflow);
            _curX = Math.Clamp(_curX, 0, Overflow);
        }
        OnOverflowChanged(Overflowing);
    }

    /// <summary>溢出状态或滚动距离变化：子类据此切对齐、起停走马灯。</summary>
    protected abstract void OnOverflowChanged(bool overflowing);

    /// <summary>设定目标左移量（自动限幅到 [0, Overflow]），由渲染帧平滑逼近。
    ///
    /// 不能用 BeginAnimation 逐帧重定目标：DoubleAnimation 只给 To 时起点取属性
    /// 「基值」（恒 0）而非当前动画值，于是每帧重启的动画都从 0 跑向新目标、
    /// 只跑出第一小段就被下一帧替换——文本始终追不上目标，直到某个字的间隙里
    /// 目标稳住，动画才跑完猛跳一大截（实测 97% 的帧完全不动，剩下几帧跳 40px，
    /// 也就是长歌词滚动「一卡一卡」的根因）。改为逐渲染帧做一阶低通：
    /// 输出严格连续，逐字边界的速度突变和进度校准的跳变都会被滤平。</summary>
    protected void ScrollToPixels(double px)
    {
        var overflow = Overflow;
        if (overflow <= 0) return;
        var target = Math.Clamp(px, 0, overflow);
        if (Math.Abs(target - _targetX) < 0.01) return;
        _targetX = target;
        ScrollFraction = target / overflow;
        StartFollowing();
    }

    /// <summary>直接把位移设到目标（切行/重建时用，不做动画）。</summary>
    protected void SnapScroll(double px)
    {
        var overflow = Overflow;
        _targetX = overflow > 0 ? Math.Clamp(px, 0, overflow) : 0;
        _curX = _targetX;
        ScrollFraction = overflow > 0 ? _targetX / overflow : 0;
        ApplyOffset();
    }

    private void StartFollowing()
    {
        if (_following) return;
        _following = true;
        _lastFrameMs = double.NaN;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopFollowing()
    {
        if (!_following) return;
        _following = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var ms = e is RenderingEventArgs re ? re.RenderingTime.TotalMilliseconds : double.NaN;
        var dt = 1 / 60.0;
        if (!double.IsNaN(ms))
        {
            if (!double.IsNaN(_lastFrameMs))
                dt = Math.Clamp((ms - _lastFrameMs) / 1000.0, 0.001, 0.1); // 掉帧时限幅，避免一步跳到位
            _lastFrameMs = ms;
        }

        var diff = _targetX - _curX;
        if (Math.Abs(diff) < 0.05)
        {
            _curX = _targetX;
            ApplyOffset();
            StopFollowing(); // 已到位就停订阅，静止时不占渲染回调
            return;
        }
        // 一阶低通（指数逼近）：与帧长无关，掉帧也不会突进
        _curX += diff * (1 - Math.Exp(-dt / Tau));
        ApplyOffset();
    }

    private void ApplyOffset()
    {
        if (_tt == null)
        {
            if (RenderTransform is TranslateTransform existing)
            {
                _tt = existing;
            }
            else
            {
                _tt = new TranslateTransform();
                RenderTransform = _tt;
            }
            // 走马灯留下的动画会锁死 X 属性，本地赋值将无效
            _tt.BeginAnimation(TranslateTransform.XProperty, null);
        }
        _tt.X = -_curX;
    }

    /// <summary>走马灯要接管 X 属性动画，先让平滑跟随让位。</summary>
    protected void ReleaseForMarquee()
    {
        StopFollowing();
        _tt = null;
        _targetX = 0;
        _curX = 0;
    }

    /// <summary>这一行要淡出了：把横向滚动就地定格。
    ///
    /// 切行时只暂停逐字 Storyboard 是不够的——平滑跟随是本类自己的
    /// CompositionTarget.Rendering 订阅，与那个 Storyboard 无关，会继续每帧
    /// 唤醒 UI 线程做低通滤波、改 TranslateTransform.X。旧行正在淡出，没人再看
    /// 它滚到哪，这些帧全是白烧的——而它们正落在切行动画那 320ms 里，
    /// 与新行的滚动、软件光栅化挤同一个 16.7ms 预算。
    /// 停订阅而不复位位移：位置定格在当前处，视觉上没有跳变。
    /// 不碰走马灯——它与平滑跟随互斥，且 BeginAnimation(null) 会让 X 掉回基值 0，
    /// 淡出中的行会横向跳一下。走马灯是属性动画，由渲染线程独立插值，不占 UI 线程。</summary>
    public void FreezeScroll() => StopFollowing();

    /// <summary>本元素所在视觉树的 DPI 缩放（未入树时退回系统主 DPI）。</summary>
    protected double DpiScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return dpi.PixelsPerDip > 0 ? dpi.PixelsPerDip : 1.0;
    }
}

public sealed class KaraokeText : ScrollingTextHost
{
    public const double PendingAlpha = 140.0 / 255.0; // 未唱到歌词的透明度（对应 render.py PENDING_ALPHA）

    /// <summary>正在唱的位置在视口中保持的位置（对标 Lyricify，约 1/3 处）。</summary>
    private const double FocusRatio = 0.35;

    private readonly TextBlock _pending;
    private readonly TextBlock _accent;
    /// <summary>高亮裁剪矩形：只改 Rect 不重建，避免逐帧产生新几何对象。</summary>
    private readonly RectangleGeometry _accentClip = new(new Rect(0, -1000, 0, 12000));
    private IReadOnlyList<KaraokeWord>? _words;
    private List<double> _wordStartX = new();
    private List<double> _wordEndX = new();
    private double _targetFontSize;
    private FontWeight _targetWeight = FontWeights.Normal;
    private string _text = "";
    private HorizontalAlignment _align = HorizontalAlignment.Center;
    /// <summary>切行后首次定位：瞬时到位而不是从行首缓滚过去（从中途接上播放时）。</summary>
    private bool _needSnap = true;

    public static readonly DependencyProperty PositionMsProperty =
        DependencyProperty.Register(nameof(PositionMs), typeof(double), typeof(KaraokeText),
            new FrameworkPropertyMetadata(0.0, (d, _) => ((KaraokeText)d).UpdateClip()));

    /// <summary>行内已播放毫秒数（Storyboard 动画目标）。</summary>
    public double PositionMs
    {
        get => (double)GetValue(PositionMsProperty);
        set => SetValue(PositionMsProperty, value);
    }

    public KaraokeText()
    {
        _pending = MakeBlock();
        _accent = MakeBlock();
        Children.Add(_pending);
        Children.Add(_accent);
    }

    private static TextBlock MakeBlock() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>设置一行歌词。words 为 null 时整行常亮（无逐字数据）。</summary>
    public void SetLine(string text, IReadOnlyList<KaraokeWord>? words, FontFamily family, double fontSizeDip,
        Brush bright, Brush pending, bool shadow, HorizontalAlignment align = HorizontalAlignment.Center,
        FontWeight? weight = null)
    {
        _text = text;
        _words = words;
        _targetFontSize = fontSizeDip;
        _targetWeight = weight ?? FontWeights.Normal;
        _align = align;
        _pending.Text = text;
        _accent.Text = text;
        _pending.FontFamily = family;
        _accent.FontFamily = family;
        _pending.FontSize = fontSizeDip;
        _accent.FontSize = fontSizeDip;
        _pending.FontWeight = _targetWeight;
        _accent.FontWeight = _targetWeight;
        _pending.Foreground = pending;
        _accent.Foreground = bright;
        // 固定行高（1.3 倍字号 ≈ 自然行距）：原文/译文间保留舒适间距，整组垂直居中
        var lh = Math.Ceiling(fontSizeDip * 1.3);
        _pending.LineHeight = lh;
        _accent.LineHeight = lh;
        _pending.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _accent.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        // 阴影挂在 TextBlock 上而非整格：滚动时文本超出容器宽度，
        // 容器级 Effect 会把超出部分先裁进位图，尾部看不到。
        // 粗笔画配大半径阴影会糊成毛边，加粗时换更轻的影子
        var bold = _targetWeight >= FontWeights.SemiBold;
        var effect = shadow ? TextShadow.For(bold) : null;
        _pending.Effect = effect;
        _accent.Effect = effect;
        _pending.Visibility = words != null ? Visibility.Visible : Visibility.Collapsed;
        _accent.Clip = null;
        _needSnap = true; // 从中途接上播放时，滚动位置直接到位，不从行首缓滚一段
        RebuildWordWidths(_targetFontSize);
        // 不给 TextBlock 显式 Width：父容器已让文本按自然宽度排版并溢出渲染，
        // 而显式宽度只能取自测量值——与真实排版宽度的任何偏差都会变成尾部被裁或右侧空隙
        SetNaturalWidth(MeasureWidth(_text, _targetFontSize));
        UpdateClip();
    }

    private double MeasureWidth(string text, double fontSize)
        => MeasureTextWidth(text, _accent.FontFamily, fontSize, DpiScale(), _targetWeight);

    /// <summary>测量任意文本宽度（供译文行与窗口紧凑布局用）。
    ///
    /// 排版模式必须与渲染一致：应用里文字渲染是 TextFormattingMode.Display
    /// （OverlayWindow.xaml 的 Root 上设置，可继承），而 FormattedText 默认用 Ideal。
    /// Ideal 按亚像素精度排版，实测比 Display 的真实排版宽度大 8~17px（越长越大），
    /// 这段虚高会同时污染滚动距离、逐字高亮边界（误差随前缀累积）和窗口宽度。
    /// Display 模式量化到整像素，测出的宽度与 TextBlock 实际排版宽度完全一致。
    ///
    /// 字重也必须与实际渲染一致：雅黑 Bold 比 Regular 宽一截，按细字测量会导致
    /// 窗口定窄（粗字超框）、滚动不触发（以为没超）、逐字边界错位。</summary>
    public static double MeasureTextWidth(string text, FontFamily family, double fontSize,
        double pixelsPerDip, FontWeight? weight = null)
    {
        if (text.Length == 0) return 0;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.Black, null, TextFormattingMode.Display, pixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    /// <summary>按目标字号测量当前行文本宽度（供窗口紧凑布局用）。</summary>
    public double MeasureLineWidth() => MeasureWidth(_text, _targetFontSize);

    /// <summary>文本明显超宽时切换为左对齐 + 横向滚动（有逐字数据时跟随播放进度，
    /// 无逐字数据时往返走马灯兜底）。</summary>
    protected override void OnOverflowChanged(bool overflowing)
    {
        var ha = overflowing ? HorizontalAlignment.Left : _align;
        _pending.HorizontalAlignment = ha;
        _accent.HorizontalAlignment = ha;
        if (overflowing && _words == null)
        {
            ReleaseForMarquee();           // 走马灯要接管 X 属性动画
            Marquee.Apply(this, Overflow); // 无逐字：往返滚动兜底
        }
        else
        {
            Marquee.Clear(this);           // 有逐字：由 UpdateClip 跟随进度滚动
        }
        if (overflowing) UpdateClip();     // 视口变化后立即按新距离重定滚动位置
    }

    /// <summary>预计算每个字的起止 x（相对文本起点），逐帧查表免测量。</summary>
    private void RebuildWordWidths(double fontSize)
    {
        _wordStartX = new List<double>();
        _wordEndX = new List<double>();
        if (_words == null) return;
        var passed = "";
        var prevEnd = 0.0;
        foreach (var w in _words)
        {
            // 起点直接取前一个字的终点，不再单独测一遍：测量次数减半。
            // FormattedText 的构造是切行同步路径上最贵的一环（长句一行几十次），
            // 而它就发生在切行动画启动的前一刻——省下的都是动画首帧的余量
            _wordStartX.Add(prevEnd);
            passed += w.Text;
            prevEnd = MeasureWidth(passed, fontSize);
            _wordEndX.Add(prevEnd);
        }
    }

    /// <summary>按 PositionMs 重算高亮右边界并更新 Clip。</summary>
    private void UpdateClip()
    {
        if (_words == null || _words.Count == 0 || _wordEndX.Count != _words.Count)
        {
            _accent.Clip = null;
            return;
        }
        var elapsed = PositionMs;
        var boundary = 0.0;
        for (var i = 0; i < _words.Count; i++)
        {
            var w = _words[i];
            if (elapsed >= w.OffsetMs + w.DurationMs)
            {
                boundary = _wordEndX[i]; // 已唱完的字全亮
            }
            else if (elapsed >= w.OffsetMs)
            {
                // 正在唱的字按进度比例亮
                var frac = Math.Min(1.0, (elapsed - w.OffsetMs) / Math.Max(1, w.DurationMs));
                boundary = _wordStartX[i] + (_wordEndX[i] - _wordStartX[i]) * frac;
                break;
            }
            else
            {
                break;
            }
        }
        if (boundary < 0) boundary = 0;
        // Clip 只需裁右边界，高度给足避免布局时序问题。改 Rect 而不重建几何：
        // 逐帧 new RectangleGeometry 会每帧重挂 Clip 属性并产生垃圾
        _accentClip.Rect = new Rect(0, -1000, boundary, 12000);
        if (!ReferenceEquals(_accent.Clip, _accentClip)) _accent.Clip = _accentClip;

        // 横向滚动跟随逐字进度：正在唱的位置保持在视口约 35% 处
        if (!Overflowing) return;
        var scrollTo = boundary - Avail * FocusRatio;
        if (_needSnap)
        {
            _needSnap = false;
            SnapScroll(scrollTo);
        }
        else
        {
            ScrollToPixels(scrollTo);
        }
    }
}

/// <summary>第二行文本（译文 / 罗马音 / 下一句）。与原文同样的溢出滚动机制：
/// 原文有逐字数据时按原文进度比例同步滚动，否则走马灯兜底。</summary>
public sealed class TranslationText : ScrollingTextHost
{
    private readonly TextBlock _tb;
    private HorizontalAlignment _align = HorizontalAlignment.Center;
    private bool _followProgress;
    private bool _needSnap = true;

    public TranslationText()
    {
        _tb = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_tb);
    }

    /// <summary>设置第二行文本。followProgress=true 时不启走马灯，
    /// 改由 ScrollToFraction 跟随原文逐字进度。</summary>
    public void SetText(string text, FontFamily family, double fontSizeDip, Brush foreground,
        bool shadow, HorizontalAlignment align, bool followProgress)
    {
        _align = align;
        _followProgress = followProgress;
        _needSnap = true;
        _tb.Text = text;
        _tb.FontFamily = family;
        _tb.FontSize = fontSizeDip;
        _tb.Foreground = foreground;
        _tb.LineHeight = Math.Ceiling(fontSizeDip * 1.3); // 与原文同规则固定行高，保留行间距
        _tb.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        // 小字号用更轻的阴影，避免发虚；同样挂在 TextBlock 上（容器级会裁掉滚动尾部）
        _tb.Effect = shadow ? TextShadow.Second : null;
        SetNaturalWidth(KaraokeText.MeasureTextWidth(text, family, fontSizeDip, DpiScale()));
    }

    protected override void OnOverflowChanged(bool overflowing)
    {
        _tb.HorizontalAlignment = overflowing ? HorizontalAlignment.Left : _align;
        if (overflowing && !_followProgress)
        {
            ReleaseForMarquee();
            Marquee.Apply(this, Overflow); // 无逐字：往返滚动兜底
        }
        else
        {
            Marquee.Clear(this);
        }
    }

    /// <summary>按原文滚动比例同步滚动（0~1）。</summary>
    public void ScrollToFraction(double fraction)
    {
        if (!Overflowing || !_followProgress) return;
        var px = fraction * Overflow;
        if (_needSnap)
        {
            _needSnap = false;
            SnapScroll(px); // 从中途接上播放时直接到位
        }
        else
        {
            ScrollToPixels(px);
        }
    }
}
