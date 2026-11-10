using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.HiDpi;

namespace Encore.Platform.Desktop;

/// <summary>A monitor's full area (<c>rcMonitor</c>) in physical pixels, and its effective DPI.</summary>
public readonly record struct MonitorArea(int Left, int Top, int Right, int Bottom, int Dpi);

/// <summary>Which monitor a game is on, and where an Overlay sits on it.</summary>
public static unsafe class Monitors
{
    private const int DefaultDpi = 96;

    /// <summary>The monitor holding the process's largest visible window, or the primary monitor when there is none.</summary>
    public static MonitorArea ForProcess(int? processId) =>
        TryRead(MonitorOf(processId), out var area) || TryRead(PrimaryMonitor(), out area)
            ? area
            : throw new Win32Exception();

    /// <summary>A square in the monitor's top-right corner, in physical pixels; the size and inset are DIPs scaled by its DPI.</summary>
    public static (int X, int Y, int Size) TopRight(MonitorArea monitor, double sizeDip, double insetDip)
    {
        var scale = (double)monitor.Dpi / DefaultDpi;
        var size = (int)Math.Round(sizeDip * scale);
        var inset = (int)Math.Round(insetDip * scale);
        return (monitor.Right - inset - size, monitor.Top + inset, size);
    }

    /// <summary>Whether the game's monitor has hardware overlay planes, or null when DXGI can't say. See docs/code-notes.md.</summary>
    public static bool? OverlayPlanes(int? processId)
    {
        var monitor = MonitorOf(processId);
        if (monitor.IsNull)
        {
            return null;
        }

        IDXGIFactory1* factory = null;
        try
        {
            var factoryIid = IDXGIFactory1.IID_Guid;
            if (PInvoke.CreateDXGIFactory1(&factoryIid, (void**)&factory).Failed)
            {
                return null;
            }

            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1* adapter = null;
                if (factory->EnumAdapters1(a, &adapter).Failed)
                {
                    return null;
                }

                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IDXGIOutput* output = null;
                        if (adapter->EnumOutputs(o, &output).Failed)
                        {
                            break;
                        }

                        try
                        {
                            if (output->GetDesc().Monitor == monitor)
                            {
                                return Supports(output);
                            }
                        }
                        finally
                        {
                            output->Release();
                        }
                    }
                }
                finally
                {
                    adapter->Release();
                }
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (factory is not null)
            {
                factory->Release();
            }
        }
    }

    private static bool? Supports(IDXGIOutput* output)
    {
        IDXGIOutput2* output2 = null;
        var iid = IDXGIOutput2.IID_Guid;
        if (((IUnknown*)output)->QueryInterface(&iid, (void**)&output2).Failed)
        {
            return null;
        }

        try
        {
            return output2->SupportsOverlays();
        }
        finally
        {
            output2->Release();
        }
    }

    // The monitor nearest the process's largest visible window, else the primary monitor.
    private static HMONITOR MonitorOf(int? processId) =>
        processId is { } pid && LargestWindow(pid) is { } window
            ? PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST)
            : PrimaryMonitor();

    private static HMONITOR PrimaryMonitor() =>
        PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

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
