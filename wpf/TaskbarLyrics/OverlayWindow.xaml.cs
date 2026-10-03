// 歌词覆盖窗口（移植自 overlay.py 的窗口行为；渲染改为 WPF 合成器）。
//
// 两种模式：
// - taskbar：挂进任务栏（Shell_TrayWnd/副屏 Shell_SecondaryTrayWnd）成为子窗口
// - floating：独立置顶悬浮窗，可拖到屏幕任意位置
// 左键拖动、右键弹出菜单、锁定时整窗鼠标穿透。
// 悬停时播放控制按钮淡入浮现（离开时淡出），切行有滑动淡入动画，
// 逐字扫过由 Storyboard 驱动 KaraokeText.PositionMs（渲染线程合成，不逐帧重绘）。
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
    private static readonly TimeSpan AnimLine = TimeSpan.FromMilliseconds(360); // 切行动画（位移与淡变同长，见 EaseMove）
    private static readonly TimeSpan AnimFade = TimeSpan.FromMilliseconds(150); // 按钮/遮罩浮现淡出
    private const double ButtonsWidth = 96;                                     // 悬停按钮区占位宽度
    /// <summary>逐字进度允许的漂移（ms），超出才 Seek 校准（见 SyncProgress）。
    /// 取 150ms：小于一个字的典型时长，听感上察觉不到，又足以吸收定时器抖动。</summary>
    internal const double SeekToleranceMs = 150;

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
    // 前台切换事件成串到达（旧窗失焦、新窗获焦、任务栏自身闪一下前台，各算一次），
    // 逐个 BeginInvoke(Dock) 就是逐个跑完整重摆——UIA 时代这里能连累 UI 线程几百毫秒。
    // 合并成尾沿触发：一串事件只排一次重摆，并等前台切换的布局抖动落定后再摆
    private readonly System.Windows.Threading.DispatcherTimer _dockCoalesce =
        new() { Interval = TimeSpan.FromMilliseconds(120) };
    // 上次看到的「任务栏按钮窗口集合」指纹：前台切换只在它变了时才催后台重测空档（见 _dockCoalesce.Tick）
    private ulong _taskbarFingerprint;

    private bool _dragging;
    private NativeMethods.POINT _dragCursor0;
    private int _dragWinX0, _dragWinY0;
    // 浮动模式上次摆放的位置尺寸：没变就不调 SetWindowPos（防分层窗重合成闪烁）
    private int _lastFloatX = int.MinValue, _lastFloatY, _lastFloatW, _lastFloatH;
    // 任务栏模式同上：没变就只断言 z-order，不带 FRAMECHANGED 全量重摆
    private IntPtr _lastTbTray;
    private int _lastTbX, _lastTbW, _lastTbH;

    private AppConfig Cfg => _app.Cfg;

    private bool _destroyed;

    /// <summary>窗口是否还活着。宿主任务栏被销毁时我们的子窗口会被连带销毁，
    /// 此时 Closed 已触发；再查一次 IsWindow 兜底那些不经 WPF 通知的销毁路径。</summary>
    public bool IsAlive => !_destroyed
        && (_hwnd == IntPtr.Zero || NativeMethods.IsWindow(_hwnd)); // 句柄未就绪算活（Show 前）

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
            // 本窗口改走软件渲染。分层（透明）窗口走 GPU 时，每帧都得把画面从显存读回
            // 系统内存再交给 UpdateLayeredWindow——窗口就这么一小条，回读和驱动往返比光栅化
            // 本身还贵。实测逐字扫光的常态下渲染线程 CPU 从 17~20% 降到 8~11%，
            // 改窗口宽度那一下的卡顿也短了一截，帧率不变。只影响这个窗口，设置窗口照旧走 GPU
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target)
                target.RenderMode = RenderMode.SoftwareOnly;
            SetLocked(Cfg.Locked);
            ApplyTextRendering();
            ApplyThemeChrome();
            Dock();
            // 前台切换即时重摆：任务栏 XAML 层在前台变化瞬间会盖住嵌入窗口，
            // 不等 1.5s 周期兜底，收到事件立刻重断言位置与 z-order
            _winEventProc = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(QueueDock);
            _winEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        };
        Closed += (_, _) =>
        {
            _destroyed = true;
            if (_winEventHook != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(_winEventHook);
            _dockCoalesce.Stop();
            _hoverPoll.Stop();
        };
        MouseLeftButtonDown += OnLeftDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftUp;
        MouseRightButtonUp += (_, _) => { if (!Cfg.Locked) _app.ShowContextMenu(); };
        // 内容区宽度变化（窗口宽随歌词长度/任务栏空档/悬停按钮列变化）即重算滚动距离
        LinesHost.SizeChanged += (_, _) => ApplyViewportWidth();
        _dockCoalesce.Tick += (_, _) =>
        {
            _dockCoalesce.Stop();
            // 前台窗口变了不等于任务栏按钮变了：来回切窗口、最小化还原都不增减按钮。
            // 原先每个前台事件都 Nudge，60s 退避几乎从不生效，UIA 泄漏照漏
            // （按每天活跃 8 小时估约 40-130MB/天）。这里先用 EnumWindows 算个便宜的
            // 指纹，只有「会上任务栏的顶层窗口」集合真的变了才催后台重测。
            // 窗口隐藏/非避让时 Dock 已经把枚举停了，这时 Nudge 也只是空转，不必管
            var fp = NativeMethods.TaskbarWindowsFingerprint();
            if (fp != _taskbarFingerprint)
            {
                _taskbarFingerprint = fp;
                TaskbarFreeSpace.Nudge();
            }
            Dock();
        };
        _hoverPoll.Tick += (_, _) => PollHover();
        _hoverPoll.Start();
    }

    /// <summary>排一次合并后的重摆（已排队则忽略，一串前台事件只摆一次）。</summary>
    private void QueueDock()
    {
        // 这里只负责合并排队；是否催后台重测空档由 Tick 里的窗口集合指纹决定
        if (_dockCoalesce.IsEnabled) return;
        _dockCoalesce.Start();
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
            // 任务栏模式再确认光标下最顶层的窗口确实是我们或宿主任务栏：
            // Alt+Tab 覆盖层、输入法候选窗之类盖在这块区域上时，鼠标其实在人家窗口里，
            // 只判矩形就会照样切到悬停浮层、白跑一遍展开动画。
            // 分层窗口全透明的像素会被 WindowFromPoint 穿透落到宿主任务栏上，
            // 所以命中任务栏同样算「没被盖住」。
            // 浮动模式不做这个检查：它是 Topmost，下面躺着的可能是任意窗口，
            // 透明像素穿透过去会被误判成「被遮挡」。
            if (inside && Cfg.Mode == "taskbar")
            {
                var hit = NativeMethods.WindowFromPoint(pt);
                if (hit != _hwnd && hit != NativeMethods.GetParent(_hwnd)) inside = false;
            }
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

        // 切行先清掉上一行的第二行引用：新行无译文时，
        // 残留引用会让跟随滚动逻辑在已移除的旧元素上空跑
        _transLine = null;

        var oldLine = _currentLine;
        var oldSb = _karaokeStoryboard;
        _karaokeStoryboard = null;

        var (visual, karaoke, trans) = BuildLineVisual(original, translation, words);
        _currentLine = visual;
        _karaoke = karaoke;
        _transLine = trans;
        // 新行建好就先按当前视口宽算一次溢出：等布局后的 SizeChanged 才算的话，
        // 首帧会以「视口 0」渲染（当作不溢出、居中），随后跳成滚动布局
        ApplyViewportWidth();

        // 本次切行的动画（没走动画分支时为 null）：窗口收窄要等它跑完，见函数末尾 Dock 处
        Storyboard? lineSb = null;
        // 得等布局落定才能量的那部分动画（传送带切行的共享句形变），在 Dock 之后、启动之前补上
        Action? addAfterLayout = null;

        if (animate && oldLine != null)
        {
            // 上一次切行的动画还没跑完就又切了行（密集说唱段落）：
            // 把除这次要参与动画的旧行之外的残留行直接摘掉，否则 LinesHost 里会
            // 同时叠着三四层半透明的行，糊成一团。各自动画的 Completed 照旧执行
            // （Remove 已移除的元素是空操作），逐字动画的释放不受影响
            for (var i = LinesHost.Children.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(LinesHost.Children[i], oldLine))
                    LinesHost.Children.RemoveAt(i);

            // 旧行正在淡出、没人再看它的高亮，却仍在每帧重算 Clip——而文本上挂着
            // DropShadowEffect，内容一变整块就得重新模糊一遍。暂停（不是移除）让高亮
            // 定格在当前位置：Remove 会把 PositionMs 还原成基值 0，整行高亮瞬间清空
            oldSb?.Pause(this);
            // Pause 只停了逐字 Storyboard；横向平滑滚动是 ScrollingTextHost 自己的
            // CompositionTarget.Rendering 订阅，与它无关，会继续每帧唤醒 UI 线程做
            // 低通滤波、改 transform，一直烧到切行动画结束
            FreezeScrolling(oldLine);

            var conveyor = nextLineMode
                           && oldLine is Panel osp && osp.Children.Count > 1
                           && ((FrameworkElement)osp.Children[0]).ActualHeight > 4;
            if (conveyor)
            {
                // 传送带切行（第二行是下一句）：旧块整体上移一个行距，
                // 新块同速从下方进入——旧下行与新上行是同一句，看起来就是它补位上去。
                // 行距必须取第二行的实际偏移（含两行间距）：只算第一行高度会差 3px，
                // 旧下行和新上行永远错开，动画全程重影、结尾还跳一下（「残留」的根因）
                var osp2 = (Panel)oldLine;
                var pitch = ((FrameworkElement)osp2.Children[1])
                    .TransformToAncestor(osp2).Transform(new Point(0, 0)).Y;
                if (pitch < 4) // 兜底：测量失败退回第一行高度
                    pitch = ((FrameworkElement)osp2.Children[0]).ActualHeight;
                // 密集切行时旧块还停在半路：同普通分支，从它当前的位置接着走，
                // 新块的起点跟着让同样的量——两块始终差一个行距，旧下行与新上行全程重合
                var oldY = oldLine.RenderTransform is TranslateTransform ot ? ot.Y : 0;
                oldLine.RenderTransform = new TranslateTransform(0, oldY);
                visual.RenderTransform = new TranslateTransform(0, oldY + pitch);
                LinesHost.Children.Add(visual);

                var sb = new Storyboard();
                AddAnim(sb, oldLine, TranslateYPath, oldY, -pitch);
                AddAnim(sb, visual, TranslateYPath, oldY + pitch, 0);
                // 旧上行从顶边走出去：同普通分支贴边淡出，不让裁剪边缘一刀切掉
                var oldTop = (FrameworkElement)osp2.Children[0];
                AddAnim(sb, oldTop, OpacityPath, oldTop.Opacity, 0, FadeLeaving);
                var oldBottom = (FrameworkElement)osp2.Children[1];
                if (visual is Panel nsp && nsp.Children.Count > 1
                    && oldBottom is ScrollingTextHost from && nsp.Children[0] is ScrollingTextHost to)
                {
                    // 共享句（旧下行与新上行是同一句）：小号灰字边往上走边长成大号白字，
                    // 而不是大字在首行原地浮现。要量两行文字的实际排版，放到布局落定之后（见 AddMorph）
                    to.Opacity = 0;
                    addAfterLayout = () => AddMorph(sb, from, to);
                    // 新的“下一句”从底边进来，同样贴边淡入
                    var bottom = (FrameworkElement)nsp.Children[1];
                    bottom.Opacity = 0;
                    AddAnim(sb, bottom, OpacityPath, 0, 1, FadeEntering);
                }
                else
                {
                    AddAnim(sb, oldBottom, OpacityPath, oldBottom.Opacity, 0, FadeLeaving);
                }
                var oldRef = oldLine;
                sb.Completed += (_, _) =>
                {
                    LinesHost.Children.Remove(oldRef);
                    ReleaseKaraoke(oldSb);
                };
                lineSb = sb;
            }
            else
            {
                // 整行传送带切行：旧行整块往上走出去，新行贴在它正下方同速跟上来。
                // 位移取两块高度的平均再加一道块间距——两行都垂直居中，这正好是
                // 「新行顶边始终离旧行底边 BlockGap」的距离，全程互不重叠（见 EaseMove）。
                // 新行得先挂进树再量：字号、排版模式、布局取整都是从窗口继承的
                LinesHost.Children.Add(visual);
                visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var oldH = oldLine.ActualHeight > 4 ? oldLine.ActualHeight : 24;
                var newH = visual.DesiredSize.Height > 4 ? visual.DesiredSize.Height : oldH;
                var dist = (oldH + newH) / 2 + BlockGap;
                // 上一次切行没跑完就又切（密集段落）时，旧行还停在半路：从它当前的位置接着走，
                // 按 0 重来的话它会先瞬移回原位再出发。新行的起点跟着往下让同样的量，
                // 两块走的路程才相等、间距才全程不变
                var oldY = oldLine.RenderTransform is TranslateTransform ot ? ot.Y : 0;
                oldLine.RenderTransform = new TranslateTransform(0, oldY);
                visual.RenderTransform = new TranslateTransform(0, oldY + dist);
                visual.Opacity = 0;

                // 淡变和位移同一时长、同一个进度，只是把变化挪到贴边的那半程（见 EdgeFade）
                var sb = new Storyboard();
                AddAnim(sb, oldLine, OpacityPath, oldLine.Opacity, 0, FadeLeaving);
                AddAnim(sb, oldLine, TranslateYPath, oldY, -dist);
                AddAnim(sb, visual, OpacityPath, 0, 1, FadeEntering);
                AddAnim(sb, visual, TranslateYPath, oldY + dist, 0);
                var oldRef = oldLine;
                sb.Completed += (_, _) =>
                {
                    LinesHost.Children.Remove(oldRef);
                    ReleaseKaraoke(oldSb); // 旧行的逐字动画随淡出结束释放
                };
                lineSb = sb;
            }
        }
        else
        {
            if (oldLine != null) LinesHost.Children.Remove(oldLine);
            ReleaseKaraoke(oldSb);
            LinesHost.Children.Clear();
            LinesHost.Children.Add(visual);
        }

        StartKaraoke(karaoke, words, lineElapsedMs, playing);

        // 紧凑布局：宽度随当前行文本自适应（Cfg.Width 作为最大宽度，超出由 FitFont 缩字号）
        var origW = karaoke.MeasureLineWidth();
        var transW = translation.Length > 0 && Cfg.SecondLine != "off"
            ? KaraokeText.MeasureTextWidth(translation, new FontFamily(Cfg.FontFamily), TransFontDip, DpiScaleX())
            : 0;
        var targetWidth = Math.Clamp(QuantizeWidth(Math.Max(origW, transW) + (IsLeftAlign ? 16 : 28)),
            80, Cfg.Width);
        // 动画期间不收窄窗口。
        //
        // 分层窗口每次 SetWindowPos 都要重建整张 layered surface，还要让 explorer
        // 重合成任务栏那一条，实测一次 30~100ms，UI 线程全程被按住。
        // 变宽必须立刻（否则更长的新行会被裁掉尾巴），收窄则完全可以等：
        // 切行动画那一小段里右侧多留几像素透明空白，没人看得出来。
        // QuantizeWidth 的 8dip 台阶已经吃掉了相邻两行长度相近的情形，
        // 这里再削掉「新行明显更短」那一半。
        if (lineSb != null && targetWidth < _lyricsWidthDip)
        {
            var narrowTo = targetWidth;
            var forLine = _currentLine; // 密集切行时上一条动画的收尾不该覆盖更新的行
            lineSb.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_currentLine, forLine)) return;
                _lyricsWidthDip = narrowTo;
                Dock(assertZOrder: false);
            };
        }
        else
        {
            _lyricsWidthDip = targetWidth;
        }
        // 切行这一路不重新断言 z-order：那是一次跨进程的 SetWindowPos（实测 1~8ms），
        // 正好压在动画第 0 帧上；任务栏子窗口的层级由 1.5s 周期 Dock 和前台切换钩子兜着
        Dock(assertZOrder: false);
        // 动画放到 Dock 之后才启动：变宽的那次改尺寸会同步卡住几十毫秒，
        // 先启动的话时钟在卡顿里照走，解冻后第一帧直接跳到半路；
        // 放在后面，卡顿落在画面还静止的时候，动画从第 0 帧完整走起。
        // 共享句形变要量新旧两行文字的实际位置，同样得等 Dock 把窗口宽改完
        addAfterLayout?.Invoke();
        lineSb?.Begin(this);
    }

    /// <summary>传送带切行的共享句形变：from 是旧块的「下一句」（小号灰字），
    /// to 是新块的「当前句」（大号白字），两者是同一句歌词。
    ///
    /// to 起步时被缩放平移到恰好盖住 from 的文字包围盒，随动画长回原样；
    /// from 反过来从原样长到 to 的终点形状。两边的缩放、平移都按同一进度线性插值，
    /// 两个包围盒全程严丝合缝地重合，看起来就是一行字边往上走边变大。
    /// 两块整体又一起平移（始终差一个行距），所以在起点量出的相对关系整段动画都成立。
    ///
    /// 位置取实际排版结果，不按对齐方式推算：大字溢出切了左对齐+滚动、小字没溢出还居中，
    /// 或者大字已经滚到一半（从中途接上播放），推算都会错位，量出来的包围盒天然覆盖这些情形。</summary>
    private void AddMorph(Storyboard sb, ScrollingTextHost from, ScrollingTextHost to)
    {
        // 新块刚挂进树、窗口可能刚改过宽：先把布局落定再量。
        // 这一遍本来就要在下一帧做，挪到这里只是提前，不是额外开销
        UpdateLayout();
        var s = from.TextBounds(LinesHost);
        var b = to.TextBounds(LinesHost);
        if (s.Width < 1 || s.Height < 1 || b.Width < 1 || b.Height < 1)
        {
            // 量不到（窗口隐藏、布局没出来）：退回各自淡入淡出，照样能切
            AddAnim(sb, from, OpacityPath, from.Opacity, 0, FadeLeaving);
            AddAnim(sb, to, OpacityPath, 0, 1, FadeEntering);
            return;
        }
        var kx = s.Width / b.Width;
        var ky = s.Height / b.Height;
        var dx = s.Left - b.Left;
        var dy = s.Top - b.Top;

        var toBlock = to.ApplyMorph(kx, ky, dx, dy);
        AddAnim(sb, toBlock, MorphScaleXPath, kx, 1);
        AddAnim(sb, toBlock, MorphScaleYPath, ky, 1);
        AddAnim(sb, toBlock, MorphXPath, dx, 0);
        AddAnim(sb, toBlock, MorphYPath, dy, 0);
        var fromBlock = from.ApplyMorph(1, 1, 0, 0);
        AddAnim(sb, fromBlock, MorphScaleXPath, 1, 1 / kx);
        AddAnim(sb, fromBlock, MorphScaleYPath, 1, 1 / ky);
        AddAnim(sb, fromBlock, MorphXPath, 0, -dx);
        AddAnim(sb, fromBlock, MorphYPath, 0, -dy);

        // 换色（灰→白）的交叉淡变挤在起步那一小段做完。两层是不同字号排出来再缩放的，
        // 笔画不可能完全重合，两层都半透明叠着的时间越长，越像一行字带着重影在走。
        // 新字在上层（后挂进树）先淡入把旧字盖住，这期间旧字保持不透明，
        // 总覆盖率不掉、不会闪暗；盖严了再把旧字撤掉。这时才走了约四分之一的路、
        // 尺寸几乎还是小字，之后整段上移和放大都只有新字一层
        AddAnim(sb, to, OpacityPath, 0, 1, MorphIn);
        AddAnim(sb, from, OpacityPath, from.Opacity, 0, MorphOut);
        // 旧行随动画结束整块移除，不用管；新行停在恒等变换，撤掉让文字回到不带变换的渲染
        sb.Completed += (_, _) => to.ClearMorph();
    }

    /// <summary>把内容宽度向上取到 8dip 的台阶。
    ///
    /// 窗口宽度每变一次就要 SetWindowPos 一次，而它正好发生在切行动画启动的同一刻。
    /// 逐行按精确文本宽度自适应的话几乎每行都变；量化之后，相邻两行长度相近时宽度
    /// 完全不变，ApplyPosition 的幂等短路直接把这次调用吃掉。向上取整不会裁字，
    /// 台阶只有 8dip，紧凑布局的观感照旧。</summary>
    private static double QuantizeWidth(double dip) => Math.Ceiling(dip / 8.0) * 8.0;

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
        TitleText.Foreground = new SolidColorBrush(Theme.TextColor(Cfg));
        TitleText.HorizontalAlignment = align;
        ArtistText.Text = artist;
        ArtistText.FontFamily = family;
        ArtistText.FontSize = TransFontDip;
        ArtistText.LineHeight = Math.Ceiling(TransFontDip * 1.3);
        ArtistText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        ArtistText.Margin = new Thickness(0, 3, 0, 0); // 与歌词行相同的上下间距
        ArtistText.Foreground = new SolidColorBrush(Theme.TransColor(Cfg));
        ArtistText.HorizontalAlignment = align;
        ArtistText.Visibility = artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var dpi = DpiScaleX();
        var titleWeight = Cfg.FontBold ? FontWeights.SemiBold : FontWeights.Normal;
        var tw = KaraokeText.MeasureTextWidth(title, family, OrigFontDip, dpi, titleWeight);
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
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-6, 0, AnimFade)
            {
                EasingFunction = EaseOut,
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
        var anim = new DoubleAnimation(ButtonsHost.ActualWidth, to, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = EaseOut,
        };
        if (snapDockOnComplete)
            anim.Completed += (_, _) => Dock();
        ButtonsHost.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>淡变/展开统一用三次缓出：线性淡变在起止处显得生硬。</summary>
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>切行动画的缓动：正弦缓入缓出。
    ///
    /// 之前是位移只走 0.6 个行高、正弦缓出，旧行 120ms 就淡没。幅度不满一行，
    /// 新旧两行中段必然叠在一起，只能让旧行抢先退场——可 120ms 的三次缓出第一帧就掉
    /// 36% 不透明度、第三帧只剩两成，这时它才挪了不到 4px：旧行不是「滑上去」而是「闪没」，
    /// 两行加起来的亮度在 50~80ms 还会塌到 0.6 上下，整条一暗一亮。
    ///
    /// 现在是首尾相接的整块传送带（见 SetLine）：两行永不重叠，旧行可以陪新行走完全程，
    /// 淡变跟着位移的进度走（见 EdgeFade）。「旧行必须抢在前面退场」这条约束没了，
    /// 缓入缓出当初被否掉的理由也就不在了，而它两头速度为零正好对症：
    /// 头一两帧要排版、光栅化新行，最后一帧要摘掉旧行，是整段里最容易丢帧的时刻，
    /// 这时速度接近零，丢一帧也看不出位移跳变。峰值速度是平均的 π/2 倍，
    /// 倍数和原来缓出的初速度一样，只是从开头挪到了中段。</summary>
    private static readonly IEasingFunction EaseMove = new SineEase { EasingMode = EasingMode.EaseInOut };

    /// <summary>整行传送带切行时新旧两块之间的间距（DIP）。
    /// 比原文与译文之间的 3px 行距略宽：中途两块同时在画面里，靠这点间隔认出哪两行是一句。</summary>
    private const double BlockGap = 6;

    /// <summary>整行传送带切行的淡变曲线：进度 q 照 EaseMove 走，离场的行按 1-q² 变暗，
    /// 入场的行按 1-(1-q)² 变亮——走到半程各还有 75%，淡变集中在贴着窗口边缘的那半程。
    ///
    /// 不直接拿 q 当透明度（半程各 50%）：带译文的一块差不多和任务栏一样高，
    /// 走到半程时新旧两块各有一半已经在窗口外被裁掉，再各打五折，
    /// 画面上只剩平时一半多一点的字，每切一行整条都暗一下。
    /// 两块不重叠，没有「叠起来过亮」的问题，亮度留高一点只会让滚动看得更清楚。
    /// 两头的变化率仍是零（q 的速度在两头为零），起止不会突兀。</summary>
    private sealed class EdgeFade(bool entering) : IEasingFunction
    {
        public double Ease(double t)
        {
            var q = EaseMove.Ease(t);
            return entering ? 1 - (1 - q) * (1 - q) : q * q;
        }
    }

    private static readonly IEasingFunction FadeEntering = new EdgeFade(true);
    private static readonly IEasingFunction FadeLeaving = new EdgeFade(false);

    /// <summary>只在动画时长的 [from, to] 这一段里走完的进度（smoothstep，两头变化率为零），
    /// 段外保持不动。按时间而不是按位移进度切：共享句换色要赶在起步、移动还慢的时候做完。</summary>
    private sealed class TimeWindow(double from, double to) : IEasingFunction
    {
        public double Ease(double t)
        {
            var x = Math.Clamp((t - from) / (to - from), 0, 1);
            return x * x * (3 - 2 * x);
        }
    }

    // 共享句的新字在前 35% 的时间里淡入；旧字等新字过半（20% 处）才开始撤，45% 时撤完
    // ——撤的过程中两层合起来的覆盖率始终在 97% 以上
    private static readonly IEasingFunction MorphIn = new TimeWindow(0, 0.35);
    private static readonly IEasingFunction MorphOut = new TimeWindow(0.2, 0.45);

    private static readonly PropertyPath OpacityPath = new("Opacity");
    private static readonly PropertyPath TranslateYPath = new("(UIElement.RenderTransform).(TranslateTransform.Y)");
    // 共享句形变挂在文本本体上：变换组里 [0] 是缩放、[1] 是平移（见 ScrollingTextHost.ApplyMorph）
    private static readonly PropertyPath MorphScaleXPath =
        new("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)");
    private static readonly PropertyPath MorphScaleYPath =
        new("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)");
    private static readonly PropertyPath MorphXPath =
        new("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)");
    private static readonly PropertyPath MorphYPath =
        new("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)");

    /// <summary>透明度淡变。必须显式给 From（取当前有效值）：
    /// DoubleAnimation 只给 To 时起点取属性「基值」而非当前动画值——
    /// 被上一轮动画改到 1 的 Opacity 其基值仍是 XAML 里的 0，
    /// 再动画到 0 就成了 0→0，元素瞬间消失而不是淡出。</summary>
    private static void FadeTo(UIElement el, double target)
        => el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(el.Opacity, target, AnimFade) { EasingFunction = EaseOut });

    /// <summary>加一条切行动画（时长 AnimLine，缓动省略时用 EaseMove，理由见那里）。
    /// From 同样必须显式给，理由见 FadeTo。</summary>
    private static void AddAnim(Storyboard sb, FrameworkElement target, PropertyPath path,
        double from, double to, IEasingFunction? easing = null)
    {
        var anim = new DoubleAnimation(from, to, AnimLine) { EasingFunction = easing ?? EaseMove };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        sb.Children.Add(anim);
    }

    /// <summary>把即将淡出的旧行的横向平滑滚动就地定格。
    ///
    /// 这不是省几次乘法的微优化：ScrollingTextHost 的平滑跟随靠订阅
    /// CompositionTarget.Rendering，一订阅就把 WPF 的渲染节拍从「渲染线程自己插值」
    /// 拉成「每帧唤醒 UI 线程」。切行时旧行的跟随往往还没到位（长行滚动中被切走），
    /// 于是整段切行动画里 UI 线程被每帧叫醒一次，而这个窗口是分层（透明）窗口、
    /// 全程 CPU 软件光栅化——多出来的那点工作正好把帧时间顶过 16.7ms。
    /// 旧行已在淡出，滚到哪都没人看，直接停订阅。</summary>
    private static void FreezeScrolling(DependencyObject root)
    {
        if (root is ScrollingTextHost host) host.FreezeScroll();
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            FreezeScrolling(VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>释放一行的逐字动画。必须 Remove 而不是 Stop：
    /// Begin(scope, isControllable: true) 创建的 clock 由 scope 对象（本窗口）持有，
    /// 只有 Remove 才会解除持有——Stop 只是让它停下，clock 连同它引用的整棵行视觉树
    /// （两层文本、阴影、画刷）永远不回收。实测每行泄漏约 8KB，一行歌词 3~5 秒，
    /// 也就是每小时白吃 8MB 且只增不减，长时间运行后内存持续膨胀。
    /// 逐字进度要 Seek/Pause/Resume，所以 isControllable 不能去掉，只能配对 Remove。</summary>
    private void ReleaseKaraoke(Storyboard? sb) => sb?.Remove(this);

    private (NaturalMeasureStack Visual, KaraokeText Karaoke, TranslationText? Trans) BuildLineVisual(
        string original, string translation, IReadOnlyList<KaraokeWord>? words)
    {
        var textColor = Theme.TextColor(Cfg);
        var bright = Freeze(new SolidColorBrush(textColor));
        var pending = Freeze(new SolidColorBrush(Color.FromArgb(140, textColor.R, textColor.G, textColor.B)));
        var blackShadow = Theme.BlackShadow(textColor);

        var align = IsLeftAlign ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        var weight = Cfg.FontBold ? FontWeights.SemiBold : FontWeights.Normal; // 原文半粗更清晰，译文保持常规
        var karaoke = new KaraokeText();
        karaoke.SetLine(original, words, new FontFamily(Cfg.FontFamily), OrigFontDip, bright, pending,
            Cfg.Shadow, blackShadow, align, weight);

        var sp = new NaturalMeasureStack { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(karaoke);
        TranslationText? trans = null;
        if (translation.Length > 0 && Cfg.SecondLine != "off")
        {
            var transColor = Theme.TransColor(Cfg);
            var transBrush = Freeze(new SolidColorBrush(transColor));
            trans = new TranslationText { Margin = new Thickness(0, 3, 0, 0) }; // 与原文之间的行距
            trans.SetText(translation, new FontFamily(Cfg.FontFamily), TransFontDip, transBrush,
                Cfg.Shadow, Theme.BlackShadow(transColor), align, followProgress: words != null);
            sp.Children.Add(trans);
        }
        return (sp, karaoke, trans);
    }

    // 当前第二行（供 SyncProgress 跟随原文进度同步滚动、视口变化时重算溢出）
    private TranslationText? _transLine;

    /// <summary>把内容区可用宽度告知当前行：滚动逻辑要的是视口宽，
    /// 不能由行自身的 ActualWidth 反推（溢出时自身被文本撑宽，会自激振荡）。
    /// LinesHost 宽度只由窗口宽决定（star 列），不受行内容影响，是稳定的视口信号。</summary>
    private void ApplyViewportWidth()
    {
        var w = LinesHost.ActualWidth;
        if (w <= 0) return;
        _karaoke?.SetViewportWidth(w);
        _transLine?.SetViewportWidth(w);
    }

    /// <summary>把装饰件（悬停遮罩、封面占位）刷成当前任务栏明暗对应的配色。
    ///
    /// 单独一个方法而不是塞进 Dock()：Dock 每 1.5 秒跑一次，
    /// 每轮重设一遍画刷属性会白白让渲染层重新失效，而这几个颜色只在主题切换时变。</summary>
    public void ApplyThemeChrome()
    {
        HoverMask.Background = Theme.HoverMask;
        PlaceholderBg.Fill = Theme.PlaceholderBg;
        PlaceholderIcon.Foreground = Theme.PlaceholderIcon;
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

    /// <summary>~50ms 周期调用：用 SMTC 本地插值进度校准逐字动画（暂停时冻结）。
    ///
    /// 只在偏差超过 SeekToleranceMs 时才 Seek。无条件每拍 Seek 会让逐字扫过和
    /// 横向滚动以定时器频率（20Hz）来回被拽：Seek 在动画 tick 边界生效，
    /// 而目标时间取自调用瞬间的真实时钟，两者永远差不到一帧但方向随机，
    /// 于是位置每 50ms 抖一次——这就是长歌词滚动「一卡一卡」的主因。
    /// 时钟本身走的也是真实时间，放手让它自己跑就是平滑的；
    /// 只有真正漂移（用户拖进度条、换歌、暂停恢复、SMTC 基准刷新）才需要拉回。</summary>
    public void SyncProgress(double lineElapsedMs, bool playing)
    {
        if (_karaokeStoryboard == null) return;
        var t = TimeSpan.FromMilliseconds(Math.Clamp(lineElapsedMs, 0, _karaokeTotalMs));
        var cur = _karaokeStoryboard.GetCurrentTime(this);
        if (cur == null || Math.Abs((t - cur.Value).TotalMilliseconds) > SeekToleranceMs)
            _karaokeStoryboard.Seek(this, t, TimeSeekOrigin.BeginTime);
        if (playing != _sbPlaying)
        {
            _sbPlaying = playing;
            if (playing) _karaokeStoryboard.Resume(this);
            else _karaokeStoryboard.Pause(this);
        }
        // 第二行跟随原文逐字进度同步横向滚动
        if (_karaoke != null) _transLine?.ScrollToFraction(_karaoke.ScrollFraction);
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

    /// <summary>窗口此刻应不应该显示。暂停超时时即使还没到第一句歌词，也显示歌曲信息（歌名/歌手）。</summary>
    private bool WantVisible => (_hasContent || _infoMode) && !_fsHidden;

    private void UpdateVisibility()
    {
        var show = WantVisible;
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

    // 自动避让时窗口与两侧图标之间留的间距（client 像素，两侧各留一份）。
    // 原先是 4px：贴得太近，视觉上跟旁边的图标黏成一片，长歌词滚动裁切的边缘
    // 正好压在图标边上，看起来像被图标遮住了
    private const int GapPad = 8;
    // 内容区窄于这个宽度（DIP）时长歌词只剩几个字在那滚，读不成句子。
    // 到这一步宁可把封面收掉，把地方全让给文字
    private const double MinContentDip = 160;

    /// <summary>按当前模式与配置摆放窗口（周期调用以跟随任务栏变化/重建）。
    /// assertZOrder=false 时位置尺寸没变就什么都不做，不再顺手断言层级（切行路径用，见 SetLine）。</summary>
    public void Dock(bool assertZOrder = true)
    {
        if (_hwnd == IntPtr.Zero) return;
        var heightDip = CurrentHeightDip();
        _lastHeightDip = heightDip;
        var showCover = Cfg.ShowCover;
        var coverZone = showCover ? heightDip : 0;
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
                // 最小可接受宽度按「收掉封面后的文字底线」算：宁可先牺牲封面，
                // 也不要为了留住封面把整条挤到读不了的宽度
                var minDip = MinContentDip + buttonsZone + GapPad * 2 / dpi;
                (int L, int R)? gap = null;
                // 还一次空档都没拿到时（启动期窗口通常是藏着的）照常测：否则第一句歌词出来时
                // 手上没有快照，窗口会先按默认位置摆出来再跳进空档
                if (WantVisible || !_autoGap.HasValue)
                    gap = TaskbarFreeSpace.FindBestGap(
                        trayHwnd, _hwnd, (int)Math.Round(wantDip * dpi), (int)Math.Round(minDip * dpi),
                        CurrentTrayX(trayHwnd), Cfg.AutoSide);
                else
                    // 窗口藏着（没在放歌、全屏隐藏）时没人看空档，让后台停止枚举，别白漏 UIA 内存。
                    // 不走 FindBestGap（它会把目标设回来）；重新显示后下一次 Dock 设回目标，
                    // SetTargets 发现目标变了会自己 Nudge 立刻重测。_autoGap 保留，显示时先按旧空档摆
                    TaskbarFreeSpace.SetTargets(IntPtr.Zero, IntPtr.Zero);
                if (gap.HasValue) _autoGap = gap.Value; // 失败则沿用旧空档，不回退跳位
                if (_autoGap.HasValue)
                {
                    var g = _autoGap.Value;
                    var gapDip = (g.R - g.L - GapPad * 2) / dpi; // 两侧各留一份间距，不与图标贴边
                    // 空档装不下「封面 + 底线宽度的文字」时就把封面收掉：
                    // 封面没了还认得出在放哪首歌，歌词被切成两个字就真没意义了
                    if (showCover && gapDip - coverZone - buttonsZone < MinContentDip)
                    {
                        showCover = false;
                        coverZone = 0;
                        wantDip = Cfg.Width + buttonsZone;
                    }
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
            // 清掉目标句柄，否则后台线程会捧着上次的句柄一直白枚举下去——
            // 关掉自动避让的用户照样在漏 UIA 的原生内存
            TaskbarFreeSpace.SetTargets(IntPtr.Zero, IntPtr.Zero);
        }
        _lastCoverZoneDip = coverZone; // 要等避让降级决定完封面收不收，居中补偿才对得上

        // 视觉布局同步
        Height = heightDip;
        CoverZone.Visibility = showCover ? Visibility.Visible : Visibility.Collapsed;
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
        ApplyPosition(assertZOrder);
    }

    /// <summary>窗口当前在任务栏 client 坐标系里的 x（自动避让就近选档用）。</summary>
    private int CurrentTrayX(IntPtr trayHwnd)
    {
        NativeMethods.GetWindowRect(_hwnd, out var wrc);
        NativeMethods.GetWindowRect(trayHwnd, out var trc);
        return wrc.Left - trc.Left;
    }

    /// <summary>按 _displayWidthDip 摆放窗口位置（任务栏挂靠或浮动）。</summary>
    private void ApplyPosition(bool assertZOrder)
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
                // 自动避让：按「停靠对齐」钉空档的边缘/居中（各留 GapPad）。
                // 默认左对齐钉左缘：悬停加按钮时窗口向右扩，封面不推图标
                var g = _autoGap.Value;
                x = Cfg.AutoAlign switch
                {
                    "right" => g.R - GapPad - widthPx,
                    "center" => (g.L + g.R - widthPx) / 2,
                    _ => g.L + GapPad,
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
            // 位置尺寸没变时只断言 z-order：Dock 每 1.5s 跑一次、前台切换还会额外触发，
            // 每次都带 SWP_FRAMECHANGED 全量重摆等于让 explorer 反复重算子窗口边框、
            // 重合成任务栏那一条——白烧 CPU，任务栏繁忙时还会排在它的消息队列里等。
            // 缓存值要跟窗口的真实矩形对一遍：否则万一有别的东西挪动了我们，
            // 缓存会把错位状态永久锁死（缓存只是省调用，不是位置的唯一真相）。
            // 不带 SWP_NOZORDER：断言为任务栏子窗口最顶层，
            // 任务栏内部重排（如悬停图标弹出预览）后仍保持可见。
            // 切行路径传 assertZOrder=false 连这一下也省掉（见 SetLine 末尾）
            var prevTray = _lastTbTray;
            NativeMethods.GetWindowRect(_hwnd, out var curRc);
            NativeMethods.GetWindowRect(tray, out var trayRc);
            if (tray == _lastTbTray && x == _lastTbX && widthPx == _lastTbW && heightPx == _lastTbH
                && curRc.Left - trayRc.Left == x
                && curRc.Width == widthPx && curRc.Height == heightPx)
            {
                if (assertZOrder)
                    NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
                return;
            }
            (_lastTbTray, _lastTbX, _lastTbW, _lastTbH) = (tray, x, widthPx, heightPx);
            // FRAMECHANGED 只在换了宿主任务栏（explorer 重启、切显示器）之后才需要——
            // 那时窗口样式刚被 MakeChildOf 改过，得让 explorer 重算一次子窗口边框。
            // 单纯的位置/尺寸变化不需要它，而歌词窗口宽度是逐行自适应的：每切一行都带
            // FRAMECHANGED 重摆一次，等于每行都请 explorer 重算边框加重合成任务栏那一条，
            // 且正好发生在切行动画启动的同一刻，任务栏忙时能把 UI 线程按住好几帧
            var frameChanged = tray != prevTray;
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, 0, widthPx, heightPx,
                NativeMethods.SWP_NOACTIVATE
                | (frameChanged ? NativeMethods.SWP_FRAMECHANGED : 0u));
        }
        else // floating
        {
            NativeMethods.MakePopup(_hwnd, topmost: true);
            Topmost = true;
            _lastTbTray = IntPtr.Zero; // 模式切回任务栏时强制重新摆放
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
