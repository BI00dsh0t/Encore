using Encore.App.Engine;
using Encore.App.Settings;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Overlay;

/// <summary>The Overlay's state, derived only from the engine's state: the dot, icons and queued messages. docs/code-notes.md.</summary>
internal sealed class OverlayViewModel : Observable
{
    private readonly IEngineSession _engine;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly List<Warning> _waiting = [];
    private readonly HashSet<string> _seen = [];
    private readonly Func<int?, bool?> _overlayPlanes;
    private readonly Action<Action> _background;
    // Whether a query is out, and for which Target Game process.
    private bool _askOut;
    private int? _askFor;

    // Bumped by a display change, so an answer asked before it is dropped.
    private int _generation;
    private bool _inQuery;
    private ITimer? _cooldownEnd;
    private ITimer? _dotEnd;
    private DateTimeOffset _dotUntil;
    private (OverlayDot Dot, bool Muted) _steady;
    private (int? ProcessId, bool? Planes, DateTimeOffset At)? _planes;
    private ITimer? _messageEnd;
    private Warning? _shown;
    private Warning? _message;
    private OverlayDot _dot;
    private bool _saving;
    private bool _micMuted;
    private bool _diskFull;
    private int? _targetProcessId;
    private int _displayChanges;

    /// <summary>Post runs on the UI thread; overlayPlanes and background are null in tests, where every monitor has planes.</summary>
    public OverlayViewModel(
        IEngineSession engine,
        Action<Action> post,
        TimeProvider? time = null,
        Func<int?, bool?>? overlayPlanes = null,
        Action<Action>? background = null)
    {
        _engine = engine;
        _post = post;
        _time = time ?? TimeProvider.System;
        _overlayPlanes = overlayPlanes ?? (_ => true);
        _background = background ?? (action => action());
        engine.Changed += () => post(Refresh);
        Refresh();
    }

    public OverlayDot Dot
    {
        get => _dot;
        private set => Set(ref _dot, value);
    }

    /// <summary>Whether the saving icon shows: a Clip is being written, or the Save Cooldown hasn't ended yet.</summary>
    public bool Saving
    {
        get => _saving;
        private set => Set(ref _saving, value);
    }

    /// <summary>Whether the mic-muted icon shows: the mic is muted while the dot shows; muted alone never shows it.</summary>
    public bool MicMuted
    {
        get => _micMuted;
        private set => Set(ref _micMuted, value);
    }

    /// <summary>Whether the red X shows: a <c>disk-full</c> Overlay warning is active; it doesn't follow the dot.</summary>
    public bool DiskFull
    {
        get => _diskFull;
        private set => Set(ref _diskFull, value);
    }

    /// <summary>How long the dot shows after each change when it doesn't show always.</summary>
    public static TimeSpan DotOnChangeDuration { get; } = TimeSpan.FromSeconds(3);

    /// <summary>How long an unknown answer to the overlay-plane query stands before it is asked again.</summary>
    public static TimeSpan UnknownPlanesRetry { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether the dot shows for as long as its state lasts, rather than on each change.</summary>
    public bool DotAlways { get; private set; } = true;

    /// <summary>The monitors changed: ask again whether the Target Game's has overlay planes. Runs on the UI thread.</summary>
    public void DisplaysChanged()
    {
        _planes = null;
        _askOut = false;
        _generation++;
        Refresh();

        // The window is placed again even when nothing it shows changed.
        DisplayChanges++;
    }

    /// <summary>How many display changes there have been; each one places the window again, on the Target Game's monitor.</summary>
    public int DisplayChanges
    {
        get => _displayChanges;
        private set => Set(ref _displayChanges, value);
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

    /// <summary>The dot the capture state calls for: solid while a Recording runs, hollow while Rewind buffers or drains.</summary>
    public static OverlayDot DotFor(CaptureState? capture) =>
        capture is null ? OverlayDot.None
        : capture.Recording == RecordingState.Running || capture.MonitorRecording == RecordingState.Running ? OverlayDot.Solid
        : capture.Replay is ReplayState.Buffering or ReplayState.Draining ? OverlayDot.Hollow
        : OverlayDot.None;

    /// <summary>Reads the session again. Runs on the UI thread.</summary>
    public void Refresh()
    {
        var state = _engine.ConnectedState();
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

        // Hide before the Target moves, and move before showing, so nothing flashes on the wrong monitor.
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
        DotAlways = mode == OverlayDotMode.Always || (mode == OverlayDotMode.Auto && Planes(state?.TargetGame?.ProcessId) == true);
        var now = _time.GetUtcNow();
        if ((dot, muted) != _steady)
        {
            _steady = (dot, muted);
            _dotEnd?.Dispose();
            _dotEnd = null;
            _dotUntil = now + DotOnChangeDuration;
        }

        var shown = DotAlways || now < _dotUntil;

        // A timer can fire a little early: arm again for what's left, so the dot never stays up too long.
        if (!DotAlways && dot != OverlayDot.None && now < _dotUntil && _dotEnd is null)
        {
            _dotEnd = _time.CreateTimer(_ => _post(EndDot), null, _dotUntil - now, Timeout.InfiniteTimeSpan);
        }

        return shown ? (dot, muted) : (OverlayDot.None, false);
    }

    private void EndDot()
    {
        _dotEnd?.Dispose();
        _dotEnd = null;
        Refresh();
    }

    // Asked once per Target Game and when the monitors change; until answered, the monitor counts as having no planes.
    private bool? Planes(int? processId)
    {
        var now = _time.GetUtcNow();
        var stale = _planes is not { } known || known.ProcessId != processId
            || (known.Planes is null && now - known.At >= UnknownPlanesRetry);
        if (stale && !(_askOut && _askFor == processId))
        {
            (_askOut, _askFor) = (true, processId);
            var generation = _generation;
            _inQuery = true;
            try
            {
                _background(() =>
                {
                    bool? planes;
                    try
                    {
                        planes = _overlayPlanes(processId);
                    }
                    catch (Exception)
                    {
                        planes = null;
                    }

                    _post(() => Answered(generation, processId, planes));
                });
            }
            finally
            {
                _inQuery = false;
            }
        }

        return _planes is { } answer && answer.ProcessId == processId ? answer.Planes : null;
    }

    // Runs on the UI thread. An answer that comes back inline, inside Planes, is read by the Refresh that asked.
    private void Answered(int generation, int? processId, bool? planes)
    {
        if (generation != _generation)
        {
            return;
        }

        if (_askOut && _askFor == processId)
        {
            _askOut = false;
        }

        _planes = (processId, planes, _time.GetUtcNow());
        if (!_inQuery)
        {
            Refresh();
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
