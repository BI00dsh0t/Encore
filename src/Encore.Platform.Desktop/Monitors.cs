using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.HiDpi;

namespace Encore.Platform.Desktop;

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

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                IDXGIAdapter1* adapter = null;
                if (factory->EnumAdapters1(adapterIndex, &adapter).Failed)
                {
                    return null;
                }

                if (FindSupport(adapter, monitor, out var supports))
                {
                    return supports;
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

    /// <summary>Looks for the monitor among the adapter's outputs, then releases the adapter; true when found.</summary>
    private static bool FindSupport(IDXGIAdapter1* adapter, HMONITOR monitor, out bool? supports)
    {
        try
        {
            for (uint outputIndex = 0; ; outputIndex++)
            {
                IDXGIOutput* output = null;
                if (adapter->EnumOutputs(outputIndex, &output).Failed)
                {
                    supports = null;
                    return false;
                }

                if (OutputSupport(output, monitor, out supports))
                {
                    return true;
                }
            }
        }
        finally
        {
            adapter->Release();
        }
    }

    /// <summary>Whether the output shows the monitor, with its overlay support; the output is released either way.</summary>
    private static bool OutputSupport(IDXGIOutput* output, HMONITOR monitor, out bool? supports)
    {
        try
        {
            var shows = output->GetDesc().Monitor == monitor;
            supports = shows ? Supports(output) : null;
            return shows;
        }
        finally
        {
            output->Release();
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
        processId is { } id && LargestWindow(id) is { } window
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

        var bounds = info.rcMonitor;
        area = new MonitorArea(bounds.left, bounds.top, bounds.right, bounds.bottom, (int)dpiX);
        return true;
    }
}
