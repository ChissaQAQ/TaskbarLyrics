// 崩溃/异常日志：写在 exe 同目录的 error.log（与 config.json 同级）。
// 长时间运行时的偶发异常最难查的地方在于它什么都不留：UI 线程一个未捕获异常
// 就直接终止进程，事后连异常类型和调用栈都拿不到。这里只做最朴素的一件事——
// 把异常按时间写下来，并给文件封顶，避免某个每秒重现的异常把磁盘写满。
using System.IO;
using System.Text;

namespace TaskbarLyrics;

internal static class Log
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "error.log");
    private const long MaxBytes = 256 * 1024; // 超过就重开一份，只保留最近的现场
    private static readonly object Gate = new();

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
            lock (Gate)
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                var sb = new StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append("] ").Append(tag).Append(": ");
                sb.AppendLine(body);
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 写不进去就算了
        }
    }
}
