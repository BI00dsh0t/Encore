using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace ReplayBuffer.Platform.Capture;

/// <summary>
/// One monitor's place on the virtual desktop, in physical pixels (spike 11.1): its position and
/// size from the current display mode, whatever the caller's DPI awareness, and its refresh rate.
/// </summary>
public sealed record MonitorPlacement(MonitorInfo Monitor, int X, int Y, int Width, int Height, int RefreshHz)
{
    public override string ToString() => $"{Monitor.DeviceName} {Width}x{Height}@{RefreshHz}Hz at ({X},{Y}){(Monitor.IsPrimary ? " primary" : "")}";
}

/// <summary>
/// The canvas rule of a multi-monitor Monitor Recording (AD-25, design-multi-monitor.md), from spike
/// 11.1: the canvas is the bounding box of the recorded monitors in virtual-desktop pixels, and the
/// encoded size is that box scaled down, aspect kept, only to fit the codec's largest side, with even
/// sides. There is no pixel budget: the builder chose full width with HEVC or AV1 (2026-11-05).
/// </summary>
public static class MonitorCanvas
{
    /// <summary>NVENC's largest H.264 side, measured in spike 11.1 (the encoder probe reports no limit).</summary>
    public const int H264MaxSide = 4096;

    /// <summary>NVENC's largest HEVC and AV1 side, measured in spike 11.1.</summary>
    public const int HevcAv1MaxSide = 8192;

    /// <summary>
    /// Every attached monitor named in <paramref name="deviceNames"/> (ignoring case), or every one when
    /// it is null or empty, placed by its current display mode; the primary first.
    /// </summary>
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
            if (PInvoke.EnumDisplaySettings(monitor.DeviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode))
            {
                placements.Add(new MonitorPlacement(
                    monitor, mode.dmPosition.x, mode.dmPosition.y, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency));
            }
        }

        return placements;
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

    /// <summary>
    /// <paramref name="native"/> scaled down, aspect kept, so neither side exceeds
    /// <paramref name="maxSide"/> (the codec's largest side); never scaled up; both sides rounded
    /// down to even.
    /// </summary>
    public static VideoSize Fit(VideoSize native, int maxSide)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxSide / native.Width, (double)maxSide / native.Height));
        // The epsilon keeps an exact fit (5120 × 0.8 = 4096) from flooring to 4095.
        return VideoSize.EvenFloor((int)Math.Floor((native.Width * scale) + 1e-6), (int)Math.Floor((native.Height * scale) + 1e-6));
    }
}
