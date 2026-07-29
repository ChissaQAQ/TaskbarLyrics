// 系统托盘图标 + 共享右键菜单（移植自 tray.py + menu.py）。
// 托盘菜单与歌词窗口右键菜单用同一份定义：任务栏/浮动（radio）、锁定（check）、
// 打开设置…、退出。NotifyIcon 在 UI 线程创建，事件直接在 UI 线程触发，无需跨线程投递。
using System.Windows.Forms;

namespace TaskbarLyrics;

public sealed class TrayIcon : IDisposable
{
    private readonly MainController _app;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public TrayIcon(MainController app)
    {
        _app = app;
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();

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
            ContextMenuStrip = _menu,
        };
    }

    /// <summary>菜单规范（menu.py build_spec）：高频操作，详细设置进设置窗口。</summary>
    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var cfg = _app.Cfg;

        var taskbar = new ToolStripMenuItem("任务栏模式") { Checked = cfg.Mode == "taskbar" };
        taskbar.Click += (_, _) => _app.SetMode("taskbar");
        var floating = new ToolStripMenuItem("浮动模式") { Checked = cfg.Mode == "floating" };
        floating.Click += (_, _) => _app.SetMode("floating");
        var locked = new ToolStripMenuItem("锁定位置（鼠标穿透）") { Checked = cfg.Locked };
        locked.Click += (_, _) => _app.SetLocked(!cfg.Locked);
        var settings = new ToolStripMenuItem("打开设置…");
        settings.Click += (_, _) => _app.OpenSettings();
        var quit = new ToolStripMenuItem("退出");
        quit.Click += (_, _) => _app.Quit();

        _menu.Items.Add(taskbar);
        _menu.Items.Add(floating);
        _menu.Items.Add(locked);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(settings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(quit);
    }

    /// <summary>歌词窗口右键：在光标处弹出同一份菜单。</summary>
    public void ShowMenuAtCursor()
    {
        RebuildMenu();
        _menu.Show(Cursor.Position);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
