namespace Encore.Platform.Capture;

/// <summary>One monitor's place on the virtual desktop in physical pixels, from the current display mode.</summary>
public sealed record MonitorPlacement(MonitorInfo Monitor, int X, int Y, int Width, int Height, int RefreshHz)
{
    public override string ToString() =>
        $"{Monitor.DeviceName} {Width}x{Height}@{RefreshHz}Hz at ({X},{Y}){(Monitor.IsPrimary ? " primary" : "")}";
}
