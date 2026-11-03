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
/// A snapshot read at reconnect is treated as any event: the dot and the saving icon follow it.
/// The snapshot's <c>overlay: true</c> warnings queue (R3): each shows as <see cref="Message"/> for
/// <see cref="MessageDuration"/>, oldest first, and an unseen repeat of a waiting one takes its place.
/// Which warnings were shown is the Overlay's own fact, kept by id, so a reconnect never replays one.
/// </summary>
internal sealed class OverlayViewModel : Observable
{
    private readonly IEngineSession _engine;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly List<Warning> _waiting = [];
    private readonly HashSet<string> _seen = [];
    private ITimer? _cooldownEnd;
    private ITimer? _messageEnd;
    private Warning? _shown;
    private Warning? _message;
    private OverlayDot _dot;
    private bool _saving;
    private int? _targetProcessId;

    /// <param name="post">Runs an action on the UI thread; session changes arrive on background threads.</param>
    /// <param name="time">Times the Save Cooldown's end and each message; <see cref="TimeProvider.System"/> when null.</param>
    public OverlayViewModel(IEngineSession engine, Action<Action> post, TimeProvider? time = null)
    {
        _engine = engine;
        _post = post;
        _time = time ?? TimeProvider.System;
        engine.Changed += () => post(Refresh);
        Refresh();
    }

    public OverlayDot Dot
    {
        get => _dot;
        private set => Set(ref _dot, value);
    }

    /// <summary>
    /// Whether the saving icon shows (R2): a Clip is being written, or the Save Cooldown of the last
    /// accepted press hasn't ended yet.
    /// </summary>
    public bool Saving
    {
        get => _saving;
        private set => Set(ref _saving, value);
    }

    /// <summary>How long each queued message shows.</summary>
    public static TimeSpan MessageDuration { get; } = TimeSpan.FromSeconds(4);

    /// <summary>The queued Overlay warning showing now, or <c>null</c>.</summary>
    public Warning? Message
    {
        get => _message;
        private set => Set(ref _message, value);
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
        var dot = state?.Capture is not { } capture ? OverlayDot.None
            : capture.Recording == RecordingState.Running || capture.MonitorRecording == RecordingState.Running ? OverlayDot.Solid
            : capture.Replay is ReplayState.Buffering or ReplayState.Draining ? OverlayDot.Hollow
            : OverlayDot.None;

        // The cooldown ends without an event, so a one-shot timer reads the session again then.
        _cooldownEnd?.Dispose();
        _cooldownEnd = null;
        var current = state?.Capture;
        var remaining = current?.CooldownUntil is { } until ? until - _time.GetUtcNow() : TimeSpan.Zero;
        if (remaining > TimeSpan.Zero)
        {
            _cooldownEnd = _time.CreateTimer(_ => _post(Refresh), null, remaining, Timeout.InfiniteTimeSpan);
        }

        var saving = current is not null && (current.ClipsWriting > 0 || remaining > TimeSpan.Zero);
        var message = NextMessage(state);

        // The window updates per property: hide before the Target moves, and move before showing,
        // so the dot, the icon and the message never flash on the wrong monitor.
        if (dot == OverlayDot.None && !saving && message is null)
        {
            Dot = dot;
            Saving = saving;
            Message = message;
            TargetProcessId = state?.TargetGame?.ProcessId;
        }
        else
        {
            TargetProcessId = state?.TargetGame?.ProcessId;
            Dot = dot;
            Saving = saving;
            Message = message;
        }
    }

    // Queues the unseen Overlay warnings and picks the one to show; null while disconnected.
    private Warning? NextMessage(StateSnapshot? state)
    {
        if (state is null)
        {
            // What was showing has been seen; what was waiting still waits.
            EndMessage();
            return null;
        }

        var active = state.Warnings.Where(w => w.Overlay).ToList();
        var activeIds = active.Select(w => w.Id).ToHashSet();
        _seen.RemoveWhere(id => !activeIds.Contains(id));
        foreach (var warning in active.OrderBy(w => w.At))
        {
            if (!_seen.Add(warning.Id))
            {
                continue;
            }

            // A repeat of a waiting message takes its place; a repeat of the one on screen queues.
            var repeat = _waiting.FindIndex(w => w.Code == warning.Code && w.Message == warning.Message);
            if (repeat >= 0)
            {
                _waiting[repeat] = warning;
            }
            else
            {
                _waiting.Add(warning);
            }
        }

        // Dismissed before their turn, or while showing.
        _waiting.RemoveAll(w => !activeIds.Contains(w.Id));
        if (_shown is not null && !activeIds.Contains(_shown.Id))
        {
            EndMessage();
        }

        if (_shown is null && _waiting.Count > 0)
        {
            var shown = _waiting[0];
            _waiting.RemoveAt(0);
            _shown = shown;
            _messageEnd = _time.CreateTimer(_ => _post(() => Expire(shown)), null, MessageDuration, Timeout.InfiniteTimeSpan);
        }

        return _shown;
    }

    // A timer already queued when its message ended early must not end the next one.
    private void Expire(Warning shown)
    {
        if (ReferenceEquals(_shown, shown))
        {
            EndMessage();
            Refresh();
        }
    }

    private void EndMessage()
    {
        _messageEnd?.Dispose();
        _messageEnd = null;
        _shown = null;
    }
}
