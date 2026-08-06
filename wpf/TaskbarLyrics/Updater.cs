// 检查更新与自动更新：
// 查 GitHub 最新 release 的 tag 与当前版本比较；更新用「新 exe 接力替换」——
// 下载新 exe 后以 --apply-update 启动它，本进程退出，新进程等旧进程退出后
// 覆盖目标路径并重启（正在运行的 exe 不能直接覆盖自己）。
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TaskbarLyrics;

public sealed record ReleaseInfo(string Tag, Version Version, string AssetUrl, string Notes);

public static class Updater
{
    // 用 /releases/latest 而不是「列表取第一条」：这个端点由 GitHub 保证跳过草稿与预发布，
    // 正好是「稳定版才推给用户」的语义。代价是它返回单个对象、不是数组
    private const string ReleasesApi =
        "https://api.github.com/repos/ChissaQAQ/TaskbarLyrics/releases/latest";

    private static readonly HttpClient Http = CreateApiClient();

    /// <summary>GitHub API 强制要求带 User-Agent，缺了会直接 403。</summary>
    private static HttpClient CreateApiClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TaskbarLyrics-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>下载专用客户端：Timeout 管的是整个请求（含收完响应体），
    /// 25MB 的单文件 exe 用查询用的 10 秒会在稍慢的链路上直接超时。</summary>
    private static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>当前版本（来自程序集版本号，csproj 的 Version，发布时与 tag 同步）。</summary>
    public static readonly Version CurrentVersion = ReadCurrentVersion();

    private static Version ReadCurrentVersion()
    {
        try
        {
            var attr = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attr != null && Version.TryParse(attr.InformationalVersion.Split('+')[0], out var v))
                return v;
        }
        catch { /* 读取失败用兜底版本 */ }
        return new Version(1, 0, 0);
    }

    /// <summary>查最新 release；没有新版本或查询失败返回 null（调用方区分提示）。</summary>
    /// <returns>(最新 release, 是否有更新)</returns>
    public static async Task<(ReleaseInfo? Latest, bool HasUpdate)> CheckLatestAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(ReleasesApi));
        var rel = doc.RootElement;
        if (rel.ValueKind != JsonValueKind.Object) return (null, false);
        var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver))
            return (null, false);
        string? url = null;
        if (rel.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var n) && n.GetString() == "TaskbarLyrics.exe"
                    && a.TryGetProperty("browser_download_url", out var u))
                    url = u.GetString();
            }
        }
        var notes = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var info = url != null ? new ReleaseInfo(tag, ver, url, notes) : null;
        return (info, info != null && ver > CurrentVersion);
    }

    public static string NewExeDir => Path.Combine(AppContext.BaseDirectory, "updates");
    public static string NewExePath => Path.Combine(NewExeDir, "TaskbarLyrics-new.exe");

    /// <summary>下载新版 exe 到 exe 同目录的 updates 子目录。
    ///
    /// 落盘前先验一遍是不是 PE 文件：更新源不可达时，代理/网关常返回 200 加一页 HTML
    /// 错误页；照抄成 exe 写下去，再拿它去接力替换，替换出来的就是个跑不起来的坏文件。</summary>
    public static async Task<string> DownloadAsync(string url)
    {
        var bytes = await Download.GetByteArrayAsync(url);
        if (bytes.Length < 1024 * 1024 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidDataException(
                $"下载到的不是可执行文件（{bytes.Length} 字节），更新源可能返回了错误页");
        Directory.CreateDirectory(NewExeDir);
        await File.WriteAllBytesAsync(NewExePath, bytes);
        return NewExePath;
    }

    /// <summary>启动新 exe 进入接力替换模式（随后调用方应退出本进程）。</summary>
    public static void StartApplyAndExit(string newExePath, Action quit)
    {
        var target = Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo(newExePath,
            $"--apply-update \"{target}\" {Environment.ProcessId}")
        {
            WorkingDirectory = Path.GetDirectoryName(newExePath)!,
        });
        quit();
    }

    /// <summary>接力替换入口（新 exe 以 --apply-update &lt;目标路径&gt; &lt;旧pid&gt; 启动）：
    /// 等旧进程退出后覆盖目标并重启。</summary>
    public static void ApplyUpdateMain(string targetExe, int oldPid)
    {
        for (var i = 0; i < 60; i++) // 最多等 30s
        {
            try
            {
                if (Process.GetProcessById(oldPid).HasExited) break;
            }
            catch { break; } // 进程不存在 = 已退出
            Thread.Sleep(500);
        }
        Thread.Sleep(500); // 等文件句柄释放
        var copied = false;
        Exception? lastError = null;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                File.Copy(Environment.ProcessPath!, targetExe, true);
                copied = true;
                break;
            }
            catch (Exception ex) { lastError = ex; Thread.Sleep(500); }
        }
        // 覆盖不了（目标仍被占用、装在只读目录）时绝不能装作成功：原先 10 次全失败也照样
        // 重启旧 exe，用户看到程序回来了、以为已经是新版，实际什么都没变，连一行日志都没有。
        // 这里记日志 + 明确告知，并把新 exe 另存一份——Cleanup() 只删 TaskbarLyrics-new.exe，
        // 换个带版本号的名字才不会在下次启动时被清掉，用户才真能手工替换。
        if (!copied)
        {
            Log.Error("applyupdate", lastError);
            var keep = Path.Combine(NewExeDir, $"TaskbarLyrics-{CurrentVersion}.exe");
            try { File.Copy(Environment.ProcessPath!, keep, true); }
            catch (Exception ex) { Log.Error("applyupdate-keep", ex); keep = Environment.ProcessPath!; }
            System.Windows.MessageBox.Show(
                $"更新失败：无法覆盖\n{targetExe}\n\n将继续以旧版本启动。\n"
                + $"新版本已保存在：\n{keep}\n退出程序后手动替换即可（详情见 error.log）。",
                "任务栏歌词", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        Process.Start(new ProcessStartInfo(targetExe)
        {
            WorkingDirectory = Path.GetDirectoryName(targetExe)!,
        });
    }

    /// <summary>启动时清掉上次更新留下的临时新 exe。</summary>
    public static void Cleanup()
    {
        try { if (File.Exists(NewExePath)) File.Delete(NewExePath); } catch { /* 占用就留着 */ }
    }
}
