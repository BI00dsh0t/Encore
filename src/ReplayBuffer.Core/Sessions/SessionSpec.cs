using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Core.Sessions;

/// <summary>What a session captures: a game's window, or (Monitor Recording, epic 3) a monitor.</summary>
public enum SessionKind
{
    Game,
    Monitor,
}

/// <summary>
/// The window a session captures, the process that owns it, and that process's Game Profile
/// (null for Global). A retarget keeps <see cref="ProcessId"/> (AD-19).
/// </summary>
public sealed record CaptureTarget(int ProcessId, nint Window, string? ProfileId);

/// <summary>One audio device a session captures: loopback for an Output Device, capture for an Input Device (AD-13).</summary>
public sealed record SessionAudioDevice(string EndpointId, AudioFlow Flow, string FriendlyName);

/// <summary>The audio devices a session captures, in stream order; the first is the default stream.</summary>
public sealed record SessionAudio(IReadOnlyList<SessionAudioDevice> Devices);

/// <summary>
/// A session's description (AD-22): its kind, its target, the settings snapshot resolved once at
/// Start (AD-5), and its audio. Only <see cref="Target"/> changes over a session, on a retarget.
/// </summary>
public sealed record SessionSpec(SessionKind Kind, CaptureTarget Target, EffectiveSettings Snapshot, SessionAudio Audio);
