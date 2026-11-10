namespace Encore.Platform.Capture;

/// <summary>A display monitor: its <c>HMONITOR</c> and its GDI device name, such as <c>\\.\DISPLAY1</c>.</summary>
public sealed record MonitorInfo(nint Handle, string DeviceName, bool IsPrimary);
