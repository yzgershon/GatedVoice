using System.Diagnostics;
using Microsoft.Win32;

namespace Flow;

/// <summary>Toggles "launch ShyVoice when I sign in" via the per-user Run registry key.</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShyVoice";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                      ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (k == null) return;

        if (on)
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe)) k.SetValue(ValueName, $"\"{exe}\"");
        }
        else if (k.GetValue(ValueName) != null)
        {
            k.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
