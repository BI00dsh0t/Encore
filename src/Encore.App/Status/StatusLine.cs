using System.Windows;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.App.Status;

/// <summary>Session status for the Main App; the top card shows only active errors and service failures.</summary>
internal sealed class StatusLine(IEngineSession engine) : Observable
{
    private string _state = "";
    private string _hint = "";
    private string _connectionError = "";
    private StatusDot _dot;
    private IReadOnlyList<StatusWarning> _warnings = [];
    private IReadOnlyList<Warning> _rendered = [];
    private FrameworkElement? _view;

    /// <summary>The capture state, or the connection's when the engine isn't connected.</summary>
    public string State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>The save hint shown while capturing; empty during a Recording, without a working Save Hotkey, or when idle.</summary>
    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    /// <summary>The status header's dot, from the same capture state as <see cref="State"/>.</summary>
    public StatusDot Dot
    {
        get => _dot;
        private set => Set(ref _dot, value);
    }

    /// <summary>The active warnings, newest first.</summary>
    public IReadOnlyList<StatusWarning> Warnings
    {
        get => _warnings;
        private set
        {
            if (Set(ref _warnings, value))
            {
                OnPropertyChanged(nameof(Errors));
                OnPropertyChanged(nameof(HasErrors));
            }
        }
    }

    /// <summary>The warnings the top card shows: the errors.</summary>
    public IEnumerable<StatusWarning> Errors => Warnings.Where(warning => warning.Severity == nameof(WarningSeverity.Error));

    public bool HasErrors => HasConnectionError || Errors.Any();

    public bool HasConnectionError => ConnectionError.Length > 0;

    /// <summary>Why the engine can't be reached, while it can't; empty while connected or still connecting.</summary>
    public string ConnectionError
    {
        get => _connectionError;
        private set
        {
            if (Set(ref _connectionError, value))
            {
                OnPropertyChanged(nameof(HasConnectionError));
                OnPropertyChanged(nameof(HasErrors));
            }
        }
    }

    public FrameworkElement View => _view ??= new StatusLineView { DataContext = this };

    /// <summary>Renders the session. Runs on the UI thread.</summary>
    public void Render(EngineStatus status, string? problem, StateSnapshot? state)
    {
        var connected = EngineSessions.ConnectedState(status, state);
        State = connected is not null ? StatusText.Capture(connected) : StatusText.Connection(status, problem);
        Dot = connected is null ? StatusDot.Idle : StatusText.Dot(connected.Capture);
        Hint = connected is null ? "" : StatusText.Hint(connected);
        var unreachable = status is EngineStatus.Restarting or EngineStatus.NotRunning or EngineStatus.CrashLoop or EngineStatus.Disconnected;
        ConnectionError = connected is null && unreachable ? StatusText.Connection(status, problem) : "";

        // Like the Overlay: a session that isn't connected has no warnings to show or dismiss.
        var warnings = connected?.Warnings ?? [];
        if (warnings.SequenceEqual(_rendered))
        {
            return;
        }

        _rendered = warnings;
        Warnings = [.. warnings.OrderByDescending(warning => warning.At).Select(warning => new StatusWarning(warning, Dismiss))];
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
