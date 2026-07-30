// 逐字歌词控件：暗色 TextBlock（底层，未唱）+ 亮色 TextBlock（上层，唱过），
// 上层用 Clip 矩形裁剪。PositionMs 依赖属性由 Storyboard 按逐字时间戳驱动，
// OnPropertyChanged 时按「唱过的字全亮 + 正在唱的字按比例」重算 Clip 边界
// （对应 Python render.py 的 _karaoke_boundary_x）。
// 超长歌词不缩字号，由 Marquee 横向走马灯滚动（窗口宽度已达上限时）。
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

public sealed class KaraokeText : NaturalMeasureGrid
{
    public const double PendingAlpha = 140.0 / 255.0; // 未唱到歌词的透明度（对应 render.py PENDING_ALPHA）

    private readonly TextBlock _pending;
    private readonly TextBlock _accent;
    private IReadOnlyList<KaraokeWord>? _words;
    private List<double> _wordStartX = new();
    private List<double> _wordEndX = new();
    private double _targetFontSize;
    private FontWeight _targetWeight = FontWeights.Normal;
    private string _text = "";
    private HorizontalAlignment _align = HorizontalAlignment.Center;

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
        SizeChanged += (_, _) => { UpdateScroll(); UpdateClip(); };
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
        _pending.FontWeight = weight ?? FontWeights.Normal;
        _accent.FontWeight = weight ?? FontWeights.Normal;
        _pending.Foreground = pending;
        _accent.Foreground = bright;
        _pending.HorizontalAlignment = align;
        _accent.HorizontalAlignment = align;
        // 固定行高（1.3 倍字号 ≈ 自然行距）：原文/译文间保留舒适间距，整组垂直居中
        var lh = Math.Ceiling(fontSizeDip * 1.3);
        _pending.LineHeight = lh;
        _accent.LineHeight = lh;
        _pending.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _accent.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        // 阴影挂在 TextBlock 上而非整格：走马灯时文本超出容器宽度，
        // 容器级 Effect 会把超出部分先裁进位图，尾部看不到。
        // 粗笔画配大半径阴影会糊成毛边，加粗时换更轻的影子
        var bold = (weight ?? FontWeights.Normal) >= FontWeights.SemiBold;
        var effect = shadow
            ? new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = bold ? 3.5 : 6,
                ShadowDepth = 0,
                Opacity = bold ? 0.45 : 0.6,
            }
            : null;
        _pending.Effect = effect;
        _accent.Effect = effect;
        _pending.Visibility = words != null ? Visibility.Visible : Visibility.Collapsed;
        _accent.Clip = null;
        FitFont();
        UpdateClip();
    }

    private double DpiScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return dpi.PixelsPerDip > 0 ? dpi.PixelsPerDip : 1.0;
    }

    private double MeasureWidth(string text, double fontSize)
        => MeasureTextWidth(text, _accent.FontFamily, fontSize, DpiScale(), _targetWeight);

    /// <summary>测量任意文本宽度（供译文行与窗口紧凑布局用）。
    /// 字重必须与实际渲染一致：雅黑 Bold 比 Regular 宽一截，按细字测量会导致
    /// 窗口定窄（粗字超框）、滚动不触发（以为没超）、逐字边界错位。</summary>
    public static double MeasureTextWidth(string text, FontFamily family, double fontSize,
        double pixelsPerDip, FontWeight? weight = null)
    {
        if (text.Length == 0) return 0;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.Black, pixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    /// <summary>按目标字号测量当前行文本宽度（供窗口紧凑布局用）。</summary>
    public double MeasureLineWidth() => MeasureWidth(_text, _targetFontSize);

    /// <summary>按目标字号渲染并预计算逐字宽度；超长不缩字号，交给滚动。</summary>
    private void FitFont()
    {
        if (_text.Length == 0) return;
        _pending.FontSize = _targetFontSize;
        _accent.FontSize = _targetFontSize;
        // 无条件显式给定自然宽度：DropShadowEffect 会按「排版宽度」先把文本渲成位图，
        // 若在排版后才（在 SizeChanged 的溢出分支里）补宽度，超出容器的尾部已被裁掉，
        // 滚动时也永远看不到（「滚到后面就不显示」的根因）；
        // 短文本设自然宽度后居中/左对齐视觉效果不变
        var natural = MeasureWidth(_text, _targetFontSize);
        _pending.Width = natural;
        _accent.Width = natural;
        RebuildWordWidths(_targetFontSize);
        UpdateScroll();
    }

    /// <summary>当前横向滚动比例（0~1，跟随逐字播放进度；供译文同步滚动）。</summary>
    public double ScrollFraction { get; private set; }

    private double _overflow; // 文本超出视口的宽度（>10 时才启用滚动）

    /// <summary>文本明显超宽时切换为左对齐 + 横向滚动（有逐字数据时跟随播放进度，
    /// 无逐字数据时往返走马灯兜底）。</summary>
    private void UpdateScroll()
    {
        if (_text.Length == 0) return;
        var natural = MeasureWidth(_text, _targetFontSize);
        // 视口宽取父容器：本元素超宽时自身 Width 会被设为自然宽度，
        // 再拿 ActualWidth 当视口就错了
        var viewportW = (Parent as FrameworkElement)?.ActualWidth ?? ActualWidth;
        var avail = viewportW - 4;
        _overflow = natural - avail;
        if (_overflow > 10 && avail > 0) // 阈值 10px：微小测量误差不触发，避免无故左右晃
        {
            // 自身也要给足自然宽度：带平移变换的元素会先按自身边界渲成中间表面，
            // 自身只有视口宽的话，内容先被裁成视口宽再移动，滚动也看不到尾部
            Width = natural;
            _pending.HorizontalAlignment = HorizontalAlignment.Left;
            _accent.HorizontalAlignment = HorizontalAlignment.Left;
            if (_words == null)
                Marquee.Apply(this, _overflow); // 无逐字：往返滚动兜底
            else
                Marquee.Clear(this);            // 有逐字：由 UpdateClip 跟随进度滚动
        }
        else
        {
            _overflow = 0;
            ScrollFraction = 0;
            Width = double.NaN;
            _pending.HorizontalAlignment = _align;
            _accent.HorizontalAlignment = _align;
            Marquee.Clear(this);
            RenderTransform = null;
        }
    }

    /// <summary>预计算每个字的起止 x（相对文本起点），逐帧查表免测量。</summary>
    private void RebuildWordWidths(double fontSize)
    {
        _wordStartX = new List<double>();
        _wordEndX = new List<double>();
        if (_words == null) return;
        var passed = "";
        foreach (var w in _words)
        {
            _wordStartX.Add(MeasureWidth(passed, fontSize));
            passed += w.Text;
            _wordEndX.Add(MeasureWidth(passed, fontSize));
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
        // Clip 只需裁右边界，高度给足避免布局时序问题
        _accent.Clip = new RectangleGeometry(new Rect(0, -1000, boundary, 12000));

        // 横向滚动跟随逐字进度：正在唱的位置保持在视口约 35% 处（对标 Lyricify），
        // 每 50ms 重定目标的短动画形成平滑跟随
        if (_overflow > 10)
        {
            var viewportW = (Parent as FrameworkElement)?.ActualWidth ?? ActualWidth;
            var avail = viewportW - 4;
            var target = Math.Clamp(boundary - avail * 0.35, 0, _overflow);
            ScrollFraction = target / _overflow;
            if (RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-target, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }
}
