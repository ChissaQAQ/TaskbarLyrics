// 应用入口（对应 Python main.py 的装配）。
// 用法：TaskbarLyrics.exe                正常运行
//       TaskbarLyrics.exe --settings     只打开设置窗口（不启动歌词覆盖层）
//       TaskbarLyrics.exe --lyrics-test "歌名" "歌手" [translation|romaji|off] [时长秒]   控制台验证歌词抓取
//       TaskbarLyrics.exe --update-test                                        控制台验证检查更新与配额
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

    // Local\ 前缀按用户会话隔离：快速切换用户、远程桌面的另一个会话有自己的任务栏，各跑一份才对
    private const string InstanceMutexName = @"Local\TaskbarLyrics.SingleInstance";

    /// <summary>单实例锁。必须放 static 字段里持有到进程退出：只放局部变量的话，
    /// Mutex 对象被 GC 回收时句柄随之关闭，锁就悄悄没了。</summary>
    private static Mutex? _instanceMutex;

    /// <summary>拿单实例锁，拿不到（已有一份在跑）返回 false。
    ///
    /// 要等几秒而不是一试不中就放弃：更新接力时中继是等旧进程退出才拉起新版的，但旧进程
    /// 走完退出流程、真正放掉锁可能还差一点（中继最多等旧进程 30 秒，超时也照样往下走），
    /// 不等的话刚换上的新版会把还没退干净的旧进程当成「已在运行」，自己退掉。
    /// 代价是用户真重复双击时要等这几秒才看到提示。</summary>
    private static bool AcquireSingleInstance()
    {
        Mutex mutex;
        try { mutex = new Mutex(false, InstanceMutexName); }
        // 同名锁在但打不开：是以管理员身份运行的那一份建的（提权进程建的对象普通权限打不开），同样算已在运行
        catch (UnauthorizedAccessException) { return false; }
        // 其他意外（同名对象不是 Mutex 之类）：宁可不防重，也不能因为一把锁把程序拦在门外——
        // 这里抛出去的话 OnStartup 半途而废，OnExplicitShutdown 下进程就无声地挂在后台
        catch (Exception ex) { Log.Error("single-instance", ex); return true; }
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
        // 上一份没放锁就没了（崩溃、被任务管理器结束）：锁已经转到我们手上，照常启动
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            mutex.Dispose();
            return false;
        }
        _instanceMutex = mutex;
        return true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallExceptionHandlers();

        // 接力替换模式（更新用）：TaskbarLyrics-new.exe --apply-update "<目标exe>" <旧pid>
        if (e.Args.Length >= 3 && e.Args[0] == "--apply-update")
        {
            // finally 必不可少：这里抛出的异常会被 DispatcherUnhandledException 兜住（Handled=true），
            // 而 ShutdownMode=OnExplicitShutdown 下没人调 Shutdown 进程就不会退，
            // 中继进程会一直留在后台，任务管理器里平白多一个 TaskbarLyrics-new
            try
            {
                Updater.ApplyUpdateMain(e.Args[1], int.Parse(e.Args[2]));
            }
            finally
            {
                Shutdown(0);
            }
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
            // 第 5 个参数模拟 SMTC 上报的时长（秒），省略为 0 即网易云客户端的情形
            var durationS = e.Args.Length >= 5
                && double.TryParse(e.Args[4], System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            Lyrics.RunConsoleTestAsync(title, artist, secondLine, durationS).GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        // 检查更新诊断入口：TaskbarLyrics.exe --update-test
        if (e.Args.Length >= 1 && e.Args[0] == "--update-test")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            Updater.RunConsoleTestAsync().GetAwaiter().GetResult();
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

        // 单实例：再开一份就是两个歌词条、两个托盘图标，后台的任务栏 UIA 枚举和歌词请求也跟着翻倍。
        // 只在正常运行这条路径上占锁，上面几个分支都不占：中继进程（--apply-update）本来就要跟
        // 旧进程、跟它随后拉起的新版先后交替；两个控制台诊断不碰界面；--settings 只弹一个模态设置窗、
        // 关掉即退，不建歌词条和托盘、也不跑后台枚举，不在这把锁要防的范围内——反过来它若占了锁，
        // 设置窗开着时用户再正常启动，会被告知「已在运行（见系统托盘）」，托盘里却什么都没有
        if (!AcquireSingleInstance())
        {
            MessageBox.Show("任务栏歌词已在运行（见系统托盘）。", "任务栏歌词",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
        // 主动放锁，更新接力时新版不必等到本进程彻底消失。ReleaseMutex 必须跟 WaitOne 在同一线程，
        // OnExit 与 OnStartup 都在 UI 线程上；万一不满足也只是抛个异常，进程退出时系统照样收回
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* 见上 */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        base.OnExit(e);
    }
}
