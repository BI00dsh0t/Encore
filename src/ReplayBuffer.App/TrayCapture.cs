using System.Windows;
using System.Windows.Media;
using H.NotifyIcon;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;

namespace ReplayBuffer.App;

/// <summary>
/// The tray icon's capture state (story 5.15): grey while nothing captures, red while the Replay Buffer runs,
/// and red with a dot while recording, with the state in the tooltip. It shows whatever <c>overlayDot</c> says,
/// so the state can be read when the Overlay's dot is hidden. An engine crash loop shows the error state
/// instead: "!" on amber, until Restart from the menu (AD-16).
/// </summary>
internal static class TrayCapture
{
    /// <summary>The tooltip's state while the engine is in a crash loop.</summary>
    public const string CrashLoopText = "The engine keeps crashing; Restart from this menu";

    private static readonly Color Off = Color.FromRgb(0x61, 0x61, 0x61);
    private static readonly Color On = Color.FromRgb(0xC6, 0x28, 0x28);
    private static readonly Color Error = Color.FromRgb(0xFF, 0xA0, 0x00);

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

    /// <summary>The error state's icon, for <see cref="EngineStatus.CrashLoop"/>.</summary>
    public static GeneratedIconSource CrashLoopIcon() => new()
    {
        Text = "!",
        Foreground = Brushes.Black,
        Background = new SolidColorBrush(Error),
        FontWeight = FontWeights.Bold,
    };
}
