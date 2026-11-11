namespace Encore.Platform.Capture;

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
