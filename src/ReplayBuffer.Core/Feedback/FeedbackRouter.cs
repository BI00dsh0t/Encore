using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Feedback;

/// <summary>
/// A user-facing notice of an outcome: raised as a <c>warning.raised</c> that the Overlay shows and
/// the user may dismiss (AD-3).
/// </summary>
/// <param name="Code">One of <see cref="ErrorCodes"/>.</param>
/// <param name="Source">The engine area it is about, such as <c>clips</c> or <c>recordings</c>.</param>
/// <param name="Sound">The <c>feedbackSounds</c> name it plays, or <c>null</c> once the router has found it switched off.</param>
public sealed record FeedbackNotice(string Code, WarningSeverity Severity, string Source, string Message, string? Sound);

/// <summary>Where the <see cref="FeedbackRouter"/> raises its notices; the engine's warnings.</summary>
public interface IFeedbackNotices
{
    /// <summary>Raises <paramref name="notice"/>, overlay and dismissable, and returns its id.</summary>
    string Raise(FeedbackNotice notice);

    /// <summary>Removes the notice <paramref name="id"/> if it is still active; nothing happens if it isn't.</summary>
    void Withdraw(string id);
}

/// <summary>
/// Turns outcomes into notices (FR-6): Clip saved, Clip not saved, Recording started and stopped
/// (from the real <c>recording.started</c> and <c>recording.stopped</c> events, the stop naming its
/// reason), Recording restarted and disk full. Info
/// notices, and the restart's, are withdrawn after <see cref="InfoLifetime"/>; a Clip not saved
/// stays until the next saved Clip, a disk full
/// until the next Recording starts, a Replay Buffer stopped by a full disk (R5) until it restarts,
/// or until the user dismisses any of them. With a
/// <see cref="FeedbackSoundPlayer"/>, each notice's sound is queued as it is raised; a sound that is
/// switched off isn't played, and its notice carries no <see cref="FeedbackNotice.Sound"/>. Thread-safe.
/// </summary>
public sealed class FeedbackRouter(IFeedbackNotices notices, TimeProvider time, FeedbackSoundPlayer? sounds = null) : IDisposable
{
    /// <summary>How long an info notice stays before the engine dismisses it.</summary>
    public static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(10);

    private const string Clips = "clips";
    private const string Recordings = "recordings";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ITimer> _timers = new(StringComparer.Ordinal);
    private readonly List<string> _clipFailures = [];
    private readonly List<string> _diskFulls = [];
    private readonly List<string> _replayStops = [];
    private RecordingState _recording = RecordingState.Off;
    private bool _replacing;
    private bool _disposed;

