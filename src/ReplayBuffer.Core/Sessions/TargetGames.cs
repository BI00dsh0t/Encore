using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Core.Sessions;

/// <summary>
/// A game Game Detection proposes as the Target Game: its process, the window to capture, its Game
/// Profile id (AD-18's canonical executable path) and its display name.
/// </summary>
public sealed record TargetCandidate(int ProcessId, nint Window, string ProfileId, string Name)
{
    /// <summary>The Target Game as <c>target.changed</c> and the snapshot carry it.</summary>
    public TargetGame ToTargetGame() => new(ProfileId, Name, ProcessId);
}

/// <summary>
/// Game Detection proposes <paramref name="To"/> (none when the Target is gone) as the Target Game
/// (AD-19): only the orchestrator commits it and publishes <c>target.changed</c>.
/// </summary>
public sealed record ProposeTarget(TargetCandidate? To, TargetChangeReason Reason) : SessionMessage;

/// <summary>
/// The debug capture that follows the Target Game (<c>serve --debug-capture</c>), until the Replay
/// Buffer starts sessions on <c>target.changed</c> (epic 4): each new Target ends the game session
/// and starts one on the Target's window.
/// </summary>
/// <param name="Audio">The audio of each session it starts.</param>
/// <param name="Failed">Called on the orchestrator thread when a session on the Target couldn't be started or retargeted.</param>
public sealed record TargetCapture(Func<SessionAudio> Audio, Action<TargetCandidate, Exception> Failed);
