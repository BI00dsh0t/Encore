using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.Core.Feedback;

/// <summary>The words the feedback notices use for each reason.</summary>
internal static class FeedbackText
{
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

    /// <summary>Whether a stop reason is one the engine gives on purpose; any other is a writer's failure.</summary>
    public static bool IsKnownReason(string reason) => reason is RecordingStopReasons.Stopped
        or RecordingStopReasons.GameExit
        or RecordingStopReasons.Ignored
        or RecordingStopReasons.SessionEnded
        or RecordingStopReasons.Shutdown
        or RecordingStopReasons.EncoderRestart
        or RecordingStopReasons.DiskFull
        or RecordingStopReasons.Suspend
        or RecordingStopReasons.MonitorRemoved;

    /// <summary>The words a notice uses for a <c>clip.failed</c> reason.</summary>
    public static string ShortReason(string reason) => reason switch
    {
        ErrorCodes.DiskFull => "disk full",
        ErrorCodes.ClipTooShort => "nothing to save yet",
        ErrorCodes.SaveBusy => "still saving the last one",
        ErrorCodes.AccessDenied => "access denied",
        _ => "save failed",
    };

    /// <summary>The message of the warning that a Recording goes on in a new file.</summary>
    public static string RestartMessage(string stoppedPath, string startedPath) =>
        "The Recording continues in a new file after its video restarted: "
        + $"{Path.GetFileName(stoppedPath)} → {Path.GetFileName(startedPath)}";
}
