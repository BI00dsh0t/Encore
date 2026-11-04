using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App.Status;

/// <summary>
/// The status line above every page of the Main App (R5): one line of capture state, then the active
/// warnings newest first. Everything shown comes from the session (AD-3); a row goes only when the
/// snapshot drops its warning.
/// </summary>
internal sealed class StatusLine(IEngineSession engine) : Observable
{
    private string _state = "";
    private IReadOnlyList<StatusWarning> _warnings = [];
    private IReadOnlyList<Warning> _rendered = [];
    private FrameworkElement? _view;

    /// <summary>The capture state, or the connection's when the engine isn't connected.</summary>
    public string State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>The active warnings, newest first.</summary>
    public IReadOnlyList<StatusWarning> Warnings
    {
        get => _warnings;
        private set => Set(ref _warnings, value);
    }

    public FrameworkElement View => _view ??= new StatusLineView { DataContext = this };

    /// <summary>Renders the session. Runs on the UI thread.</summary>
    public void Render(EngineStatus status, string? problem, StateSnapshot? state)
    {
        var connected = EngineSessions.ConnectedState(status, state);
        State = connected is not null
            ? CaptureText(connected)
            : ConnectionText(status, problem);

        // Like the Overlay: a session that isn't connected has no warnings to show or dismiss.
        var warnings = connected?.Warnings ?? [];
        if (warnings.SequenceEqual(_rendered))
        {
            return;
        }

        _rendered = warnings;
        Warnings = [.. warnings.OrderByDescending(w => w.At).Select(w => new StatusWarning(w, Dismiss))];
    }

    private static string ConnectionText(EngineStatus status, string? problem) => status switch
    {
        EngineStatus.Connecting => "Connecting to the engine…",
        EngineStatus.Connected => "Connected to the engine.",
        EngineStatus.Restarting => "Restarting the engine…" + (problem is { } why ? " " + why : ""),
        EngineStatus.NotRunning => "The engine isn't running." + (problem is { } reason ? " " + reason : ""),
        EngineStatus.CrashLoop => problem ?? "The engine keeps crashing, so it isn't being restarted. Restart it from the tray menu.",
        _ => "The engine disconnected. Reconnecting…",
    };

    private static string CaptureText(StateSnapshot state)
    {
        var capture = state.Capture;
        List<string> parts =
        [
            capture.Replay switch
            {
                ReplayState.Buffering => state.TargetGame is { } game ? $"Buffering on {game.Name}" : "Buffering",
                ReplayState.Draining => "Draining",
                _ => "Replay Buffer off",
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

    private async Task Dismiss(string id)
    {
        try
        {
            await engine.SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(id));
        }
        catch (ErrorCodeException)
        {
            // Already gone, or no engine; the snapshot carries the truth.
        }
    }
}

/// <summary>One warning row of the status line.</summary>
internal sealed class StatusWarning
{
    public StatusWarning(Warning warning, Func<string, Task> dismiss)
    {
        Id = warning.Id;
        Time = warning.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Severity = warning.Severity.ToString();
        Message = warning.Message;
        CanDismiss = warning.Dismissable;
        DismissCommand = new RelayCommand(() => _ = dismiss(Id));
    }

    public string Id { get; }

    /// <summary>When it was raised, local time, as HH:mm:ss.</summary>
    public string Time { get; }

    public string Severity { get; }

    public string Message { get; }

    public bool CanDismiss { get; }

    /// <summary>Sends <c>warning.dismiss</c>; the row stays until the snapshot drops it.</summary>
    public ICommand DismissCommand { get; }
}
