using Encore.App.Engine;
using Encore.App.Overlay;

namespace Encore.App;

/// <summary>The tray's capture state, and the icon that shows it: the mark when idle, the tile while capturing.</summary>
internal static class TrayCapture
{
    /// <summary>The tooltip's state while the engine is in a crash loop.</summary>
    public const string CrashLoopText = "Encore's background service keeps crashing; Restart from this menu";

    /// <summary>The state the tray shows: the Overlay's dot before <c>overlayDot</c>, none while disconnected.</summary>
    public static OverlayDot For(IEngineSession engine) =>
        OverlayViewModel.DotFor(engine.ConnectedState()?.Capture);

    public static string Describe(OverlayDot dot) => dot switch
    {
        OverlayDot.Solid => "Recording",
        OverlayDot.Hollow => "Ready to save Clips",
        _ => "Not capturing",
    };

    /// <summary>The icon file for a state; high contrast shows the plain mark for every state and leaves it to the tooltip.</summary>
    public static string IconName(OverlayDot dot, bool crashLoop, TaskbarLook look)
    {
        var idle = look.IsLight ? TrayIcons.IdleLight : TrayIcons.IdleDark;
        if (look.IsHighContrast)
        {
            return idle;
        }

        if (crashLoop)
        {
            return look.IsLight ? TrayIcons.ErrorLight : TrayIcons.ErrorDark;
        }

        return dot switch
        {
            OverlayDot.Solid => TrayIcons.Recording,
            OverlayDot.Hollow => TrayIcons.Ready,
            _ => idle,
        };
    }
}
