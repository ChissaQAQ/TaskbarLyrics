// 崩溃/异常日志：首选写在 exe 同目录的 error.log（与 config.json 同级），那里写不进去
// （装在 Program Files、从只读介质运行）时退到 %AppData%\TaskbarLyrics\error.log。
// 长时间运行时的偶发异常最难查的地方在于它什么都不留：UI 线程一个未捕获异常
// 就直接终止进程，事后连异常类型和调用栈都拿不到。这里只做最朴素的一件事——
// 把异常按时间写下来，并给文件封顶，避免某个每秒重现的异常把磁盘写满。
using System.IO;
using System.Text;

namespace TaskbarLyrics;

internal static class Log
{
    // 首选目录。更新中继进程跑在 updates\ 子目录里，会用 RedirectTo 改成目标 exe 所在目录
    private static string _dir = AppContext.BaseDirectory;
    private const long MaxBytes = 256 * 1024; // 超过就重开一份，只保留最近的现场
    private static readonly object Gate = new();

    /// <summary>把首选目录改到 dir（退路不变）。给更新中继进程用：它的 AppContext.BaseDirectory
    /// 是 updates\，不改的话日志落进 updates\error.log，而用户只会去 exe 旁边找。</summary>
    internal static void RedirectTo(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        lock (Gate) _dir = dir;
    }

    /// <summary>日志实际所在的目录（设置页「打开日志目录」用）：首选目录有 error.log 就是它，
    /// 否则退路目录有就是退路，两处都没有时仍给首选目录。</summary>
    internal static string LogDir()
    {
        string dir;
        lock (Gate) dir = _dir;
        try
        {
            if (File.Exists(Path.Combine(dir, "error.log"))) return dir;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return dir;
            var fallback = Path.Combine(appData, "TaskbarLyrics");
            return File.Exists(Path.Combine(fallback, "error.log")) ? fallback : dir;
        }
        catch
        {
            return dir;
        }
    }

    /// <summary>记一条异常。tag 标明来源（dispatcher / appdomain / task）。
    /// 日志本身写失败（目录只读、磁盘满）绝不能反过来把程序搞挂，全部吞掉。</summary>
    public static void Error(string tag, Exception? ex)
        => Write(tag, ex?.ToString() ?? "(无异常对象)");

    /// <summary>记一条没有异常对象的异常状况。
    /// 有些故障不以异常的形式出现——比如某个后台查询从此不再返回结果，
    /// 功能静默降级、用户看到的只是「位置不对」。这类现场同样只有写下来才查得到。</summary>
    public static void Note(string tag, string message) => Write(tag, message);

    private static void Write(string tag, string body)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append("] ").Append(tag).Append(": ");
            sb.AppendLine(body);
            var text = sb.ToString();
            lock (Gate)
            {
                // 每条都先试 exe 同目录，不在第一次失败后就永久切走：那里可能只是被
                // OneDrive / 杀软短暂锁了一下，下一条就能回到原处。日志写得很稀，多一次失败的尝试不算什么
                if (TryAppend(Path.Combine(_dir, "error.log"), text)) return;
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return; // 取不到就算了，别拼出相对路径写进当前目录
                var dir = Path.Combine(appData, "TaskbarLyrics");
                Directory.CreateDirectory(dir);
                TryAppend(Path.Combine(dir, "error.log"), text);
            }
        }
        catch
        {
            // 写不进去就算了
        }
    }

    private static bool TryAppend(string path, string text)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
            File.AppendAllText(path, text, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
