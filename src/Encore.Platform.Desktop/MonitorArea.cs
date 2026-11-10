namespace Encore.Platform.Desktop;

/// <summary>A monitor's full area (<c>rcMonitor</c>) in physical pixels, and its effective DPI.</summary>
public readonly record struct MonitorArea(int Left, int Top, int Right, int Bottom, int Dpi);
