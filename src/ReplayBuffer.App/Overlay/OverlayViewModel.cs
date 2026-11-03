using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App.Overlay;

/// <summary>What the Overlay shows: no dot, a hollow one (Replay Buffer on) or a solid one (recording).</summary>
internal enum OverlayDot
{
    None,
    Hollow,
    Solid,
}

/// <summary>
/// The Overlay's state, derived only from <see cref="IEngineSession.State"/> and its status (AD-3, AD-15).
/// </summary>
internal sealed class OverlayViewModel : Observable
{
    private readonly IEngineSession _engine;
    private OverlayDot _dot;
    private int? _targetProcessId;

    /// <param name="post">Runs an action on the UI thread; session changes arrive on background threads.</param>
    public OverlayViewModel(IEngineSession engine, Action<Action> post)
    {
        _engine = engine;
        engine.Changed += () => post(Refresh);
        Refresh();
    }

    public OverlayDot Dot
    {
        get => _dot;
        private set => Set(ref _dot, value);
    }

    /// <summary>The Target Game's process, whose monitor the Overlay sits on; <c>null</c> for the primary monitor.</summary>
    public int? TargetProcessId
    {
        get => _targetProcessId;
        private set => Set(ref _targetProcessId, value);
    }

    /// <summary>Reads the session again. Runs on the UI thread.</summary>
    public void Refresh()
    {
        var state = _engine.Status == EngineStatus.Connected ? _engine.State : null;
        TargetProcessId = state?.TargetGame?.ProcessId;
        Dot = state?.Capture is not { } capture ? OverlayDot.None
            : capture.Recording == RecordingState.Running || capture.MonitorRecording == RecordingState.Running ? OverlayDot.Solid
            : capture.Replay is ReplayState.Buffering or ReplayState.Draining ? OverlayDot.Hollow
            : OverlayDot.None;
    }
}
