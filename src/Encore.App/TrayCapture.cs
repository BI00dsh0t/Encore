using System.Windows;
using System.Windows.Media;
using H.NotifyIcon;
using Encore.App.Engine;
using Encore.App.Overlay;

namespace Encore.App;

/// <summary>The tray icon's capture state: grey when idle, red while Rewind runs, a dot while recording, "!" for a crash loop.</summary>
internal static class TrayCapture
{
    /// <summary>The tooltip's state while the engine is in a crash loop.</summary>
    public const string CrashLoopText = "Encore's background service keeps crashing; Restart from this menu";

    private static readonly Color Off = Color.FromRgb(0x61, 0x61, 0x61);
    private static readonly Color On = Color.FromRgb(0xC6, 0x28, 0x28);
    private static readonly Color Error = Color.FromRgb(0xFF, 0xA0, 0x00);

    /// <summary>The state the tray shows: the Overlay's dot before <c>overlayDot</c>, none while disconnected.</summary>
    public static OverlayDot For(IEngineSession engine) =>
        OverlayViewModel.DotFor(engine.ConnectedState()?.Capture);

    public static string Describe(OverlayDot dot) => dot switch
    {
        OverlayDot.Solid => "Recording",
        OverlayDot.Hollow => "Ready to save Clips",
        _ => "Not capturing",
    };

    public static GeneratedIconSource Icon(OverlayDot dot) => new()
    {
        Text = dot == OverlayDot.Solid ? "●" : "R",
        Foreground = Brushes.White,
        Background = new SolidColorBrush(dot == OverlayDot.None ? Off : On),
        FontWeight = FontWeights.Bold,
    };

    /// <summary>The error state's icon, for <see cref="EngineStatus.CrashLoop"/>.</summary>
    public static GeneratedIconSource CrashLoopIcon() => new()
    {
        Text = "!",
        Foreground = Brushes.Black,
        Background = new SolidColorBrush(Error),
        FontWeight = FontWeights.Bold,
    };
}
