using Encore.Core.Monitors;

namespace Encore.Platform.Capture;

/// <summary>The canvas of a multi-monitor recording: the monitors' bounding box, scaled down only to fit the codec.</summary>
public static class MonitorCanvas
{
    /// <summary>The attached monitors named in deviceNames, or all of them when it is null or empty; the primary first.</summary>
    public static IReadOnlyList<MonitorPlacement> Place(IReadOnlyCollection<string>? deviceNames)
    {
        var placements = new List<MonitorPlacement>();
        foreach (var monitor in MonitorFinder.All().OrderBy(monitor => monitor.IsPrimary ? 0 : 1))
        {
            if (deviceNames is { Count: > 0 } && !deviceNames.Contains(monitor.DeviceName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // A mode that reads as 0x0 is skipped, as if the monitor weren't attached.
            if (MonitorFinder.CurrentMode(monitor.DeviceName) is { Width: > 0, Height: > 0 } mode)
            {
                placements.Add(new MonitorPlacement(monitor, mode.X, mode.Y, mode.Width, mode.Height, mode.RefreshHz));
            }
        }

        return placements;
    }

    /// <summary>The attached monitors among a recording's screens, in the same order; a missing or 0x0 screen is left out.</summary>
    public static IReadOnlyList<MonitorPlacement> Place(IReadOnlyList<Contracts.Messages.DisplayMonitor> screens)
    {
        var placed = Place((IReadOnlyCollection<string>?)null);
        return
        [
            .. screens
                .Select(screen => placed.FirstOrDefault(
                    placement => string.Equals(placement.Monitor.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase)))
                .OfType<MonitorPlacement>(),
        ];
    }

    /// <summary>The bounding box of <paramref name="placements"/> and its top-left corner on the virtual desktop.</summary>
    public static (VideoSize Size, int Left, int Top) Bounds(IReadOnlyList<MonitorPlacement> placements)
    {
        ArgumentOutOfRangeException.ThrowIfZero(placements.Count);
        var left = placements.Min(placement => placement.X);
        var top = placements.Min(placement => placement.Y);
        var right = placements.Max(placement => placement.X + placement.Width);
        var bottom = placements.Max(placement => placement.Y + placement.Height);
        return (new VideoSize(right - left, bottom - top), left, top);
    }

    /// <summary>Scales a size down, aspect kept, so neither side exceeds maxSide; never up, and both sides are made even.</summary>
    public static VideoSize Fit(VideoSize native, int maxSide)
    {
        var (width, height) = MonitorCanvasRule.Fit(native.Width, native.Height, maxSide);
        return new VideoSize(width, height);
    }
}
