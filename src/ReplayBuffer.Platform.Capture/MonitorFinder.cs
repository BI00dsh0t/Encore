using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace ReplayBuffer.Platform.Capture;

/// <summary>A display monitor: its <c>HMONITOR</c> and its GDI device name, such as <c>\\.\DISPLAY1</c>.</summary>
public sealed record MonitorInfo(nint Handle, string DeviceName, bool IsPrimary);

/// <summary>Finds the monitor a Monitor Recording captures (6.9) by its device name.</summary>
public static unsafe class MonitorFinder
{
    private const uint MonitorInfoPrimary = 1; // MONITORINFOF_PRIMARY

    /// <summary>
    /// The monitor whose device name is <paramref name="deviceName"/> (ignoring case); the primary
    /// monitor when it is null or no monitor has it, the latter logged at Warning.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is attached at all.</exception>
    public static MonitorInfo Find(string? deviceName, ILogger? logger = null)
    {
        var monitors = All();
        if (deviceName is not null)
        {
            if (monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                return named;
            }

            logger?.LogWarning("No monitor is named '{Name}'; the primary monitor is captured", deviceName);
        }

        if ((monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault()) is { } primary)
        {
            return primary;
        }

        var handle = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        return handle.IsNull
            ? throw new InvalidOperationException("No display monitor is attached.")
            : new MonitorInfo((nint)handle.Value, "", true);
    }

    /// <summary>Every attached display monitor, in <c>EnumDisplayMonitors</c> order.</summary>
    public static IReadOnlyList<MonitorInfo> All()
    {
        var monitors = new List<MonitorInfo>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            PInvoke.EnumDisplayMonitors(HDC.Null, null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnMonitor(HMONITOR monitor, HDC hdc, RECT* clip, LPARAM data)
    {
        var monitors = (List<MonitorInfo>)GCHandle.FromIntPtr(data).Target!;
        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
        {
            monitors.Add(new MonitorInfo(
                (nint)monitor.Value,
                info.szDevice.AsReadOnlySpan().SliceAtNull().ToString(),
                (info.monitorInfo.dwFlags & MonitorInfoPrimary) != 0));
        }

        return true;
    }
}
