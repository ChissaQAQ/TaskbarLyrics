// 开机自启：读写注册表 HKCU\...\Run 下的 TaskbarLyrics 项（移植自 autostart.py）。
using Microsoft.Win32;

namespace TaskbarLyrics;

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarLyrics";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath
                      ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                      ?? "";
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            try { key.DeleteValue(ValueName); } catch { /* 不存在时忽略 */ }
        }
    }
}
