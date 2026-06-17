using System.Diagnostics;
using SysWidge.Interop;

namespace SysWidge.Hosting;

/// <summary>A located taskbar window plus the geometry we need to dock against it.</summary>
internal sealed class TaskbarInfo
{
    public required IntPtr Handle { get; init; }
    public required NativeMethods.RECT Rect { get; init; }
    public required uint Dpi { get; init; }

    public bool IsValid => Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle);
}

/// <summary>
/// Finds the primary taskbar window.
///
/// There can be more than one window of class "Shell_TrayWnd": some shell-replacement
/// and tray utilities create their own. So we enumerate all of them and only accept the
/// one actually owned by explorer.exe.
/// </summary>
internal static class TaskbarLocator
{
    public static TaskbarInfo? FindPrimaryTaskbar()
    {
        IntPtr hwnd = IntPtr.Zero;
        while (true)
        {
            hwnd = NativeMethods.FindWindowEx(IntPtr.Zero, hwnd, "Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero) break;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!IsExplorer(pid)) continue;

            NativeMethods.GetWindowRect(hwnd, out var rect);
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;

            return new TaskbarInfo { Handle = hwnd, Rect = rect, Dpi = dpi };
        }

        return null;
    }

    private static bool IsExplorer(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
