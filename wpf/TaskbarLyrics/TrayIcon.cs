// 系统托盘图标 + 共享右键菜单（WPF ContextMenu，自动套用 Fluent/Win11 主题）。
// 托盘菜单与歌词窗口右键菜单用同一份定义：任务栏/浮动（check）、锁定（check）、
// 打开设置…、退出。NotifyIcon 在 UI 线程创建，事件直接在 UI 线程触发，无需跨线程投递。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using ContextMenu = System.Windows.Controls.ContextMenu; // WinForms 也有 ContextMenu，消歧
using MenuItem = System.Windows.Controls.MenuItem;       // WinForms 也有 MenuItem，消歧
using PlacementMode = System.Windows.Controls.Primitives.PlacementMode;

namespace TaskbarLyrics;

public sealed class TrayIcon : IDisposable
{
    private readonly MainController _app;
    private readonly NotifyIcon _icon;

    public TrayIcon(MainController app)
    {
        _app = app;

        System.Drawing.Icon icon;
        try
        {
            // 直接用 exe 内嵌的应用图标（ApplicationIcon）
            icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                   ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        _icon = new NotifyIcon
        {
            Text = "任务栏歌词",
            Icon = icon,
            Visible = true,
        };
        // 不设 ContextMenuStrip（WinForms 样式老旧），右键改弹 WPF Fluent 菜单
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowMenuAtCursor();
        };
    }

    /// <summary>菜单规范（menu.py build_spec）：高频操作，详细设置进设置窗口。</summary>
    private ContextMenu BuildMenu()
    {
        var cfg = _app.Cfg;
        var menu = new ContextMenu();

        MenuItem Item(string header, bool isChecked, Action onClick)
        {
            var mi = new MenuItem { Header = header, IsChecked = isChecked };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        menu.Items.Add(Item("任务栏模式", cfg.Mode == "taskbar", () => _app.SetMode("taskbar")));
        menu.Items.Add(Item("浮动模式", cfg.Mode == "floating", () => _app.SetMode("floating")));
        menu.Items.Add(Item("锁定位置（鼠标穿透）", cfg.Locked, () => _app.SetLocked(!cfg.Locked)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("打开设置…", false, _app.OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出", false, _app.Quit));
        return menu;
    }

    /// <summary>托盘右键 / 歌词窗口右键：在光标处弹出同一份菜单。</summary>
    public void ShowMenuAtCursor()
    {
        var menu = BuildMenu();
        // 独立弹出的 WPF ContextMenu 需要一个能获得焦点的属主窗口：
        // 本程序所有窗口都是不激活的，直接 IsOpen 会立即失焦关闭。
        // 用一个 1x1 隐形可激活窗口做属主（0x0 窗口激活不可靠，菜单会自关），
        // 启动时已调 AllowSetForegroundWindow(ASFW_ANY) 保证 Activate 成功，
        // 菜单关闭时属主一并销毁。
        var owner = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Left = -32000, // 1x1 透明点不可见，停哪都行，放屏幕外保险
            Top = -32000,
        };
        owner.ContextMenu = menu;
        owner.Show();
        owner.Activate(); // 属主必须真正激活，否则菜单在鼠标移上去前就自关
        menu.Placement = PlacementMode.MousePoint; // 跟随光标，WPF 自行处理多屏 DPI
        menu.PlacementTarget = owner;
        menu.IsOpen = true;
        menu.Closed += (_, _) => owner.Close();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
