using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;

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
/// Whether a Recording is running (AD-19): while one is, a switch away from the Target is held, and
/// it is committed the moment the flag clears. A stub until epic-recordings; only tests set it.
/// </summary>
public sealed record RecordingRunning(bool Running) : SessionMessage;

/// <summary>
/// The game session that follows the Target Game: the Replay Buffer's, or the debug capture's
/// (<c>serve --debug-capture</c>). Each new Target ends the game session and starts one on the
/// Target's window.
/// </summary>
/// <param name="Audio">The audio of each session it starts, from the snapshot its Start resolved.</param>
/// <param name="Failed">Called on the orchestrator thread when a session on the Target couldn't be started or retargeted.</param>
public sealed record TargetCapture(Func<EffectiveSettings, SessionAudio> Audio, Action<TargetCandidate, Exception> Failed);

/// <summary>
/// How the orchestrator turns a <c>save</c> press into a Clip (R4, AD-19, AD-21).
/// </summary>
/// <param name="Paths">Reserves each Clip's path.</param>
/// <param name="DefaultOutputFolder">The Output Folder when the Global <c>outputFolder</c> is unset.</param>
/// <param name="Saved">Called on the orchestrator thread once a Clip is written.</param>
/// <param name="Failed">Called on the orchestrator thread when a save could not be written.</param>
/// <param name="NotSaved">
/// Called on the orchestrator thread when a save was refused and made no Clip, e.g. with
/// <see cref="Contracts.ErrorCodes.ClipTooShort"/>; null ignores it.
/// </param>
/// <param name="ExitDrain">
/// How long a game session that lost its Target by exit keeps its Replay Buffer for a <c>save</c>
/// (R1); null is <see cref="DefaultExitDrain"/>.
/// </param>
public sealed record ReplayClips(
    OutputPathAllocator Paths,
    Func<string> DefaultOutputFolder,
    Action<ClipSaved> Saved,
    Action<Exception> Failed,
    Action<ClipFailed>? NotSaved = null,
    TimeSpan? ExitDrain = null)
{
    /// <summary>R1's exit drain: 30 s.</summary>
    public static readonly TimeSpan DefaultExitDrain = TimeSpan.FromSeconds(30);
}
