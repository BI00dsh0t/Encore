using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Feedback;
using Encore.Core.Sessions;
using Encore.Engine.Audio;
using Encore.Engine.Hotkeys;
using Encore.Engine.Ipc;

namespace Encore.Engine.Hosting;

/// <summary>Reports what the capture sessions do: an event for the App, a log line, and a warning or feedback notice.</summary>
internal sealed class CaptureOutcomes(
    EventHub hub,
    Warnings warnings,
    FeedbackRouter feedback,
    TrackMeters meters,
    FeedbackDeviceCheck feedbackCheck,
    ILoggerFactory loggers)
{
    private readonly ILogger _clipLog = loggers.CreateLogger("Clips");
    private readonly ILogger _recordingLog = loggers.CreateLogger("Recordings");
    private readonly ILogger _hotkeyLog = loggers.CreateLogger("Hotkeys");

    public void StateChanged(CaptureState state)
    {
        hub.Publish(CaptureMessages.StateChanged, state);
        meters.SetMicMuted(state.MicMuted);
        feedback.StateChanged(state);
    }

    public void HotkeyPressed(HotkeyPress press)
    {
        hub.Publish(HotkeyMessages.Pressed, press);
        HotkeyService.LogPress(_hotkeyLog, press);
    }

    public void TargetChanged(TargetChanged changed)
    {
        hub.Publish(GameMessages.TargetChanged, changed);
        feedbackCheck.Check();
    }

    public void ClipSaved(ClipSaved saved)
    {
        hub.Publish(CaptureMessages.Saved, saved);
        _clipLog.LogInformation("Clip saved to {Path}", saved.Path);
        feedback.ClipSaved(saved);
    }

    public void ClipNotSaved(ClipFailed failed)
    {
        hub.Publish(CaptureMessages.Failed, failed);
        _clipLog.LogInformation("The save made no Clip: {Reason}", failed.Reason);
        feedback.ClipNotSaved(failed);
    }

    public void Saving(ClipSaving saving)
    {
        hub.Publish(CaptureMessages.Saving, saving);
        _clipLog.LogDebug("Saving a Clip");
    }

    public void SaveFailed(Exception error) => _clipLog.LogWarning("The save failed: {Message}", error.Message);

    public void SaveIgnored(TimeSpan since) => _clipLog.LogInformation(
        "The save press came {Seconds:0.0} s after the last accepted one, inside the Save Cooldown; it was ignored",
        since.TotalSeconds);

    public void RecordingStarted(RecordingStarted started)
    {
        hub.Publish(RecordingMessages.Started, started);
        _recordingLog.LogInformation("Recording to {Path}", started.Path);
        feedback.RecordingStarted(started);
    }

    public void RecordingStopped(RecordingStopped stopped)
    {
        hub.Publish(RecordingMessages.Stopped, stopped);
        _recordingLog.LogInformation("Recording stopped ({Reason}): {Path}", stopped.Reason, stopped.Path);
        feedback.RecordingStopped(stopped);
    }

    public void MonitorRecordingStarted(RecordingStarted started)
    {
        hub.Publish(RecordingMessages.Started, started);
        _recordingLog.LogInformation("Monitor Recording to {Path}", started.Path);
        feedback.MonitorRecordingStarted(started);
    }

    public void MonitorRecordingStopped(RecordingStopped stopped)
    {
        hub.Publish(RecordingMessages.Stopped, stopped);
        _recordingLog.LogInformation("Monitor Recording stopped ({Reason}): {Path}", stopped.Reason, stopped.Path);
        feedback.MonitorRecordingStopped(stopped);
    }

    public void RecordingFailed(Exception error) => _recordingLog.LogWarning("The Recording failed: {Message}", error.Message);

    public void RecordingInfo(string message) => _recordingLog.LogInformation("{Message}", message);

    public void RecordingRestarted(RecordingRestart restart)
    {
        _recordingLog.LogWarning(
            "An encoder restart ended the Recording {Stopped}; it continues in {Started}",
            restart.StoppedPath,
            restart.StartedPath);
        feedback.RecordingRestarted(restart);
    }

    public void DiskFull()
    {
        _recordingLog.LogWarning("The Recording's disk is full; the Recording stopped");
        feedback.DiskFull();
    }

    public void ReplayDiskFull(bool stopped)
    {
        if (stopped)
        {
            _recordingLog.LogWarning("The disk is full: the Disk-mode Rewind stopped until its ring fits again");
            feedback.ReplayStoppedDiskFull();
        }
        else
        {
            _recordingLog.LogInformation("The wait for room for Rewind's ring ended");
            feedback.ReplayResumed();
        }
    }

    public void RecordingWarning(string code, string message)
    {
        _recordingLog.LogWarning("{Code}: {Message}", code, message);

        // A skipped or arriving screen is a notice in the Main App, not an Overlay message.
        var overlay = code is not (ErrorCodes.MonitorMissing or ErrorCodes.MonitorAddedNextRecording);
        warnings.Raise(code, WarningSeverity.Warning, "recordings", message, overlay);
    }

    public void ResetGap(string message) =>
        warnings.Raise(ErrorCodes.ResetGap, WarningSeverity.Warning, "replay", message, overlay: false);

    public void CaptureFallback(string message) =>
        warnings.Raise(ErrorCodes.MonitorCaptureFallback, WarningSeverity.Warning, "recordings", message, overlay: false);

    public void CaptureWarning(string code, string message) =>
        warnings.Raise(code, WarningSeverity.Warning, "recordings", message, overlay: false);
}
