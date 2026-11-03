using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace ReplayBuffer.Platform.Desktop;

/// <summary>A monitor's full area (<c>rcMonitor</c>) in physical pixels, and its effective DPI.</summary>
public readonly record struct MonitorArea(int Left, int Top, int Right, int Bottom, int Dpi);

/// <summary>Which monitor a game is on, and where an Overlay sits on it.</summary>
public static unsafe class Monitors
{
    private const int DefaultDpi = 96;

    /// <summary>
    /// The monitor holding the process's largest visible top-level window, or the primary monitor
    /// when <paramref name="processId"/> is <c>null</c> or has no visible window.
    /// </summary>
    /// <exception cref="Win32Exception">Not even the primary monitor could be read.</exception>
    public static MonitorArea ForProcess(int? processId)
    {
        if (processId is { } pid && LargestWindow(pid) is { } window
            && TryRead(PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), out var area))
        {
            return area;
        }

        return TryRead(PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY), out var primary)
            ? primary
            : throw new Win32Exception();
    }

    /// <summary>
    /// A square of <paramref name="sizeDip"/> in the top-right corner of <paramref name="monitor"/>,
    /// <paramref name="insetDip"/> in from the top and the right, both scaled by the monitor's DPI.
    /// </summary>
    /// <returns>The square's top-left corner and side, in physical pixels.</returns>
    public static (int X, int Y, int Size) TopRight(MonitorArea monitor, double sizeDip, double insetDip)
    {
        var scale = (double)monitor.Dpi / DefaultDpi;
        var size = (int)Math.Round(sizeDip * scale);
        var inset = (int)Math.Round(insetDip * scale);
        return (monitor.Right - inset - size, monitor.Top + inset, size);
    }

    // Top-level windows in Z order, without a callback (no EnumWindows).
    private static HWND? LargestWindow(int processId)
    {
        HWND? largest = null;
        long largestArea = -1;
        var window = HWND.Null;
        while ((window = PInvoke.FindWindowEx(HWND.Null, window, default(PCWSTR), default(PCWSTR))) != HWND.Null)
        {
            uint owner;
            if (PInvoke.GetWindowThreadProcessId(window, &owner) == 0 || owner != (uint)processId
                || !PInvoke.IsWindowVisible(window) || !PInvoke.GetWindowRect(window, out var rect))
            {
                continue;
            }

            var area = (long)(rect.right - rect.left) * (rect.bottom - rect.top);
            if (area > largestArea)
            {
                largest = window;
                largestArea = area;
            }
        }

        return largest;
    }

    private static bool TryRead(HMONITOR monitor, out MonitorArea area)
    {
        area = default;
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (monitor.IsNull || !PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Failed)
        {
            dpiX = DefaultDpi;
        }

        var r = info.rcMonitor;
        area = new MonitorArea(r.left, r.top, r.right, r.bottom, (int)dpiX);
        return true;
    }
}
