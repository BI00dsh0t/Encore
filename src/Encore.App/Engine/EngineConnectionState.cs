namespace Encore.App.Engine;

/// <summary>Where the App stands with the engine, why, and the live connection; shared by the connection's parts. Any thread.</summary>
internal sealed class EngineConnectionState
{
    private volatile EngineStatus _status = EngineStatus.Connecting;
    private volatile string? _problem;
    private EngineLink? _link;

    /// <summary>Raised, on a background thread, when the status changes or an event arrives.</summary>
    public event Action? Changed;

    public EngineStatus Status => _status;

    /// <summary>Why the last attempt to reach the engine failed, if it did.</summary>
    public string? Problem
    {
        get => _problem;
        set => _problem = value;
    }

    /// <summary>The current connection, or null before the first one.</summary>
    public EngineLink? Link => Volatile.Read(ref _link);

    /// <summary>Makes <paramref name="link"/> the current connection and returns the one it replaces.</summary>
    public EngineLink? ReplaceLink(EngineLink? link) => Interlocked.Exchange(ref _link, link);

    public void SetStatus(EngineStatus status)
    {
        _status = status;
        Changed?.Invoke();
    }

    public void RaiseChanged() => Changed?.Invoke();
}
