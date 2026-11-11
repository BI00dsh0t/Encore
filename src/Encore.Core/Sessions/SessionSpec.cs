using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

/// <summary>A session's description: kind, target, the settings snapshot resolved once at Start, and audio.</summary>
public sealed record SessionSpec(
    SessionKind Kind,
    CaptureTarget Target,
    EffectiveSettings Snapshot,
    SessionAudio Audio,
    bool MicMuted = true)
{
    /// <summary>A Monitor session's screens, the primary first: two or more are composited. Null reads the older key.</summary>
    public IReadOnlyList<Contracts.Messages.DisplayMonitor>? Screens { get; init; }
}
