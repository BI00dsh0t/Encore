using Encore.App.Engine;
using Encore.App.Settings;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Status;

/// <summary>The wording of the status line: the capture state, the connection's state and the save hint.</summary>
internal static class StatusText
{
    /// <summary>Where the App stands with the engine, with the reason when it has one.</summary>
    public static string Connection(EngineStatus status, string? problem) => status switch
    {
        EngineStatus.Connecting => "Connecting to Encore's background service…",
        EngineStatus.Connected => "Connected to Encore's background service.",
        EngineStatus.Restarting => "Restarting Encore's background service…" + (problem is { } why ? " " + why : ""),
        EngineStatus.NotRunning => "Encore's background service isn't running." + (problem is { } reason ? " " + reason : ""),
        EngineStatus.CrashLoop => problem
            ?? "Encore's background service keeps crashing, so it isn't being restarted. Restart it from the tray menu.",
        _ => "Encore's background service disconnected. Reconnecting…",
    };

    /// <summary>The save hint while Clips can be saved; empty during a Recording, without a working Save Hotkey, or when idle.</summary>
    public static string Hint(StateSnapshot state)
    {
        var capture = state.Capture;
        if (capture.Replay != ReplayState.Buffering
            || capture.Recording == RecordingState.Running
            || capture.MonitorRecording == RecordingState.Running
            || SettingsValues.Global(state.Settings, Keys.Hotkeys).Save is not { } save
            || SaveHotkeyFailed(state.Hotkeys))
        {
            return "";
        }

        var length = HotkeyText.ClipLength(SettingsValues.Global(state.Settings, Keys.BufferWindow));
        return $"· {HotkeyText.Describe(save)} saves the last {length}";
    }

    /// <summary>The dot for a capture state: recording wins over buffering.</summary>
    public static StatusDot Dot(CaptureState capture)
    {
        bool isRecording = capture.Recording == RecordingState.Running || capture.MonitorRecording == RecordingState.Running;
        if (isRecording)
        {
            return StatusDot.Recording;
        }

        return capture.Replay == ReplayState.Buffering ? StatusDot.Buffering : StatusDot.Idle;
    }

    /// <summary>The capture state in words: "Ready to save Clips in Valorant · Recording · Mic muted".</summary>
    public static string Capture(StateSnapshot state)
    {
        var capture = state.Capture;
        List<string> parts =
        [
            capture.Replay switch
            {
                ReplayState.Buffering => state.TargetGame is { } game ? $"Ready to save Clips in {game.Name}" : "Ready to save Clips",
                ReplayState.Draining => "Finishing the save…",
                _ => "Waiting for a game",
            },
        ];
        if (capture.Recording == RecordingState.Running)
        {
            parts.Add("Recording");
        }

        if (capture.MonitorRecording == RecordingState.Running)
        {
            parts.Add("Monitor Recording");
        }

        if (capture.MicMuted)
        {
            parts.Add("Mic muted");
        }

        return string.Join(" · ", parts);
    }

    private static bool SaveHotkeyFailed(HotkeyStatus hotkeys) =>
        hotkeys.Bindings.FirstOrDefault(bound => bound.Action == HotkeyAction.Save)
            is { Registered: false, Code: not (ErrorCodes.HotkeyUnbound or ErrorCodes.HotkeyInactive) };
}
