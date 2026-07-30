// 应用入口（对应 Python main.py 的装配）。
// 用法：TaskbarLyrics.exe                正常运行
//       TaskbarLyrics.exe --settings     只打开设置窗口（不启动歌词覆盖层）
//       TaskbarLyrics.exe --lyrics-test "歌名" "歌手"   控制台验证歌词抓取
using System.Runtime.InteropServices;
using System.Windows;

namespace TaskbarLyrics;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    private MainController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 控制台验证入口：TaskbarLyrics.exe --lyrics-test "Lemon" "米津玄師"
        if (e.Args.Length >= 2 && e.Args[0] == "--lyrics-test")
        {
            AttachConsole(-1); // 挂到父进程控制台才能看到输出
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            // 清掉 DispatcherSynchronizationContext，否则 async 续体回投 UI 线程会死锁
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            var title = e.Args[1];
            var artist = e.Args.Length >= 3 ? e.Args[2] : "";
            Lyrics.RunConsoleTestAsync(title, artist).GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        // 只开设置窗口：TaskbarLyrics.exe --settings
        if (e.Args.Length >= 1 && e.Args[0] == "--settings")
        {
            _controller = new MainController();
            _controller.OpenSettings(); // ShowDialog 自带模态消息循环，关闭后返回
            Shutdown(0);
            return;
        }

        // 提高系统定时器精度，DispatcherTimer 才能更准
        try { timeBeginPeriod(1); } catch { /* 不致命 */ }
        System.Windows.Forms.Application.EnableVisualStyles();

        _controller = new MainController();
        _controller.Run();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        try { timeEndPeriod(1); } catch { /* 不致命 */ }
        base.OnExit(e);
    }
}
