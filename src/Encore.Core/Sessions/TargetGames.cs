using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Library;
using Encore.Core.Output;

namespace Encore.Core.Sessions;

/// <summary>A game proposed as the Target Game: its process, window, Game Profile id and display name.</summary>
public sealed record TargetCandidate(int ProcessId, nint Window, string ProfileId, string Name)
{
    /// <summary>The Target Game as <c>target.changed</c> and the snapshot carry it.</summary>
    public TargetGame ToTargetGame() => new(ProfileId, Name, ProcessId);
}

/// <summary>Game Detection proposes a Target Game (none when it is gone); only the orchestrator commits and publishes it.</summary>
public sealed record ProposeTarget(TargetCandidate? To, TargetChangeReason Reason) : SessionMessage;

/// <summary>The simulated Recording state: shown while no real Recording runs; it never holds the Target.</summary>
public sealed record RecordingRunning(bool Running) : SessionMessage;

/// <summary>The game session that follows the Target Game; each new Target ends it and starts one on the Target's window.</summary>
public sealed record TargetCapture(Func<EffectiveSettings, SessionAudio> Audio, Action<TargetCandidate, Exception> Failed, CaptureWait? Wait = null);

/// <summary>How the game session follows the Target's process, not one window. Orchestrator thread; docs/code-notes.md.</summary>
public sealed record CaptureWait(
    Func<TargetCandidate, CaptureWindow> Find,
    Action<TargetCandidate, string> Waiting,
    Action<TargetCandidate, bool> Ended,
    Func<Exception, bool>? Retries = null)
{
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan DefaultFirstRetry = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan DefaultMaxRetry = TimeSpan.FromSeconds(30);

    /// <summary>How often the Target's windows are checked while none can be captured, and the captured one after that.</summary>
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

/// <summary>How the orchestrator turns a save press into a Clip; every callback runs on the orchestrator thread.</summary>
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
    /// <summary>At most this many Clips are written at once, across sessions.</summary>
    public const int MaxWriters = 2;

    /// <summary>The exit drain: 30 s.</summary>
    public static readonly TimeSpan DefaultExitDrain = TimeSpan.FromSeconds(30);
}

/// <summary>How the orchestrator turns a Record press into a Recording; every callback runs on the orchestrator thread.</summary>
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
    /// <summary>How often a Rewind a full disk stopped checks for room for its ring by default.</summary>
    public static readonly TimeSpan DefaultRingRecheck = TimeSpan.FromSeconds(30);
}

/// <summary>How a lost GPU device is recovered: rebuild at once, retry after each failure, and give up after a limit.</summary>
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

/// <summary>A forced encoder restart's handover: the Recording it stopped and the one that took over, by path.</summary>
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

/// <summary>What decides whether two specs share an encoder chain: kind, process, video, and the Tracks' names and devices.</summary>
public sealed record ChainKey(SessionKind Kind, int ProcessId, VideoSettings Video, IReadOnlyList<ChainTrack> Tracks)
{
    /// <summary>The chain a running session encodes.</summary>
    public static ChainKey Of(SessionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Of(spec.Kind, spec.Target, spec.Snapshot.Video, spec.Audio);
    }

    /// <summary>The chain a session would encode from a snapshot; null when its Track Layout can't be resolved.</summary>
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

    /// <summary>Whether two specs encode on one chain: equal keys and the same mic mute inputs.</summary>
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
