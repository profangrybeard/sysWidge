using Microsoft.Win32;

namespace SysWidge.Config;

/// <summary>
/// Launch-at-login via the per-user Run key (no admin, no scheduled task).
/// The widget never flips this on its own — only the user, via the tray toggle.
/// </summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SysWidge";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // best-effort; a locked-down profile just means the toggle does nothing
        }
    }
}
