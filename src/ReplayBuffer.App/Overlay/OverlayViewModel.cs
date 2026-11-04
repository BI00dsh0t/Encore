using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

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
/// The mic-muted icon shows while the mic is muted and the dot shows, and the red X while a
/// <c>disk-full</c> Overlay warning is active, whatever the dot (R3).
/// The dot and the mic-muted icon show always, or only for <see cref="DotOnChangeDuration"/> after each
/// change of either, as <c>overlayDot</c> says. Its default, <c>auto</c>, shows them always where the Target
/// Game's monitor reports hardware overlay planes, since only a free plane keeps a borderless game in
/// independent flip under a topmost window (research for story 5.8; the builder's decision, 2026-11-03).
/// </summary>
internal sealed class OverlayViewModel : Observable
{
    private readonly IEngineSession _engine;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly List<Warning> _waiting = [];
    private readonly HashSet<string> _seen = [];
    private readonly Func<int?, bool?> _overlayPlanes;
    private ITimer? _cooldownEnd;
    private ITimer? _dotEnd;
    private DateTimeOffset _dotUntil;
    private (OverlayDot Dot, bool Muted) _steady;
    private (int? ProcessId, bool? Planes)? _planes;
    private ITimer? _messageEnd;
    private Warning? _shown;
    private Warning? _message;
    private OverlayDot _dot;
    private bool _saving;
    private bool _micMuted;
    private bool _diskFull;
    private int? _targetProcessId;

    /// <param name="post">Runs an action on the UI thread; session changes arrive on background threads.</param>
    /// <param name="time">Times the Save Cooldown's end and each message; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="overlayPlanes">
    /// Whether the monitor of a process (null: the primary) has hardware overlay planes, or null when unknown;
    /// the App passes <c>Monitors.OverlayPlanes</c>. When null, every monitor is taken to have them.
    /// </param>
    public OverlayViewModel(IEngineSession engine, Action<Action> post, TimeProvider? time = null, Func<int?, bool?>? overlayPlanes = null)
    {
        _engine = engine;
        _post = post;
        _time = time ?? TimeProvider.System;
        _overlayPlanes = overlayPlanes ?? (_ => true);
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

    /// <summary>
    /// Whether the mic-muted icon shows: the mic is muted while the dot shows. The muted default
    /// alone never shows it, so nothing shows while nothing runs.
    /// </summary>
    public bool MicMuted
    {
        get => _micMuted;
        private set => Set(ref _micMuted, value);
    }

    /// <summary>
    /// Whether the red X shows: a <c>disk-full</c> Overlay warning is active, until the engine
    /// dismisses it. It doesn't follow the dot, since a full disk can stop the capture.
    /// </summary>
    public bool DiskFull
    {
        get => _diskFull;
        private set => Set(ref _diskFull, value);
    }

    /// <summary>How long the dot shows after each change when it doesn't show always.</summary>
    public static TimeSpan DotOnChangeDuration { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Whether the dot shows for as long as its state lasts, rather than on each change.</summary>
    public bool DotAlways { get; private set; } = true;

    /// <summary>The monitors changed: ask again whether the Target Game's has overlay planes. Runs on the UI thread.</summary>
    public void DisplaysChanged()
    {
        _planes = null;
        Refresh();
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

    /// <summary>
    /// The dot the capture state calls for, before <c>overlayDot</c> decides whether it shows: solid while a
    /// Recording or Monitor Recording runs, hollow while the Replay Buffer buffers or drains. The tray icon
    /// shows the same state in every mode.
    /// </summary>
    public static OverlayDot DotFor(CaptureState? capture) =>
        capture is null ? OverlayDot.None
        : capture.Recording == RecordingState.Running || capture.MonitorRecording == RecordingState.Running ? OverlayDot.Solid
        : capture.Replay is ReplayState.Buffering or ReplayState.Draining ? OverlayDot.Hollow
        : OverlayDot.None;

    /// <summary>Reads the session again. Runs on the UI thread.</summary>
    public void Refresh()
    {
        var state = _engine.Status == EngineStatus.Connected ? _engine.State : null;
        var dot = DotFor(state?.Capture);

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
        (dot, var micMuted) = Present(state, dot, dot != OverlayDot.None && current!.MicMuted);
        var diskFull = state is not null && state.Warnings.Any(w => w.Overlay && w.Code == ErrorCodes.DiskFull);
        var message = NextMessage(state);

        // The window updates per property: hide before the Target moves, and move before showing,
        // so the dot, the icons and the message never flash on the wrong monitor.
        if (dot == OverlayDot.None && !saving && message is null && !diskFull)
        {
            Dot = dot;
            Saving = saving;
            Message = message;
            MicMuted = micMuted;
            DiskFull = diskFull;
            TargetProcessId = state?.TargetGame?.ProcessId;
        }
        else
        {
            TargetProcessId = state?.TargetGame?.ProcessId;
            Dot = dot;
            Saving = saving;
            Message = message;
            MicMuted = micMuted;
            DiskFull = diskFull;
        }
    }

    // The dot and the mic icon as shown: always, or for DotOnChangeDuration after either changes.
    private (OverlayDot Dot, bool Muted) Present(StateSnapshot? state, OverlayDot dot, bool muted)
    {
        var mode = state is null ? OverlayDotMode.Auto : SettingsValues.Global(state.Settings, Keys.OverlayDot);
        DotAlways = mode == OverlayDotMode.Always || (mode == OverlayDotMode.Auto && Planes(state?.TargetGame?.ProcessId) != false);
        var now = _time.GetUtcNow();
        if ((dot, muted) != _steady)
        {
            _steady = (dot, muted);
            _dotEnd?.Dispose();
            _dotEnd = null;
            _dotUntil = now + DotOnChangeDuration;
            if (dot != OverlayDot.None)
            {
                _dotEnd = _time.CreateTimer(_ => _post(Refresh), null, DotOnChangeDuration, Timeout.InfiniteTimeSpan);
            }
        }

        return DotAlways || now < _dotUntil ? (dot, muted) : (OverlayDot.None, false);
    }

    // Asked once per Target Game, and again when the monitors change.
    private bool? Planes(int? processId)
    {
        if (_planes is not { } known || known.ProcessId != processId)
        {
            known = (processId, _overlayPlanes(processId));
            _planes = known;
        }

        return known.Planes;
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
