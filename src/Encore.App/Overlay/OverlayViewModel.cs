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
    private readonly OverlayPlaneQuery _planes;
    private readonly OverlayMessageQueue _messages;
    private ITimer? _cooldownEnd;
    private ITimer? _dotEnd;
    private DateTimeOffset _dotUntil;
    private (OverlayDot Dot, bool Muted) _steady;
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
        _planes = new OverlayPlaneQuery(_time, overlayPlanes ?? (_ => true), background ?? (action => action()), post, Refresh);
        _messages = new OverlayMessageQueue(_time, post, Refresh);
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
        _planes.Reset();
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
        var diskFull = state is not null && state.Warnings.Any(warning => warning.Overlay && warning.Code == ErrorCodes.DiskFull);
        var message = _messages.Next(state);

        // Hide before the Target moves, and move before showing, so nothing flashes on the wrong monitor.
        var hides = dot == OverlayDot.None && !saving && message is null && !diskFull;
        if (!hides)
        {
            TargetProcessId = state?.TargetGame?.ProcessId;
        }

        Dot = dot;
        Saving = saving;
        Message = message;
        MicMuted = micMuted;
        DiskFull = diskFull;
        if (hides)
        {
            TargetProcessId = state?.TargetGame?.ProcessId;
        }
    }

    // The dot and the mic icon as shown: always, or for DotOnChangeDuration after either changes.
    private (OverlayDot Dot, bool Muted) Present(StateSnapshot? state, OverlayDot dot, bool muted)
    {
        var mode = state is null ? OverlayDotMode.Auto : SettingsValues.Global(state.Settings, Keys.OverlayDot);
        DotAlways = mode == OverlayDotMode.Always || (mode == OverlayDotMode.Auto && _planes.Planes(state?.TargetGame?.ProcessId) == true);
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
}
