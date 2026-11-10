using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Devices.Display;
using Windows.Win32.Graphics.Gdi;

namespace Encore.Platform.Capture;

/// <summary>Finds the monitor a Monitor Recording captures, and describes the attached monitors for the App's list.</summary>
public static unsafe class MonitorFinder
{
    private const uint MonitorInfoPrimary = 1; // MONITORINFOF_PRIMARY

    /// <summary>The monitor with this device name, or the primary when the name is null or unknown; throws when none exists.</summary>
    public static MonitorInfo Find(string? deviceName, ILogger? logger = null)
    {
        var monitors = All();
        if (deviceName is not null)
        {
            var named = monitors.FirstOrDefault(
                monitor => string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                return named;
            }

            logger?.LogWarning("No monitor is named '{Name}'; the primary monitor is captured", deviceName);
        }

        if ((monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors.FirstOrDefault()) is { } primary)
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

    /// <summary>Every attached monitor, the primary first; sizes come from the display mode, never GDI's DPI-scaled rectangle.</summary>
    public static IReadOnlyList<MonitorDescription> Describe()
    {
        var targets = Targets();
        return [.. All().OrderBy(monitor => monitor.IsPrimary ? 0 : 1).Select(monitor => Describe(monitor, targets))];
    }

    /// <summary>The current display mode: physical size, virtual-desktop position and refresh rate; null when unreadable.</summary>
    internal static (int Width, int Height, int X, int Y, int RefreshHz)? CurrentMode(string deviceName)
    {
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        return PInvoke.EnumDisplaySettings(deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
            ? ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, mode.dmPosition.x, mode.dmPosition.y, (int)mode.dmDisplayFrequency)
            : null;
    }

    private static MonitorDescription Describe(MonitorInfo monitor, Dictionary<string, DisplayTarget> targets)
    {
        var mode = CurrentMode(monitor.DeviceName);
        targets.TryGetValue(monitor.DeviceName, out var target);
        var name = target?.FriendlyName is { Length: > 0 } friendly ? friendly : DeviceString(monitor.DeviceName);
        return new MonitorDescription(
            monitor.DeviceName, name ?? monitor.DeviceName, mode?.Width ?? 0, mode?.Height ?? 0, monitor.IsPrimary,
            target?.MonitorId, mode?.X, mode?.Y, mode?.RefreshHz, target?.Hdr ?? false);
    }

    /// <summary>One active source's monitor as DisplayConfig names it.</summary>
    private sealed record DisplayTarget(string? FriendlyName, string? MonitorId, bool Hdr);

    /// <summary>Each active monitor's DisplayConfig name, device path and HDR state, by GDI device name; empty on any failure.</summary>
    private static Dictionary<string, DisplayTarget> Targets()
    {
        var names = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (ReadActivePaths() is not { } paths)
            {
                return names;
            }

            foreach (var path in paths)
            {
                if (ReadTarget(path) is { } named)
                {
                    names.TryAdd(named.Device, named.Target);
                }
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Without DisplayConfig, the device descriptions stand in.
        }

        return names;
    }

    /// <summary>The active display paths, asked again while the buffers were too small; null when the query failed.</summary>
    private static DISPLAYCONFIG_PATH_INFO[]? ReadActivePaths()
    {
        const QUERY_DISPLAY_CONFIG_FLAGS activeOnly = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
        DISPLAYCONFIG_PATH_INFO[] paths;
        uint pathCount;
        WIN32_ERROR result;
        do
        {
            if (PInvoke.GetDisplayConfigBufferSizes(activeOnly, out pathCount, out var modeCount) != WIN32_ERROR.ERROR_SUCCESS)
            {
                return null;
            }

            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            result = PInvoke.QueryDisplayConfig(activeOnly, ref pathCount, paths, ref modeCount, modes);
        }
        while (result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER);

        return result == WIN32_ERROR.ERROR_SUCCESS ? paths[..(int)pathCount] : null;
    }

    /// <summary>One path's GDI device name and its monitor's names and HDR state; null when either name can't be read.</summary>
    private static (string Device, DisplayTarget Target)? ReadTarget(DISPLAYCONFIG_PATH_INFO path)
    {
        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        source.header = Header<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(
            DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, path.sourceInfo.adapterId, path.sourceInfo.id);
        var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        target.header = Header<DISPLAYCONFIG_TARGET_DEVICE_NAME>(
            DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, path.targetInfo.adapterId, path.targetInfo.id);
        if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0 || PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
        {
            return null;
        }

        var device = source.viewGdiDeviceName.AsReadOnlySpan().SliceAtNull().ToString();
        var name = target.monitorFriendlyDeviceName.AsReadOnlySpan().SliceAtNull().ToString();
        var devicePath = target.monitorDevicePath.AsReadOnlySpan().SliceAtNull().ToString();
        var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        const DISPLAYCONFIG_DEVICE_INFO_TYPE colorInfo = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        color.header = Header<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(colorInfo, path.targetInfo.adapterId, path.targetInfo.id);
        var hdr = PInvoke.DisplayConfigGetDeviceInfo(&color.header) == 0 && color.advancedColorEnabled;
        if (device.Length == 0)
        {
            return null;
        }

        return (device, new DisplayTarget(name.Length > 0 ? name : null, devicePath.Length > 0 ? devicePath : null, hdr));
    }

    /// <summary>The header DisplayConfigGetDeviceInfo reads: what to fetch, the struct's size, and for which path end.</summary>
    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header<T>(DISPLAYCONFIG_DEVICE_INFO_TYPE type, LUID adapterId, uint id)
        where T : unmanaged =>
        new() { type = type, size = (uint)sizeof(T), adapterId = adapterId, id = id };

    /// <summary>The first monitor's <c>EnumDisplayDevices</c> description under a device name, or null.</summary>
    private static string? DeviceString(string deviceName)
    {
        var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        return PInvoke.EnumDisplayDevices(deviceName, 0, ref device, 0)
            && device.DeviceString.AsReadOnlySpan().SliceAtNull().ToString() is { Length: > 0 } text
            ? text
            : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnMonitor(HMONITOR monitor, HDC deviceContext, RECT* clip, LPARAM data)
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
