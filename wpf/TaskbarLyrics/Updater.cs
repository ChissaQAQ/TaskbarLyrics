// 检查更新与自动更新：
// 查 Gitea 最新 release 的 tag 与当前版本比较；更新用「新 exe 接力替换」——
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
    private const string ReleasesApi =
        "http://gitea.local:3300/api/v1/repos/blueberry/TaskbarLyrics/releases?limit=1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return (null, false);
        var rel = doc.RootElement[0];
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

    /// <summary>下载新版 exe 到 exe 同目录的 updates 子目录。</summary>
    public static async Task<string> DownloadAsync(string url)
    {
        Directory.CreateDirectory(NewExeDir);
        var bytes = await Http.GetByteArrayAsync(url);
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
        for (var i = 0; i < 10; i++)
        {
            try
            {
                File.Copy(Environment.ProcessPath!, targetExe, true);
                break;
            }
            catch { Thread.Sleep(500); }
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
