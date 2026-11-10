using Encore.Core.Monitors;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace Encore.Platform.Capture;

/// <summary>One monitor's place on the virtual desktop in physical pixels, from the current display mode.</summary>
public sealed record MonitorPlacement(MonitorInfo Monitor, int X, int Y, int Width, int Height, int RefreshHz)
{
    public override string ToString() => $"{Monitor.DeviceName} {Width}x{Height}@{RefreshHz}Hz at ({X},{Y}){(Monitor.IsPrimary ? " primary" : "")}";
}

/// <summary>The canvas of a multi-monitor recording: the monitors' bounding box, scaled down only to fit the codec.</summary>
public static class MonitorCanvas
{
    /// <summary>The attached monitors named in deviceNames, or all of them when it is null or empty; the primary first.</summary>
    public static IReadOnlyList<MonitorPlacement> Place(IReadOnlyCollection<string>? deviceNames)
    {
        var placements = new List<MonitorPlacement>();
        foreach (var monitor in MonitorFinder.All().OrderBy(m => m.IsPrimary ? 0 : 1))
        {
            if (deviceNames is { Count: > 0 } && !deviceNames.Contains(monitor.DeviceName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var mode = new DEVMODEW { dmSize = (ushort)System.Runtime.CompilerServices.Unsafe.SizeOf<DEVMODEW>() };
            // A mode that reads as 0x0 is skipped, as if the monitor weren't attached.
            if (PInvoke.EnumDisplaySettings(monitor.DeviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode)
                && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            {
                placements.Add(new MonitorPlacement(
                    monitor, mode.dmPosition.x, mode.dmPosition.y, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency));
            }
        }

        return placements;
    }

    /// <summary>The attached monitors among a recording's screens, in the same order; a missing or 0x0 screen is left out.</summary>
    public static IReadOnlyList<MonitorPlacement> Place(IReadOnlyList<Contracts.Messages.DisplayMonitor> screens)
    {
        var placed = Place((IReadOnlyCollection<string>?)null);
        return [.. screens.Select(s => placed.FirstOrDefault(p => string.Equals(p.Monitor.DeviceName, s.DeviceName, StringComparison.OrdinalIgnoreCase))).OfType<MonitorPlacement>()];
    }

    /// <summary>The bounding box of <paramref name="placements"/> and its top-left corner on the virtual desktop.</summary>
    public static (VideoSize Size, int Left, int Top) Bounds(IReadOnlyList<MonitorPlacement> placements)
    {
        ArgumentOutOfRangeException.ThrowIfZero(placements.Count);
        var left = placements.Min(p => p.X);
        var top = placements.Min(p => p.Y);
        var right = placements.Max(p => p.X + p.Width);
        var bottom = placements.Max(p => p.Y + p.Height);
        return (new VideoSize(right - left, bottom - top), left, top);
    }

    /// <summary>Scales a size down, aspect kept, so neither side exceeds maxSide; never up, and both sides are made even.</summary>
    public static VideoSize Fit(VideoSize native, int maxSide)
    {
        var (width, height) = MonitorCanvasRule.Fit(native.Width, native.Height, maxSide);
        return new VideoSize(width, height);
    }
}
