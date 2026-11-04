using System.Windows;
using System.Windows.Media;
using H.NotifyIcon;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;

namespace ReplayBuffer.App;

/// <summary>
/// The tray icon's capture state (story 5.15): grey while nothing captures, red while the Replay Buffer runs,
/// and red with a dot while recording, with the state in the tooltip. It shows whatever <c>overlayDot</c> says,
/// so the state can be read when the Overlay's dot is hidden.
/// </summary>
internal static class TrayCapture
{
    private static readonly Color Off = Color.FromRgb(0x61, 0x61, 0x61);
    private static readonly Color On = Color.FromRgb(0xC6, 0x28, 0x28);

    /// <summary>The state the tray shows: the Overlay's dot before <c>overlayDot</c>, none while disconnected.</summary>
    public static OverlayDot For(IEngineSession engine) =>
        OverlayViewModel.DotFor(engine.Status == EngineStatus.Connected ? engine.State?.Capture : null);

    public static string Describe(OverlayDot dot) => dot switch
    {
        OverlayDot.Solid => "Recording",
        OverlayDot.Hollow => "Replay Buffer on",
        _ => "Not capturing",
    };

    public static GeneratedIconSource Icon(OverlayDot dot) => new()
    {
        Text = dot == OverlayDot.Solid ? "●" : "R",
        Foreground = Brushes.White,
        Background = new SolidColorBrush(dot == OverlayDot.None ? Off : On),
        FontWeight = FontWeights.Bold,
    };
}
