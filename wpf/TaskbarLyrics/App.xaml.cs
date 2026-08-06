// 应用入口（对应 Python main.py 的装配）。
// 用法：TaskbarLyrics.exe                正常运行
//       TaskbarLyrics.exe --settings     只打开设置窗口（不启动歌词覆盖层）
//       TaskbarLyrics.exe --lyrics-test "歌名" "歌手" [translation|romaji|off]   控制台验证歌词抓取
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
        InstallExceptionHandlers();

        // 接力替换模式（更新用）：TaskbarLyrics-new.exe --apply-update "<目标exe>" <旧pid>
        if (e.Args.Length >= 3 && e.Args[0] == "--apply-update")
        {
            Updater.ApplyUpdateMain(e.Args[1], int.Parse(e.Args[2]));
            Shutdown(0);
            return;
        }

        // 控制台验证入口：TaskbarLyrics.exe --lyrics-test "Lemon" "米津玄師"
        if (e.Args.Length >= 2 && e.Args[0] == "--lyrics-test")
        {
            AttachConsole(-1); // 挂到父进程控制台才能看到输出
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            // 清掉 DispatcherSynchronizationContext，否则 async 续体回投 UI 线程会死锁
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            var title = e.Args[1];
            var artist = e.Args.Length >= 3 ? e.Args[2] : "";
            // 第 4 个参数指定第二行内容（translation / romaji / off），省略按译文
            var secondLine = e.Args.Length >= 4 ? e.Args[3] : "translation";
            Lyrics.RunConsoleTestAsync(title, artist, secondLine).GetAwaiter().GetResult();
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

    /// <summary>三处未捕获异常的兜底。
    ///
    /// 这是个常驻后台的覆盖层：偶发异常（任务栏句柄在两次调用之间失效、
    /// SMTC/网络抖动、封面解码到坏字节）不该让整个程序消失。默认行为是
    /// UI 线程一个未捕获异常直接终止进程，且不留任何线索——现象就是
    /// 「跑着跑着程序自己没了」，跟 explorer 重启那条路径混在一起分不清。
    /// 兜住后记日志继续跑：状态最多错一帧，下一个 50ms 节拍就重算回来。
    /// AppDomain 那条只能记录（.NET 上非 UI 线程的未捕获异常无法阻止进程终止），
    /// 但至少 error.log 里会留下调用栈。</summary>
    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("dispatcher", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("appdomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("task", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        try { timeEndPeriod(1); } catch { /* 不致命 */ }
        base.OnExit(e);
    }
}
