using ReplayBuffer.Contracts;
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
/// <param name="Failed">
/// Called on the orchestrator thread when a session on the Target couldn't be started or retargeted;
/// not for a failure <paramref name="Wait"/> retries.
/// </param>
/// <param name="Wait">
/// How a Target whose window can't be captured yet is waited for and retried; null starts each
/// session at once and treats a failed one as final for the Target.
/// </param>
public sealed record TargetCapture(Func<EffectiveSettings, SessionAudio> Audio, Action<TargetCandidate, Exception> Failed, CaptureWait? Wait = null);

/// <summary>
/// How the game session follows the Target Game's process rather than one window. A game still
/// starting can show a placeholder window (156×27 for Rocket League) that WGC never delivers a frame
/// from, then swap it for a splash and its real window. So no game session starts until
/// <paramref name="Find"/> names a window of the process that can be captured: it is polled every
/// <see cref="Poll"/>. A session on the Target that fails with a failure <see cref="Retries"/>
/// accepts, or whose window closes before its first frame, is started again (after a backoff for a
/// failure, from <see cref="FirstRetry"/> doubling up to <see cref="MaxRetry"/>) while the Target
/// stays the same process. Once a session has its first frame, its window is checked every
/// <see cref="Poll"/>: when it closes or can no longer be captured, the session is retargeted to the
/// process's window <paramref name="Find"/> names, so its Replay Buffer is kept; while there is none
/// it waits, its last frame repeated. A run of waits is reported once, by <paramref name="Waiting"/>,
/// and its end once, by <paramref name="Ended"/>. All calls are on the orchestrator thread.
/// </summary>
/// <param name="Find">
/// The window of the Target's process to capture: the given window when it can be captured, else the
/// process's largest window that can; <see cref="CaptureWindow.None"/>, with why, when none can.
/// </param>
/// <param name="Waiting">The first wait or retry of a run, with its reason.</param>
/// <param name="Ended">The run is over: true once the Target is captured again, false when the Target changed or went.</param>
/// <param name="Retries">Which failures of a session on the Target are retried; null is <see cref="ErrorCodes.CaptureFailed"/> only.</param>
public sealed record CaptureWait(
    Func<TargetCandidate, CaptureWindow> Find,
    Action<TargetCandidate, string> Waiting,
    Action<TargetCandidate, bool> Ended,
    Func<Exception, bool>? Retries = null)
{
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan DefaultFirstRetry = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan DefaultMaxRetry = TimeSpan.FromSeconds(30);

    /// <summary>How often the Target's windows are checked while none can be captured, and the captured one while a session runs.</summary>
    public TimeSpan Poll { get; init; } = DefaultPoll;

    /// <summary>The wait before the first retry of a failed session or retarget.</summary>
    public TimeSpan FirstRetry { get; init; } = DefaultFirstRetry;

    /// <summary>The longest wait between retries.</summary>
    public TimeSpan MaxRetry { get; init; } = DefaultMaxRetry;

    /// <summary>Whether <paramref name="failure"/> of a session on the Target is retried.</summary>
    public bool Retry(Exception failure) =>
        Retries?.Invoke(failure) ?? failure is ErrorCodeException { Code: ErrorCodes.CaptureFailed };

    /// <summary>The wait before retry <paramref name="failures"/> (1 for the first) of a run.</summary>
    public TimeSpan Backoff(int failures)
    {
        var wait = FirstRetry;
        for (var i = 1; i < failures && wait < MaxRetry; i++)
        {
            wait += wait;
        }

        return wait < MaxRetry ? wait : MaxRetry;
    }
}

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
/// <param name="DiskFull">
/// Called on the orchestrator thread when a Recording stopped because its disk filled (R5), just
/// after its <paramref name="Stopped"/>; null ignores it.
/// </param>
/// <param name="RingFits">
/// Whether a Disk-mode ring of the given snapshot fits its drive now, asked on the orchestrator
/// thread at each recheck after a full disk stopped the Replay Buffer; null counts as fits.
/// </param>
/// <param name="ReplayDiskFull">
/// Called on the orchestrator thread with true when a full disk stopped a Disk-mode Replay Buffer
/// (R5), and with false when that wait ends, whether it restarted or not; null ignores it.
/// </param>
/// <param name="RingRecheck">
/// How often a Replay Buffer a full disk stopped checks whether its ring fits again; null is
/// <see cref="DefaultRingRecheck"/>.
/// </param>
/// <param name="MonitorStarted">
/// Called on the orchestrator thread once a Monitor Recording has started (6.9); null calls
/// <paramref name="Started"/> instead.
/// </param>
/// <param name="MonitorStopped">
/// Called on the orchestrator thread once a Monitor Recording's writer has finished; null calls
/// <paramref name="Stopped"/> instead.
/// </param>
/// <param name="Warn">
/// Called on the orchestrator thread with a warning code (<see cref="Contracts.ErrorCodes"/>) and
/// its message when a press was refused for a reason the user should see, such as
/// <see cref="Contracts.ErrorCodes.EncoderBudget"/>; null ignores it.
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
    Action<RecordingRestart>? Restarted = null,
    Action? DiskFull = null,
    Func<EffectiveSettings, bool>? RingFits = null,
    Action<bool>? ReplayDiskFull = null,
    TimeSpan? RingRecheck = null,
    Action<RecordingStarted>? MonitorStarted = null,
    Action<RecordingStopped>? MonitorStopped = null,
    Action<string, string>? Warn = null)
{
    /// <summary>How often a Replay Buffer a full disk stopped checks for room for its ring by default.</summary>
    public static readonly TimeSpan DefaultRingRecheck = TimeSpan.FromSeconds(30);
}

