using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Devices.Display;
using Windows.Win32.Graphics.Gdi;

namespace ReplayBuffer.Platform.Capture;

/// <summary>A display monitor: its <c>HMONITOR</c> and its GDI device name, such as <c>\\.\DISPLAY1</c>.</summary>
public sealed record MonitorInfo(nint Handle, string DeviceName, bool IsPrimary);

/// <summary>
/// A display monitor as the Main App lists it (6.11): its GDI device name, its friendly name, and its
/// current resolution in pixels.
/// </summary>
public sealed record MonitorDescription(string DeviceName, string FriendlyName, int Width, int Height, bool IsPrimary);

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

    /// <summary>
    /// Every attached display monitor, the primary first, then <c>EnumDisplayMonitors</c> order. A friendly
    /// name comes from DisplayConfig, else the monitor's <c>EnumDisplayDevices</c> description, else the
    /// device name; the size is the current display mode, whatever the caller's DPI awareness.
    /// </summary>
    public static IReadOnlyList<MonitorDescription> Describe()
    {
        var friendly = FriendlyNames();
        return
        [
            .. All()
                .OrderBy(m => m.IsPrimary ? 0 : 1)
                .Select(m =>
                {
                    var (width, height) = CurrentSize(m.DeviceName);
                    var name = friendly.TryGetValue(m.DeviceName, out var f) ? f : DeviceString(m.DeviceName);
                    return new MonitorDescription(m.DeviceName, name ?? m.DeviceName, width, height, m.IsPrimary);
                }),
        ];
    }

    /// <summary>The DisplayConfig friendly name of each active source's monitor, by GDI device name; empty on any failure.</summary>
    private static Dictionary<string, string> FriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (device.Length > 0 && name.Length > 0)
                {
                    names.TryAdd(device, name);
                }
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Without DisplayConfig, the device descriptions stand in.
        }

        return names;
    }

    /// <summary>The first monitor's <c>EnumDisplayDevices</c> description under <paramref name="deviceName"/>, or null.</summary>
    private static string? DeviceString(string deviceName)
    {
        var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        return PInvoke.EnumDisplayDevices(deviceName, 0, ref device, 0)
            && device.DeviceString.AsReadOnlySpan().SliceAtNull().ToString() is { Length: > 0 } text
            ? text
            : null;
    }

    /// <summary>The current display mode's size in pixels, or zero when it can't be read.</summary>
    private static (int Width, int Height) CurrentSize(string deviceName)
    {
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
        return PInvoke.EnumDisplaySettings(deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
            ? ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight)
            : (0, 0);
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
