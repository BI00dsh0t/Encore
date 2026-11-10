using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;

namespace Encore.Core.Feedback;

/// <summary>Turns outcomes (Clip saved or not, Recording events, disk full) into notices and sounds. docs/code-notes.md.</summary>
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
            RaiseInfo(new FeedbackNotice(
                ErrorCodes.ClipSaved,
                WarningSeverity.Info,
                Clips,
                "Clip saved",
                FeedbackSoundNames.ClipSaved));
        });
    }

    /// <summary>A save made no Clip: a <c>clip-failed</c> notice with its short reason.</summary>
    public void ClipNotSaved(ClipFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        UnlessDisposed(() =>
        {
            _clipFailures.Add(Raise(new FeedbackNotice(
                ErrorCodes.ClipFailed,
                WarningSeverity.Error,
                Clips,
                $"Clip not saved: {FeedbackText.ShortReason(failed.Reason)}",
                FeedbackSoundNames.SaveFailed)));
        });
    }

    /// <summary>The capture state changed; nothing is raised, the Recording state is only kept to spot an encoder restart.</summary>
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

    /// <summary>A Recording started: a notice with its sound, and every <c>disk-full</c> notice is withdrawn.</summary>
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
            RaiseStarted("Recording started");
        });
    }

    /// <summary>A Recording stopped: a notice naming the reason; a disk-full or restart stop is silent, as another follows.</summary>
    public void RecordingStopped(RecordingStopped stopped)
    {
        ArgumentNullException.ThrowIfNull(stopped);
        UnlessDisposed(() =>
        {
            // The orchestrator turns the state Off before a stop, unless an encoder restart's replacement follows.
            var silent = stopped.Reason is RecordingStopReasons.DiskFull or RecordingStopReasons.EncoderRestart;
            _replacing = _recording == RecordingState.Running;
            RaiseStopped(stopped.Reason, FeedbackText.StopMessage(stopped.Reason), silent);
        });
    }

    /// <summary>A Monitor Recording started: the same notice and sound as a Recording's, worded for the Monitor Recording.</summary>
    public void MonitorRecordingStarted(RecordingStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        UnlessDisposed(() =>
        {
            WithdrawAll(_diskFulls);
            RaiseStarted("Monitor Recording started");
        });
    }

    /// <summary>A Monitor Recording stopped: the same notice and sound as a Recording's; a writer failure is a warning.</summary>
    public void MonitorRecordingStopped(RecordingStopped stopped)
    {
        ArgumentNullException.ThrowIfNull(stopped);
        UnlessDisposed(() =>
        {
            var silent = stopped.Reason == RecordingStopReasons.DiskFull;
            RaiseStopped(stopped.Reason, FeedbackText.MonitorStopMessage(stopped.Reason), silent);
        });
    }

    /// <summary>A forced encoder restart ended a Recording and started another: one warning names both files.</summary>
    public void RecordingRestarted(RecordingRestart restart)
    {
        ArgumentNullException.ThrowIfNull(restart);
        UnlessDisposed(() =>
        {
            RaiseInfo(new FeedbackNotice(
                ErrorCodes.RecordingRestarted,
                WarningSeverity.Warning,
                Recordings,
                FeedbackText.RestartMessage(restart.StoppedPath, restart.StartedPath),
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

    /// <summary>A full disk stopped a Disk-mode Rewind: a <c>replay-stopped-disk-full</c> warning, with no sound.</summary>
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

    private void RaiseStarted(string message) =>
        RaiseInfo(new FeedbackNotice(
            ErrorCodes.RecordingStarted,
            WarningSeverity.Info,
            Recordings,
            message,
            FeedbackSoundNames.RecordingStarted));

    // A stop the engine made on purpose is information; any other reason is a writer's failure. A silent one plays no sound.
    private void RaiseStopped(string reason, string message, bool silent) =>
        RaiseInfo(new FeedbackNotice(
            ErrorCodes.RecordingStopped,
            FeedbackText.IsKnownReason(reason) ? WarningSeverity.Info : WarningSeverity.Warning,
            Recordings,
            message,
            silent ? null : FeedbackSoundNames.RecordingStopped));

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
