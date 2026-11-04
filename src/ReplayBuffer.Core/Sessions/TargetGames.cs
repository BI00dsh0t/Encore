using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Library;
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
/// <c>dev.simulate</c>'s Recording display state: sets <c>CaptureState.Recording</c> while no real
/// Recording runs, and is ignored while one does. It never holds the Target; only a real Recording does.
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
/// <param name="Failed">
/// Called on the orchestrator thread with the exception when a save could not be written, for
/// logging; <paramref name="NotSaved"/> then carries its outcome. Not called for a refusal.
/// </param>
/// <param name="NotSaved">
/// Called on the orchestrator thread for every accepted save that made no Clip, refused or failed,
/// with its reason (<see cref="Output.WriteFailures.ReasonOf"/>); null ignores it.
/// </param>
/// <param name="ExitDrain">
/// How long a game session that lost its Target by exit keeps its Replay Buffer for a <c>save</c>
/// (R1); null is <see cref="DefaultExitDrain"/>.
/// </param>
/// <param name="Saving">
/// Called on the orchestrator thread the moment a <c>save</c> press is accepted (R6), before its
/// Clip is cut; <c>clip.saved</c> or <c>clip.failed</c> follows. Null ignores it.
/// </param>
/// <param name="Ignored">
/// Called on the orchestrator thread for a <c>save</c> press inside the Save Cooldown (R5), with the
/// time since the accepted press; it makes no Clip and no event. Null ignores it.
/// </param>
/// <param name="Library">
/// Told when each Clip whose path was reserved starts and ends being written (R6), on the
/// orchestrator thread; null is <see cref="NoLibraryWrites.Instance"/>.
/// </param>
public sealed record ReplayClips(
    OutputPathAllocator Paths,
    Func<string> DefaultOutputFolder,
    Action<ClipSaved> Saved,
    Action<Exception> Failed,
    Action<ClipFailed>? NotSaved = null,
    TimeSpan? ExitDrain = null,
    Action<ClipSaving>? Saving = null,
    Action<TimeSpan>? Ignored = null,
    ILibraryWrites? Library = null)
{
    /// <summary>R6: at most this many Clips are written at once, across sessions.</summary>
    public const int MaxWriters = 2;

    /// <summary>R1's exit drain: 30 s.</summary>
    public static readonly TimeSpan DefaultExitDrain = TimeSpan.FromSeconds(30);
}

/// <summary>
/// How the orchestrator turns a <c>recordToggle</c> press into a Recording (R1, R2, AD-19, AD-21):
/// one fragmented MP4 from the game session's own encoder chain, beside its Replay Buffer.
/// </summary>
/// <param name="Paths">Reserves each Recording's path, in the same folder as the game's Clips.</param>
/// <param name="DefaultOutputFolder">The Output Folder when the profile's <c>outputFolder</c> is unset.</param>
/// <param name="Started">Called on the orchestrator thread once a Recording has started.</param>
/// <param name="Stopped">Called on the orchestrator thread once a Recording's writer has finished.</param>
/// <param name="Failed">
/// Called on the orchestrator thread with the exception when a Recording could not start, or its
/// writer failed, for logging.
/// </param>
/// <param name="Library">
/// Told when each Recording whose path was reserved starts and ends being written (R6), on the
/// orchestrator thread; null is <see cref="NoLibraryWrites.Instance"/>.
/// </param>
/// <param name="Info">
/// Called on the orchestrator thread with why a press made no Recording, or what a started one
/// shares that differs from the profile's settings; null ignores it.
/// </param>
/// <param name="Open">
/// Keeps each Recording's path from just after the Library is told it begins until just after it is
/// told how it ended (FR-12), so a restart can find the Recordings a dead engine left open; null
/// keeps nothing.
/// </param>
/// <param name="Restarted">
/// Called on the orchestrator thread when a forced encoder restart (R4) ended a Recording and a new
/// one took over, just after the new one's <paramref name="Started"/>; null ignores it.
/// </param>
public sealed record Recordings(
    OutputPathAllocator Paths,
    Func<string> DefaultOutputFolder,
    Action<RecordingStarted> Started,
    Action<RecordingStopped> Stopped,
    Action<Exception> Failed,
    ILibraryWrites? Library = null,
    Action<string>? Info = null,
    IOpenRecordings? Open = null,
    Action<RecordingRestart>? Restarted = null);

