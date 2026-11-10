using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;

namespace Encore.Core.Feedback;

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
/// until the next Recording starts, a Rewind stopped by a full disk (R5) until it restarts,
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
    private const string Replay = "replay";

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
        UnlessDisposed(() =>
        {
            WithdrawAll(_clipFailures);
            RaiseInfo(new FeedbackNotice(ErrorCodes.ClipSaved, WarningSeverity.Info, Clips, "Clip saved", FeedbackSoundNames.ClipSaved));
        });
    }

    /// <summary>A save made no Clip: a <c>clip-failed</c> notice with its short reason.</summary>
    public void ClipNotSaved(ClipFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        UnlessDisposed(() =>
        {
            _clipFailures.Add(Raise(new FeedbackNotice(
                ErrorCodes.ClipFailed, WarningSeverity.Error, Clips, $"Clip not saved: {ShortReason(failed.Reason)}", FeedbackSoundNames.SaveFailed)));
        });
    }

    /// <summary>
    /// The capture state changed. It raises nothing; the router keeps the Recording state only to
    /// tell an encoder restart's replacement from a new Recording.
    /// </summary>
    public void StateChanged(CaptureState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        UnlessDisposed(() =>
        {
            _recording = state.Recording;
            if (_recording != RecordingState.Running)
            {
                _replacing = false;
            }
        });
    }

    /// <summary>
    /// A Recording started (<c>recording.started</c>): a <c>recording-started</c> notice with the
    /// <c>recordingStarted</c> sound, and every <c>disk-full</c> notice is withdrawn. An encoder
    /// restart's replacement (a start after a stop that left the state Running) raises and withdraws
    /// nothing; its <see cref="RecordingRestarted"/> notice follows.
    /// </summary>
    public void RecordingStarted(RecordingStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        UnlessDisposed(() =>
        {
            if (_replacing)
            {
                _replacing = false;
                return;
            }

            WithdrawAll(_diskFulls);
            RaiseInfo(new FeedbackNotice(ErrorCodes.RecordingStarted, WarningSeverity.Info, Recordings, "Recording started", FeedbackSoundNames.RecordingStarted));
        });
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
        UnlessDisposed(() =>
        {
            // The orchestrator turns the state Off before a stop unless an encoder restart's replacement
            // follows, whatever the stop's reason (the old writer may still fail or fill the disk).
            var silent = stopped.Reason is RecordingStopReasons.DiskFull or RecordingStopReasons.EncoderRestart;
            _replacing = _recording == RecordingState.Running;
            RaiseInfo(new FeedbackNotice(
                ErrorCodes.RecordingStopped,
                IsKnownReason(stopped.Reason) ? WarningSeverity.Info : WarningSeverity.Warning,
                Recordings,
                StopMessage(stopped.Reason),
                silent ? null : FeedbackSoundNames.RecordingStopped));
        });
    }

    /// <summary>
    /// A Monitor Recording started (6.9): the same <c>recording-started</c> notice and sound as a
    /// Recording's, worded for the Monitor Recording, and every <c>disk-full</c> notice is withdrawn.
    /// It never touches the game Recording's restart handover.
    /// </summary>
    public void MonitorRecordingStarted(RecordingStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        UnlessDisposed(() =>
        {
            WithdrawAll(_diskFulls);
            RaiseInfo(new FeedbackNotice(ErrorCodes.RecordingStarted, WarningSeverity.Info, Recordings, "Monitor Recording started", FeedbackSoundNames.RecordingStarted));
        });
    }

    /// <summary>
    /// A Monitor Recording stopped (6.9): the same <c>recording-stopped</c> notice and sound as a
    /// Recording's (<see cref="MonitorStopMessage"/>), a disk-full stop silent; a writer failure is a
    /// warning. It never touches the game Recording's restart handover.
    /// </summary>
    public void MonitorRecordingStopped(RecordingStopped stopped)
    {
        ArgumentNullException.ThrowIfNull(stopped);
        UnlessDisposed(() =>
        {
            RaiseInfo(new FeedbackNotice(
                ErrorCodes.RecordingStopped,
                IsKnownReason(stopped.Reason) ? WarningSeverity.Info : WarningSeverity.Warning,
                Recordings,
                MonitorStopMessage(stopped.Reason),
                stopped.Reason == RecordingStopReasons.DiskFull ? null : FeedbackSoundNames.RecordingStopped));
        });
    }

    /// <summary>The message a Monitor Recording's <c>recording-stopped</c> notice uses for <paramref name="reason"/>.</summary>
    public static string MonitorStopMessage(string reason) => reason switch
    {
        RecordingStopReasons.Stopped => "Monitor Recording stopped",
        RecordingStopReasons.SessionEnded => "Monitor Recording stopped: the monitor capture ended",
        RecordingStopReasons.Shutdown => "Monitor Recording stopped: Encore shut down",
        RecordingStopReasons.DiskFull => "Monitor Recording stopped: the disk is full",
        RecordingStopReasons.Suspend => "Monitor Recording stopped: the PC went to sleep",
        RecordingStopReasons.MonitorRemoved => "Monitor Recording stopped: the monitor was removed",
        _ when IsKnownReason(reason) => "Monitor Recording stopped",
        _ => $"Monitor Recording stopped: the write failed ({reason})",
    };

    /// <summary>The message a <c>recording-stopped</c> notice uses for <paramref name="reason"/>.</summary>
    public static string StopMessage(string reason) => reason switch
    {
        RecordingStopReasons.Stopped => "Recording stopped",
        RecordingStopReasons.GameExit => "Recording stopped: the game exited",
        RecordingStopReasons.Ignored => "Recording stopped: capture was switched off for the game",
        RecordingStopReasons.SessionEnded => "Recording stopped: the game stopped being captured",
        RecordingStopReasons.Shutdown => "Recording stopped: Encore shut down",
        RecordingStopReasons.EncoderRestart => "Recording stopped: its video restarted",
        RecordingStopReasons.DiskFull => "Recording stopped: the disk is full",
        RecordingStopReasons.Suspend => "Recording stopped: the PC went to sleep",
        _ => $"Recording stopped: the write failed ({reason})",
    };

    private static bool IsKnownReason(string reason) => reason is RecordingStopReasons.Stopped
        or RecordingStopReasons.GameExit
        or RecordingStopReasons.Ignored
        or RecordingStopReasons.SessionEnded
        or RecordingStopReasons.Shutdown
        or RecordingStopReasons.EncoderRestart
        or RecordingStopReasons.DiskFull
        or RecordingStopReasons.Suspend
        or RecordingStopReasons.MonitorRemoved;

    /// <summary>
    /// A forced encoder restart ended a Recording and started another (R4): a
    /// <c>recording-restarted</c> warning naming both files, with the <c>recordingStarted</c> sound,
    /// withdrawn after <see cref="InfoLifetime"/>. The stop before it is silent and its replacement
    /// raises no started notice, so this notice plays the only sound.
    /// </summary>
    public void RecordingRestarted(RecordingRestart restart)
    {
        ArgumentNullException.ThrowIfNull(restart);
        UnlessDisposed(() =>
        {
            RaiseInfo(new FeedbackNotice(
                ErrorCodes.RecordingRestarted,
                WarningSeverity.Warning,
                Recordings,
                $"The Recording continues in a new file after its video restarted: {Path.GetFileName(restart.StoppedPath)} → {Path.GetFileName(restart.StartedPath)}",
                FeedbackSoundNames.RecordingStarted));
        });
    }

    /// <summary>The volume is full: a <c>disk-full</c> notice.</summary>
    public void DiskFull()
    {
        UnlessDisposed(() =>
        {
            _diskFulls.Add(Raise(new FeedbackNotice(
                ErrorCodes.DiskFull, WarningSeverity.Error, Recordings, "The disk is full", FeedbackSoundNames.DiskFull)));
        });
    }

    /// <summary>A full disk stopped a Disk-mode Rewind (R5): a <c>replay-stopped-disk-full</c> warning, with no sound.</summary>
    public void ReplayStoppedDiskFull()
    {
        UnlessDisposed(() =>
        {
            // The disk-full notice already played its sound.
            _replayStops.Add(notices.Raise(new FeedbackNotice(
                ErrorCodes.ReplayStoppedDiskFull,
                WarningSeverity.Warning,
                Replay,
                "Clips stopped: the cache folder's drive is full. They start again once there is room.",
                null)));
        });
    }

    /// <summary>The wait after a full disk ended: every <c>replay-stopped-disk-full</c> notice is withdrawn.</summary>
    public void ReplayResumed()
    {
        UnlessDisposed(() =>
        {
            WithdrawAll(_replayStops);
        });
    }

    /// <summary>The words a notice uses for a <c>clip.failed</c> reason.</summary>
    public static string ShortReason(string reason) => reason switch
    {
        ErrorCodes.DiskFull => "disk full",
        ErrorCodes.ClipTooShort => "nothing to save yet",
        ErrorCodes.SaveBusy => "still saving the last one",
        ErrorCodes.AccessDenied => "access denied",
        _ => "save failed",
    };

    /// <summary>Stops every pending dismissal; later outcomes raise nothing.</summary>
    public void Dispose()
    {
        UnlessDisposed(() =>
        {
            _disposed = true;
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        });
    }

    /// <summary>Runs <paramref name="action"/> under the lock, unless the router is disposed.</summary>
    private void UnlessDisposed(Action action)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                action();
            }
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
