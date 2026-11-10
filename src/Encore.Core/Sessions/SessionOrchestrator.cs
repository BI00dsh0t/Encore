using System.Diagnostics;
using System.Threading.Channels;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

/// <summary>The single-threaded orchestrator: the only code that starts, retargets and ends sessions. docs/code-notes.md.</summary>
public sealed partial class SessionOrchestrator : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for a running Recording's writer, inside the App's 5 s Quit.</summary>
    internal static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(3);

    /// <summary>The encoder budget: at most this many encoder chains run at once, across sessions.</summary>
    internal const int MaxChains = 2;

    private readonly Channel<SessionMessage> _channel = Channel.CreateUnbounded<SessionMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly ICaptureSessionFactory _factory;
    private readonly Func<SettingsDocument> _currentDocument;
    private readonly Action<CaptureState> _publish;
    private readonly Action<HotkeyPress>? _hotkeyPressed;
    private readonly IMicMuteStore? _micMute;
    private readonly Action<TargetChanged>? _targetChanged;
    private readonly TargetCapture? _targetCapture;
    private readonly ReplayClips? _clips;
    private readonly Recordings? _recordings;
    private readonly DeviceRecovery _deviceRecovery;
    private readonly IDisposable? _power;
    private readonly IDisplayMonitors? _displays;
    private readonly IDisposable? _displaysChanged;
    private readonly Thread _thread;

    // Orchestrator thread only.
    private Active? _active;

    // The game session drained after its Target exited, still holding its Rewind, and the timer for its expiry.
    private Active? _drain;
    private Timer? _drainTimer;
    private TargetCandidate? _target;
    private ProposeTarget? _pendingTarget;

    // The Target whose Disk-mode Rewind a full disk stopped, and the timer that posts its rechecks; null when none.
    private TargetCandidate? _ringWait;
    private Timer? _ringTimer;

    // The run of waits for the Target's window, when the game session can't capture one yet.
    private readonly CaptureWaitRun _captureWait;
    private CaptureState _state = CaptureState.Initial;
    private long _nextSessionId;

    private readonly SaveCooldown _saveCooldown = new();

    // Clip writes started and not yet posted back, across sessions.
    private int _writing;

    // The game session's Recording: the one running, and one an encoder restart ended that is still finishing.
    private readonly RecordingSlot _gameRecording = new();

    // The Monitor session, from its press until its Monitor Recording's writer posts back; null for none.
    private Active? _monitor;

    // The Monitor session's Monitor Recording, started at its anchor; as the game session's.
    private readonly RecordingSlot _monitorRecording = new();

    // The process of the Target whose session gave up on a GPU reset; a new window of it doesn't restart Rewind.
    private int? _gpuStoppedPid;

    private int _disposed;

    /// <summary>Every callback runs on the orchestrator thread; a null optional argument turns that feature off.</summary>
    public SessionOrchestrator(
        ICaptureSessionFactory factory,
        Func<SettingsDocument> currentDocument,
        Action<CaptureState> publish,
        Action<HotkeyPress>? hotkeyPressed = null,
        IMicMuteStore? micMute = null,
        Action<TargetChanged>? targetChanged = null,
        TargetCapture? targetCapture = null,
        ReplayClips? clips = null,
        Recordings? recordings = null,
        DeviceRecovery? deviceRecovery = null,
        IPowerNotifications? power = null,
        IDisplayMonitors? displays = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(currentDocument);
        ArgumentNullException.ThrowIfNull(publish);
        _factory = factory;
        _currentDocument = currentDocument;
        _publish = publish;
        _hotkeyPressed = hotkeyPressed;
        _micMute = micMute;
        _targetChanged = targetChanged;
        _targetCapture = targetCapture;
        _clips = clips;
        _recordings = recordings;
        _deviceRecovery = deviceRecovery ?? new DeviceRecovery();
        _captureWait = new CaptureWaitRun(targetCapture?.Wait, () => Post(new CaptureRetry()));

        // Before the thread runs, so no session can start with any other mute.
        _state = CaptureState.Initial with { MicMuted = LoadMicMuted(micMute) };
        if (_state != CaptureState.Initial)
        {
            // The first publish, before the thread exists: nothing else can publish yet.
            _publish(_state);
        }

        _thread = new Thread(Run) { Name = "SessionOrchestrator", IsBackground = true };
        _thread.Start();
        _power = power?.Subscribe(change => Post(change == PowerChange.Suspending
            ? new PowerSuspending(Stopwatch.GetTimestamp())
            : new PowerResumed()));
        _displays = displays;
        _displaysChanged = displays?.Subscribe(() => Post(new DisplaysChanged()));
    }

    /// <summary>The orchestrator thread's managed id.</summary>
    internal int ThreadId => _thread.ManagedThreadId;

    /// <summary>Posts a message from any thread; false once the orchestrator is disposed.</summary>
    public bool Post(SessionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _channel.Writer.TryWrite(message);
    }

    /// <summary>Starts a session, its snapshot resolved now for the target's profile; throws when one is already running.</summary>
    public Task<RunningSession> StartAsync(SessionKind kind, CaptureTarget target, SessionAudio audio)
    {
        var reply = NewReply<RunningSession>();
        PostOrFail(new StartSession(kind, target, audio, reply), reply);
        return reply.Task;
    }

    /// <summary>Moves the running session to another window of the same process; throws when none runs or the process differs.</summary>
    public Task<SessionSpec> RetargetAsync(CaptureTarget target)
    {
        var reply = NewReply<SessionSpec>();
        PostOrFail(new RetargetSession(target, reply), reply);
        return reply.Task;
    }

    /// <summary>Ends the running session and returns its outcome; throws when none is running.</summary>
    public Task<SessionOutcome> EndAsync(string reason = "ended")
    {
        var reply = NewReply<SessionOutcome>();
        PostOrFail(new EndSession(reason, reply), reply);
        return reply.Task;
    }

    /// <summary>Stops taking messages, handles those posted, ends any session, releases any drain, and joins the thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _power?.Dispose();
        _displaysChanged?.Dispose();
        _channel.Writer.TryComplete();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }
    }

    private static TaskCompletionSource<T> NewReply<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PostOrFail<T>(SessionMessage message, TaskCompletionSource<T> reply)
    {
        if (!Post(message))
        {
            reply.TrySetException(new ObjectDisposedException(nameof(SessionOrchestrator)));
        }
    }

    private void Run()
    {
        var reader = _channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var message))
            {
                Handle(message);
            }
        }

        ShutDown();
    }

    // The channel is complete: the sessions end, the Recording writers get one bounded wait, and the timers go.
    private void ShutDown()
    {
        if (_active is not null)
        {
            EndActive("engine stopping", failure: null, RecordingStopReasons.Shutdown);
        }

        if (_monitor is not null)
        {
            EndMonitor("engine stopping", RecordingStopReasons.Shutdown);
        }

        WaitForRecordingWriters();

        // The published state no longer claims a Recording runs.
        if (_gameRecording.IsOff)
        {
            SetState(_state with { Recording = RecordingState.Off });
        }

        SetState(_state with { MonitorRecording = RecordingState.Off });
        ReleaseDrain();

        // No callback: the engine is stopping.
        _ringTimer?.Dispose();
        _ringTimer = null;
        _captureWait.CancelRetry();
    }

    // The writers' post-backs can't arrive any more, so each is waited for here, all within ShutdownWait.
    private void WaitForRecordingWriters()
    {
        var waited = Stopwatch.StartNew();
        RecordingRun?[] unfinished =
            [_gameRecording.Ending, _gameRecording.Running, _monitorRecording.Ending, _monitorRecording.Running];
        foreach (var recording in unfinished)
        {
            if (recording is null)
            {
                continue;
            }

            var left = ShutdownWait - waited.Elapsed;
            if (recording.Done.Task.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                OnRecordingWritten(new RecordingWritten(recording, recording.Done.Task.Result), commitPending: false);
                continue;
            }

            // Left open: the next start reports it interrupted.
            SlotOf(recording).Remove(recording);
            _recordings?.Info?.Invoke(
                $"The Recording at {recording.Path} didn't finish within {ShutdownWait.TotalSeconds:0} s of shutdown; it was left open");
        }
    }

    // One case per message type, in the order the types are documented; a handler ignores a message that is out of date.
    private void Handle(SessionMessage message)
    {
        switch (message)
        {
            case StartSession start:
                Reply(start.Reply, () => Start(start));
                break;

            case RetargetSession retarget:
                Reply(retarget.Reply, () => Retarget(retarget.Target));
                break;

            case EndSession end:
                Reply(end.Reply, () => End(end.Reason));
                break;

            case HotkeyPressed pressed:
                OnHotkeyPressed(pressed);
                break;

            case ProposeTarget propose:
                CommitTarget(propose);
                break;

            case RecordingRunning recording:
                OnSimulatedRecording(recording);
                break;

            case ClipWritten written:
                OnClipWritten(written);
                break;

            case SettingsUpdated:
                OnSettingsUpdated();
                break;

            case RecordingWritten recorded:
                // Not once Dispose has begun: a held Target must never start a session during Quit.
                OnRecordingWritten(recorded, commitPending: Volatile.Read(ref _disposed) == 0);
                break;

            case SessionClosed closed:
                OnSessionClosed(closed);
                break;

            case SessionFailed failed:
                OnSessionFailed(failed);
                break;

            case SessionAnchored anchored:
                OnSessionAnchored(anchored);
                break;

            case SessionDeviceLost lost:
                OnSessionDeviceLost(lost);
                break;

            case RetryDeviceRecovery retry:
                OnRetryDeviceRecovery(retry);
                break;

            case SessionChainRestarted restarted:
                OnSessionChainRestarted(restarted);
                break;

            case DrainExpired expired:
                OnDrainExpired(expired);
                break;

            case CaptureRetry:
                OnCaptureRetry();
                break;

            case FollowCheck check:
                OnFollowCheck(check);
                break;

            case RingRecheck:
                OnRingRecheck();
                break;

            case PowerSuspending suspending:
                OnSuspend(suspending.Qpc);
                break;

            case PowerResumed:
                OnResume();
                break;

            case DisplaysChanged:
                OnDisplaysChanged();
                break;
        }
    }

    /// <summary>A Hotkey press goes to its feature, then to the engine's own listener.</summary>
    private void OnHotkeyPressed(HotkeyPressed pressed)
    {
        switch (pressed.Action)
        {
            case HotkeyAction.MicMute:
                ToggleMicMute(pressed.Qpc);
                break;

            case HotkeyAction.Save:
                SaveClip(pressed.Qpc);
                break;

            case HotkeyAction.RecordToggle or HotkeyAction.RecordStart or HotkeyAction.RecordStop:
                OnRecordPress(pressed.Action, pressed.Qpc);
                break;

            case HotkeyAction.MonitorRecordToggle or HotkeyAction.MonitorRecordStart or HotkeyAction.MonitorRecordStop:
                OnMonitorPress(pressed.Action, pressed.Qpc);
                break;
        }

        _hotkeyPressed?.Invoke(new HotkeyPress(pressed.Action, pressed.Qpc));
    }

    private static void Reply<T>(TaskCompletionSource<T> reply, Func<T> handler)
    {
        try
        {
            reply.TrySetResult(handler());
        }
        catch (Exception e)
        {
            reply.TrySetException(e);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: an empty reserved file is all that is left.
        }
    }

    private void SetState(CaptureState next)
    {
        if (next == _state)
        {
            return;
        }

        _state = next;
        _publish(next);
    }
}
