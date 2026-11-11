using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Encore.Platform.Desktop;

/// <summary>The work area of every monitor: its area without the taskbar, in physical pixels, with its effective DPI.</summary>
public static unsafe class WorkAreas
{
    private const int DefaultDpi = 96;

    public static IReadOnlyList<MonitorArea> Current()
    {
        var areas = new List<MonitorArea>();
        var handle = GCHandle.Alloc(areas);
        try
        {
            PInvoke.EnumDisplayMonitors(HDC.Null, null, &Collect, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return areas;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL Collect(HMONITOR monitor, HDC context, RECT* clip, LPARAM list)
    {
        if (TryRead(monitor, out var area))
        {
            ((List<MonitorArea>)GCHandle.FromIntPtr(list).Target!).Add(area);
        }

        return true;
    }

    private static bool TryRead(HMONITOR monitor, out MonitorArea area)
    {
        area = default;
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Failed)
        {
            dpiX = DefaultDpi;
        }

        var work = info.rcWork;
        area = new MonitorArea(work.left, work.top, work.right, work.bottom, (int)dpiX);
        return true;
    }
}
