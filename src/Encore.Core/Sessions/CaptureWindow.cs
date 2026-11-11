namespace Encore.Core.Sessions;

/// <summary>The window <see cref="CaptureWait.Find"/> names: <see cref="Window"/>, or none, with <see cref="Why"/>.</summary>
public readonly record struct CaptureWindow(nint Window, string? Why)
{
    public bool Found => Window != 0;

    public static CaptureWindow Of(nint window) => new(window, null);

    public static CaptureWindow None(string why) => new(0, why);
}