/// <summary>A forced encoder restart's handover (R4): the Recording it stopped and the one that took over, by path.</summary>
public sealed record RecordingRestart(string StoppedPath, string StartedPath);

/// <summary>One audio device of a <see cref="ChainKey"/>: its endpoint, compared ignoring case, and flow.</summary>
public readonly record struct ChainDevice
{
    public ChainDevice(string endpointId, AudioFlow flow)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        EndpointId = endpointId.ToUpperInvariant();
        Flow = flow;
    }

    public string EndpointId { get; }

    public AudioFlow Flow { get; }
}

/// <summary>One Track of a <see cref="ChainKey"/>: its name and the devices it mixes, in order.</summary>
public sealed record ChainTrack(string Name, IReadOnlyList<ChainDevice> Devices)
{
    public bool Equals(ChainTrack? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal) && Devices.SequenceEqual(other.Devices);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Name, StringComparer.Ordinal);
        foreach (var device in Devices)
        {
            hash.Add(device);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// What decides whether a Recording can share a session's encoder chain (epic-recordings): the
/// session's kind and process, its <c>video</c>, and its Tracks' names and devices. Two equal keys
/// encode the same streams.
/// </summary>
public sealed record ChainKey(SessionKind Kind, int ProcessId, VideoSettings Video, IReadOnlyList<ChainTrack> Tracks)
{
    /// <summary>The chain a running session encodes.</summary>
    public static ChainKey Of(SessionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Of(spec.Kind, spec.Target, spec.Snapshot.Video, spec.Audio);
    }

    /// <summary>
    /// The chain a session of <paramref name="kind"/> on <paramref name="target"/> would encode from
    /// <paramref name="snapshot"/>; null when its Track Layout can't be resolved, which matches no chain.
    /// </summary>
    public static ChainKey? Of(SessionKind kind, CaptureTarget target, EffectiveSettings snapshot)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        SessionAudio audio;
        try
        {
            audio = SessionAudio.FromLayout(snapshot.TrackLayout);
        }
        catch (Exception)
        {
            return null;
        }

        return Of(kind, target, snapshot.Video, audio);
    }

    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> encode on one chain (4.9): equal
    /// <see cref="ChainKey"/>s and the same mic mute inputs (<see cref="SessionAudio.GatedMicInputs"/>,
    /// each a Track name and a device endpoint, ignoring case, and flow, in order). A change of
    /// <c>isMic</c> or <c>muteAffected</c> alone so needs another chain too.
    /// </summary>
    public static bool SameChain(SessionSpec a, SessionSpec b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Of(a) == Of(b) && GatedInputs(a.Audio).SequenceEqual(GatedInputs(b.Audio));
    }

    public bool Equals(ChainKey? other) =>
        other is not null
        && Kind == other.Kind
        && ProcessId == other.ProcessId
        && Video == other.Video
        && Tracks.SequenceEqual(other.Tracks);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Kind);
        hash.Add(ProcessId);
        hash.Add(Video);
        foreach (var track in Tracks)
        {
            hash.Add(track);
        }

        return hash.ToHashCode();
    }

    private static IEnumerable<(string Track, ChainDevice Device)> GatedInputs(SessionAudio audio) =>
        audio.GatedMicInputs().Select(input => (input.Track.Name, new ChainDevice(input.Device.EndpointId, input.Device.Flow)));

    private static ChainKey Of(SessionKind kind, CaptureTarget target, VideoSettings video, SessionAudio audio) => new(
        kind,
        target.ProcessId,
        video,
        [.. audio.Tracks.Select(t => new ChainTrack(t.Name, [.. t.DeviceIndexes.Select(i => new ChainDevice(audio.Devices[i].EndpointId, audio.Devices[i].Flow))]))]);
}