    /// <summary>A Clip was saved: a <c>clip-saved</c> notice, and every <c>clip-failed</c> one is withdrawn.</summary>
    public void ClipSaved(ClipSaved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            WithdrawAll(_clipFailures);
            RaiseInfo(new FeedbackNotice(ErrorCodes.ClipSaved, WarningSeverity.Info, Clips, "Clip saved", "clipSaved"));
        }
    }

    /// <summary>A save made no Clip: a <c>clip-failed</c> notice with its short reason.</summary>
    public void ClipNotSaved(ClipFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _clipFailures.Add(Raise(new FeedbackNotice(
                ErrorCodes.ClipFailed, WarningSeverity.Error, Clips, $"Clip not saved: {ShortReason(failed.Reason)}", "saveFailed")));
        }
    }

    /// <summary>
    /// The capture state changed. It raises nothing; the router keeps the Recording state only to
    /// tell an encoder restart's replacement from a new Recording.
    /// </summary>
    public void StateChanged(CaptureState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _recording = state.Recording;
            if (_recording != RecordingState.Running)
            {
                _replacing = false;
            }
        }
    }

    /// <summary>
    /// A Recording started (<c>recording.started</c>): a <c>recording-started</c> notice with the
    /// <c>recordingStarted</c> sound, and every <c>disk-full</c> notice is withdrawn. An encoder
    /// restart's replacement raises nothing; its <see cref="RecordingRestarted"/> notice follows.
    /// </summary>
    public void RecordingStarted(RecordingStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            WithdrawAll(_diskFulls);
            if (_replacing)
            {
                _replacing = false;
                return;
            }

            RaiseInfo(new FeedbackNotice(ErrorCodes.RecordingStarted, WarningSeverity.Info, Recordings, "Recording started", "recordingStarted"));
        }
    }

    /// <summary>
    /// A Recording stopped (<c>recording.stopped</c>): a <c>recording-stopped</c> notice whose message
    /// names the reason (<see cref="StopMessage"/>), with the <c>recordingStopped</c> sound. A disk-full
    /// or encoder-restart stop is silent, as the <see cref="DiskFull"/> or
    /// <see cref="RecordingRestarted"/> notice that follows plays its own sound; a writer failure is a warning.
    /// </summary>
    public void RecordingStopped(RecordingStopped stopped)
    {
        ArgumentNullException.ThrowIfNull(stopped);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // The orchestrator turns the state Off before an encoder-restart stop only when no replacement follows.
            var silent = stopped.Reason is RecordingStopReasons.DiskFull or RecordingStopReasons.EncoderRestart;
            _replacing = stopped.Reason == RecordingStopReasons.EncoderRestart && _recording == RecordingState.Running;
            RaiseInfo(new FeedbackNotice(
                ErrorCodes.RecordingStopped,
                IsKnownReason(stopped.Reason) ? WarningSeverity.Info : WarningSeverity.Warning,
                Recordings,
                StopMessage(stopped.Reason),
                silent ? null : "recordingStopped"));
        }
    }

    /// <summary>The message a <c>recording-stopped</c> notice uses for <paramref name="reason"/>.</summary>
    public static string StopMessage(string reason) => reason switch
    {
        RecordingStopReasons.Stopped => "Recording stopped",
        RecordingStopReasons.GameExit => "Recording stopped: the game exited",
        RecordingStopReasons.Ignored => "Recording stopped: the game was set Ignored",
        RecordingStopReasons.SessionEnded => "Recording stopped: the game session ended",
        RecordingStopReasons.Shutdown => "Recording stopped: ReplayBuffer shut down",
        RecordingStopReasons.EncoderRestart => "Recording stopped: encoder restart",
        RecordingStopReasons.DiskFull => "Recording stopped: the disk is full",
        _ => $"Recording stopped: the write failed ({reason})",
    };

    private static bool IsKnownReason(string reason) => reason is RecordingStopReasons.Stopped
        or RecordingStopReasons.GameExit
        or RecordingStopReasons.Ignored
        or RecordingStopReasons.SessionEnded
        or RecordingStopReasons.Shutdown
        or RecordingStopReasons.EncoderRestart
        or RecordingStopReasons.DiskFull;

    /// <summary>
    /// A forced encoder restart ended a Recording and started another (R4): a
    /// <c>recording-restarted</c> warning naming both files, with the <c>recordingStarted</c> sound,
    /// withdrawn after <see cref="InfoLifetime"/>. The stop before it is silent and its replacement
    /// raises no started notice, so this notice plays the only sound.
    /// </summary>
    public void RecordingRestarted(RecordingRestart restart)
    {
        ArgumentNullException.ThrowIfNull(restart);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            RaiseInfo(new FeedbackNotice(
                ErrorCodes.RecordingRestarted,
                WarningSeverity.Warning,
                Recordings,
                $"Recording restarted after an encoder restart: {Path.GetFileName(restart.StoppedPath)} → {Path.GetFileName(restart.StartedPath)}",
                "recordingStarted"));
        }
    }

    /// <summary>The volume is full: a <c>disk-full</c> notice.</summary>
    public void DiskFull()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _diskFulls.Add(Raise(new FeedbackNotice(
                ErrorCodes.DiskFull, WarningSeverity.Error, Recordings, "The disk is full", "diskFull")));
        }
    }

    /// <summary>A full disk stopped a Disk-mode Replay Buffer (R5): a <c>replay-stopped-disk-full</c> warning, with no sound.</summary>
    public void ReplayStoppedDiskFull()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // The disk-full notice already played its sound.
            _replayStops.Add(notices.Raise(new FeedbackNotice(
                ErrorCodes.ReplayStoppedDiskFull,
                WarningSeverity.Warning,
                "replay",
                "The Replay Buffer stopped: its disk is full. It restarts once there is room.",
                null)));
        }
    }

    /// <summary>The wait after a full disk ended: every <c>replay-stopped-disk-full</c> notice is withdrawn.</summary>
    public void ReplayResumed()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            WithdrawAll(_replayStops);
        }
    }

    /// <summary>The words a notice uses for a <c>clip.failed</c> reason.</summary>
    public static string ShortReason(string reason) => reason switch
    {
        ErrorCodes.DiskFull => "disk full",
        ErrorCodes.ClipTooShort => "clip too short",
        ErrorCodes.SaveBusy => "busy",
        ErrorCodes.AccessDenied => "access denied",
        _ => "save failed",
    };

    /// <summary>Stops every pending dismissal; later outcomes raise nothing.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    /// <summary>Queues the notice's sound (which never blocks, so it is safe under the lock) and raises it.</summary>
    private string Raise(FeedbackNotice notice)
    {
        if (sounds is not null && !sounds.Play(notice.Sound))
        {
            notice = notice with { Sound = null };
        }

        return notices.Raise(notice);
    }

    private void RaiseInfo(FeedbackNotice notice)
    {
        var id = Raise(notice);
        _timers[id] = time.CreateTimer(Expire, id, InfoLifetime, Timeout.InfiniteTimeSpan);
    }

    private void Expire(object? state)
    {
        var id = (string)state!;
        lock (_lock)
        {
            if (!_timers.Remove(id, out var timer))
            {
                return;
            }

            timer.Dispose();
            notices.Withdraw(id);
        }
    }

    private void WithdrawAll(List<string> ids)
    {
        foreach (var id in ids)
        {
            notices.Withdraw(id);
        }

        ids.Clear();
    }
}
