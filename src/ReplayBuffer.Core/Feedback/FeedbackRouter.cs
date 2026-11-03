using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Core.Feedback;

/// <summary>
/// A user-facing notice of an outcome: raised as a <c>warning.raised</c> that the Overlay shows and
/// the user may dismiss (AD-3).
/// </summary>
/// <param name="Code">One of <see cref="ErrorCodes"/>.</param>
/// <param name="Source">The engine area it is about, such as <c>clips</c> or <c>recordings</c>.</param>
/// <param name="Sound">The <c>feedbackSounds</c> name it plays.</param>
public sealed record FeedbackNotice(string Code, WarningSeverity Severity, string Source, string Message, string Sound);

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
/// (from <see cref="CaptureState.Recording"/> transitions) and disk full. Info notices are withdrawn
/// after <see cref="InfoLifetime"/>; a Clip not saved stays until the next saved Clip, a disk full
/// until the next Recording starts, or until the user dismisses either. Thread-safe.
/// </summary>
public sealed class FeedbackRouter(IFeedbackNotices notices, TimeProvider time) : IDisposable
{
    /// <summary>How long an info notice stays before the engine dismisses it.</summary>
    public static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(10);

    private const string Clips = "clips";
    private const string Recordings = "recordings";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ITimer> _timers = new(StringComparer.Ordinal);
    private readonly List<string> _clipFailures = [];
    private readonly List<string> _diskFulls = [];
    private RecordingState _recording = RecordingState.Off;
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

            _clipFailures.Add(notices.Raise(new FeedbackNotice(
                ErrorCodes.ClipFailed, WarningSeverity.Error, Clips, $"Clip not saved: {ShortReason(failed.Reason)}", "saveFailed")));
        }
    }

    /// <summary>
    /// The capture state changed: a Recording turning on raises <c>recording-started</c> and
    /// withdraws every <c>disk-full</c> notice; turning off raises <c>recording-stopped</c>.
    /// </summary>
    public void StateChanged(CaptureState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (_disposed || state.Recording == _recording)
            {
                return;
            }

            _recording = state.Recording;
            if (_recording == RecordingState.Running)
            {
                WithdrawAll(_diskFulls);
                RaiseInfo(new FeedbackNotice(ErrorCodes.RecordingStarted, WarningSeverity.Info, Recordings, "Recording started", "recordingStarted"));
            }
            else
            {
                RaiseInfo(new FeedbackNotice(ErrorCodes.RecordingStopped, WarningSeverity.Info, Recordings, "Recording stopped", "recordingStopped"));
            }
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

            _diskFulls.Add(notices.Raise(new FeedbackNotice(
                ErrorCodes.DiskFull, WarningSeverity.Error, Recordings, "The disk is full", "diskFull")));
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

    private void RaiseInfo(FeedbackNotice notice)
    {
        var id = notices.Raise(notice);
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
