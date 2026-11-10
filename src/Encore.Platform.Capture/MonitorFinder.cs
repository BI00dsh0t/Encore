using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Devices.Display;
using Windows.Win32.Graphics.Gdi;

namespace Encore.Platform.Capture;

/// <summary>A display monitor: its <c>HMONITOR</c> and its GDI device name, such as <c>\\.\DISPLAY1</c>.</summary>
public sealed record MonitorInfo(nint Handle, string DeviceName, bool IsPrimary);

/// <summary>A display monitor as the Main App lists it; a value that can't be read is null (false for HDR).</summary>
public sealed record MonitorDescription(
    string DeviceName,
    string FriendlyName,
    int Width,
    int Height,
    bool IsPrimary,
    string? MonitorId = null,
    int? X = null,
    int? Y = null,
    int? RefreshHz = null,
    bool Hdr = false);

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

    /// <summary>Every attached monitor, the primary first; sizes come from the display mode, never GDI's DPI-scaled rectangle.</summary>
    public static IReadOnlyList<MonitorDescription> Describe()
    {
        var targets = Targets();
        return
        [
            .. All()
                .OrderBy(m => m.IsPrimary ? 0 : 1)
                .Select(m =>
                {
                    var mode = CurrentMode(m.DeviceName);
                    targets.TryGetValue(m.DeviceName, out var target);
                    var name = target?.FriendlyName is { Length: > 0 } f ? f : DeviceString(m.DeviceName);
                    return new MonitorDescription(
                        m.DeviceName, name ?? m.DeviceName, mode?.Width ?? 0, mode?.Height ?? 0, m.IsPrimary,
                        target?.MonitorId, mode?.X, mode?.Y, mode?.RefreshHz, target?.Hdr ?? false);
                }),
        ];
    }

    /// <summary>One active source's monitor as DisplayConfig names it.</summary>
    private sealed record DisplayTarget(string? FriendlyName, string? MonitorId, bool Hdr);

    /// <summary>Each active monitor's DisplayConfig name, device path and HDR state, by GDI device name; empty on any failure.</summary>
    private static Dictionary<string, DisplayTarget> Targets()
    {
        var names = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);
        try
        {
            DISPLAYCONFIG_PATH_INFO[] paths;
            DISPLAYCONFIG_MODE_INFO[] modes;
            uint pathCount, modeCount;
            WIN32_ERROR result;
            do
            {
                if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != WIN32_ERROR.ERROR_SUCCESS)
                {
                    return names;
                }

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                result = PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes);
            }
            while (result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER);

            if (result != WIN32_ERROR.ERROR_SUCCESS)
            {
                return names;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0 || PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
                {
                    continue;
                }

                var device = source.viewGdiDeviceName.AsReadOnlySpan().SliceAtNull().ToString();
                var name = target.monitorFriendlyDeviceName.AsReadOnlySpan().SliceAtNull().ToString();
                var devicePath = target.monitorDevicePath.AsReadOnlySpan().SliceAtNull().ToString();
                var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                color.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                color.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO);
                color.header.adapterId = path.targetInfo.adapterId;
                color.header.id = path.targetInfo.id;
                var hdr = PInvoke.DisplayConfigGetDeviceInfo(&color.header) == 0 && color.advancedColorEnabled;
                if (device.Length > 0)
                {
                    names.TryAdd(device, new DisplayTarget(name.Length > 0 ? name : null, devicePath.Length > 0 ? devicePath : null, hdr));
                }
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Without DisplayConfig, the device descriptions stand in.
        }

        return names;
    }

    /// <summary>The first monitor's <c>EnumDisplayDevices</c> description under a device name, or null.</summary>
    private static string? DeviceString(string deviceName)
    {
        var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        return PInvoke.EnumDisplayDevices(deviceName, 0, ref device, 0)
            && device.DeviceString.AsReadOnlySpan().SliceAtNull().ToString() is { Length: > 0 } text
            ? text
            : null;
    }

    /// <summary>The current display mode: physical size, virtual-desktop position and refresh rate; null when unreadable.</summary>
    private static (int Width, int Height, int X, int Y, int RefreshHz)? CurrentMode(string deviceName)
    {
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        return PInvoke.EnumDisplaySettings(deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
            ? ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, mode.dmPosition.x, mode.dmPosition.y, (int)mode.dmDisplayFrequency)
            : null;
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