/// <summary>
/// How the orchestrator recovers a session whose GPU device was lost (R3, PLAN.md T-01, AD-19): it
/// asks the session to rebuild at once, then every <paramref name="RetryEvery"/> after a failed
/// attempt, and gives up <paramref name="GiveUpAfter"/> after the loss, ending the session.
/// </summary>
/// <param name="Reset">
/// Called on the orchestrator thread with a message when a session's device is first lost; null
/// ignores it.
/// </param>
/// <param name="Failed">
/// Called on the orchestrator thread with a message when the orchestrator gave up and ended the
/// session; null ignores it.
/// </param>
/// <param name="Recovered">
/// Called on the orchestrator thread with the time since the loss when a session rebuilt; null
/// ignores it.
/// </param>
/// <param name="RetryEvery">How long after a failed attempt the next starts; null is <see cref="DefaultRetryEvery"/>.</param>
/// <param name="GiveUpAfter">How long after the loss a failed attempt ends the session; null is <see cref="DefaultGiveUpAfter"/>.</param>
public sealed record DeviceRecovery(
    Action<string>? Reset = null,
    Action<string>? Failed = null,
    Action<TimeSpan>? Recovered = null,
    TimeSpan? RetryEvery = null,
    TimeSpan? GiveUpAfter = null)
{
    /// <summary>The wait between recovery attempts by default: 2 s.</summary>
    public static readonly TimeSpan DefaultRetryEvery = TimeSpan.FromSeconds(2);

    /// <summary>How long after a loss recovery is given up by default: 60 s.</summary>
    public static readonly TimeSpan DefaultGiveUpAfter = TimeSpan.FromSeconds(60);
}

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

/// <summary>The window <see cref="CaptureWait.Find"/> names: <see cref="Window"/>, or none, with <see cref="Why"/>.</summary>
public readonly record struct CaptureWindow(nint Window, string? Why)
{
    public bool Found => Window != 0;

    public static CaptureWindow Of(nint window) => new(window, null);

    public static CaptureWindow None(string why) => new(0, why);
}
