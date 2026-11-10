using System.Diagnostics;
using System.Threading.Channels;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Hotkeys;
using Encore.Core.Library;
using Encore.Core.Monitors;
using Encore.Core.Output;
using Encore.Core.Settings;

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

    // A run of waits for the Target's window: the Target, the timer for the next try, and the failures so far.
    private TargetCandidate? _captureWait;
    private Timer? _captureTimer;
    private int _captureFailures;
    private CaptureState _state = CaptureState.Initial;
    private long _nextSessionId;

    // The Hotkey QPC of the last accepted save press, which the Save Cooldown runs from; a new session keeps it.
    private long? _cooldownFrom;

    // Clip writes started and not yet posted back, across sessions.
    private int _writing;

    // The Recording started and not yet posted back; null when none runs.
    private RecordingRun? _recording;

    // The Recording a forced encoder restart ended, until its writer posts back; null for none.
    private RecordingRun? _ending;

    // The Monitor session, from its press until its Monitor Recording's writer posts back; null for none.
    private Active? _monitor;

    // The Monitor Recording started at the Monitor session's anchor and not yet posted back; null for none.
    private RecordingRun? _monitorRecording;

    // The Monitor Recording a forced encoder restart ended, until its writer posts back; null for none.
    private RecordingRun? _monitorEnding;

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
        if (_recording is null && _ending is null)
        {
            SetState(_state with { Recording = RecordingState.Off });
        }

        SetState(_state with { MonitorRecording = RecordingState.Off });
        ReleaseDrain();

        // No callback: the engine is stopping.
        _ringTimer?.Dispose();
        _ringTimer = null;
        _captureTimer?.Dispose();
        _captureTimer = null;
    }

    // The writers' post-backs can't arrive any more, so each is waited for here, all within ShutdownWait.
    private void WaitForRecordingWriters()
    {
        var waited = Stopwatch.StartNew();
        foreach (var recording in new[] { _ending, _recording, _monitorEnding, _monitorRecording })
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
            Forget(recording);
            _recordings?.Info?.Invoke(
                $"The Recording at {recording.Path} didn't finish within {ShutdownWait.TotalSeconds:0} s of shutdown; it was left open");
        }
    }

    // Clears whichever of the four Recording fields holds this Recording.
    private void Forget(RecordingRun recording)
    {
        if (ReferenceEquals(_ending, recording))
        {
            _ending = null;
        }
        else if (ReferenceEquals(_monitorEnding, recording))
        {
            _monitorEnding = null;
        }
        else if (ReferenceEquals(_monitorRecording, recording))
        {
            _monitorRecording = null;
        }
        else
        {
            _recording = null;
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

    /// <summary>dev.simulate's display state only: a real Recording's state wins, and it never holds the Target.</summary>
    private void OnSimulatedRecording(RecordingRunning recording)
    {
        if (_recording is null && _ending is null)
        {
            SetState(_state with { Recording = recording.Running ? RecordingState.Running : RecordingState.Off });
        }
    }

    private void OnSettingsUpdated()
    {
        if (_active is { } active)
        {
            ApplySettings(active);
        }
    }

    /// <summary>A session's capture ended on its own: the Monitor session ends; the game session follows, drains or ends.</summary>
    private void OnSessionClosed(SessionClosed closed)
    {
        if (_monitor is { } monitor && monitor.Id == closed.SessionId)
        {
            // WGC can see the monitor go before the display change arrives.
            if (!EndMonitorIfRemoved(monitor))
            {
                EndMonitor(closed.Reason, RecordingStopReasons.SessionEnded);
            }
        }
        else if (_active is { } active && active.Id == closed.SessionId)
        {
            OnActiveClosed(active, closed.Reason);
        }
    }

    private void OnActiveClosed(Active active, string reason)
    {
        // A game whose process still runs is followed to its next window (TargetCapture.Wait).
        if (Follows(active))
        {
            OnWindowClosed(active);
        }

        // WGC can see the window close before Game Detection sees the exit: drain here too.
        else if (Drains(active))
        {
            DrainActive(reason, RecordingStopReasons.GameExit);
        }
        else
        {
            EndActive(reason, failure: null, RecordingStopReasons.GameExit);
        }
    }

    private void OnSessionFailed(SessionFailed failed)
    {
        if (_monitor is { } monitor && monitor.Id == failed.SessionId)
        {
            if (!EndMonitorIfRemoved(monitor))
            {
                _recordings?.Failed(failed.Failure);
                EndMonitor("failure: " + failed.Failure.Message, RecordingStopReasons.SessionEnded);
            }
        }
        else if (_active is { } active && active.Id == failed.SessionId)
        {
            OnActiveFailed(active, failed.Failure);
        }
    }

    private void OnSessionAnchored(SessionAnchored anchored)
    {
        if (_monitor is { } monitor && monitor.Id == anchored.SessionId)
        {
            OnMonitorAnchored(monitor, anchored.Qpc);
        }
        else if (_active is { } active && active.Id == anchored.SessionId)
        {
            OnActiveAnchored(active);
        }
    }

    private void OnSessionDeviceLost(SessionDeviceLost lost)
    {
        if (_monitor is { } monitor && monitor.Id == lost.SessionId)
        {
            OnDeviceLost(monitor, lost.Failure, monitor: true);
        }
        else if (_active is { } active && active.Id == lost.SessionId)
        {
            OnDeviceLost(active, lost.Failure, monitor: false);
        }
    }

    private void OnRetryDeviceRecovery(RetryDeviceRecovery retry)
    {
        if (_monitor is { } monitor && monitor.Id == retry.SessionId)
        {
            TryRecover(monitor, monitor: true);
        }
        else if (_active is { } active && active.Id == retry.SessionId)
        {
            TryRecover(active, monitor: false);
        }
    }

    private void OnSessionChainRestarted(SessionChainRestarted restarted)
    {
        if (_monitor is { } monitor && monitor.Id == restarted.SessionId)
        {
            OnRecovered(monitor);
            OnMonitorChainRestarted(monitor, restarted.Qpc);
        }
        else if (_active is { } active && active.Id == restarted.SessionId)
        {
            OnRecovered(active);
            OnChainRestarted(active, restarted.Qpc);
        }
    }

    private void OnDrainExpired(DrainExpired expired)
    {
        if (_drain?.Id == expired.SessionId)
        {
            ReleaseDrain();
        }
    }

    private void OnCaptureRetry()
    {
        if (_captureWait is { } waiting)
        {
            RetryCapture(waiting);
        }
    }

    private void OnFollowCheck(FollowCheck check)
    {
        if (_active is { } active && active.Id == check.SessionId && Follows(active))
        {
            FollowWindow(active, _targetCapture!.Wait!, active.Spec.Target.Window);
        }
    }

    private void OnRingRecheck()
    {
        if (_ringWait is { } wait)
        {
            RecheckRing(wait);
        }
    }

    private void OnDisplaysChanged()
    {
        if (_monitor is { } monitor)
        {
            EndMonitorIfRemoved(monitor);
        }
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

    private RunningSession Start(StartSession start) => Start(start.Kind, start.Target, _ => start.Audio);

    /// <summary>The audio comes from the snapshot resolved here; gameName is kept for the session's Clips.</summary>
    private RunningSession Start(SessionKind kind, CaptureTarget target, Func<EffectiveSettings, SessionAudio> audio, string? gameName = null)
    {
        if (_active is not null)
        {
            throw new ErrorCodeException(ErrorCodes.SessionRunning, "A session is already running.");
        }

        // A drained ring never outlives the start of a new session, nor does a wait for room for one.
        ReleaseDrain();
        ClearRingWait();

        // Resolved here: a retarget never touches it, and a later settings change arrives only by a chain switch.
        var snapshot = SettingsResolver.Resolve(_currentDocument(), target.ProfileId);
        var spec = new SessionSpec(kind, target, snapshot, audio(snapshot), _state.MicMuted);
        var id = ++_nextSessionId;
        var session = _factory.Start(spec, new Events(this, id));
        var active = new Active(id, session, spec, audio) { GameName = gameName };
        _active = active;

        // A Monitor Recording running holds the other chain, so a size move tears down first.
        if (_monitor is not null)
        {
            session.SetBuildAhead(false);
        }

        SetState(_state with { Replay = kind == SessionKind.Game ? ReplayState.Buffering : ReplayState.Off });
        return new RunningSession(spec, active.Ended.Task);
    }

    private SessionSpec Retarget(CaptureTarget target)
    {
        var active = _active ?? throw new ErrorCodeException(ErrorCodes.SessionNotRunning, "No session is running.");
        if (target.ProcessId != active.Spec.Target.ProcessId)
        {
            throw new ErrorCodeException(
                ErrorCodes.RetargetOtherProcess,
                $"The window belongs to process {target.ProcessId}, not the session's process {active.Spec.Target.ProcessId}; that is a new session.");
        }

        try
        {
            active.Session.Retarget(target);
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.RetargetOtherProcess)
        {
            // Refused before the old capture was touched: the session goes on.
            throw;
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.SessionNotRunning)
        {
            // It had already stopped itself (its post-back is still queued): its own outcome stands.
            EndActive("retarget after stop", failure: null);
            throw;
        }
        catch (Exception e)
        {
            // The old capture is already stopped: the session can't go on.
            EndActive("failure: " + e.Message, e);
            throw;
        }

        // The profile belongs to the session (its snapshot was resolved for it), not to the retarget.
        active.Spec = active.Spec with { Target = target with { ProfileId = active.Spec.Target.ProfileId } };
        return active.Spec;
    }

    private SessionOutcome End(string reason)
    {
        if (_active is null)
        {
            throw new ErrorCodeException(ErrorCodes.SessionNotRunning, "No session is running.");
        }

        return EndActive(reason, failure: null);
    }

    /// <param name="recordingReason">The stop reason of a Recording running on the session.</param>
    private SessionOutcome EndActive(string reason, Exception? failure, string recordingReason = RecordingStopReasons.SessionEnded)
    {
        var active = _active!;
        _active = null;
        StopRecovery(active);
        active.Follow?.Dispose();
        active.Follow = null;
        MarkSessionEnded(active, recordingReason);

        SessionOutcome outcome;
        try
        {
            outcome = active.Session.End(reason);
        }
        catch (Exception e)
        {
            outcome = new SessionOutcome(null, reason, e);
        }

        if (failure is not null && outcome.Failure is null)
        {
            outcome = outcome with { Failure = failure };
        }

        // Published before Ended completes, so whoever awaits it sees the state already off.
        SetState(_state with { Replay = ReplayState.Off });
        active.Ended.TrySetResult(outcome);
        return outcome;
    }

    /// <summary>Whether <paramref name="active"/> is drained, not ended, when its Target goes: a game session with Replay Clips.</summary>
    private bool Drains(Active? active) => _clips is not null && active is { Spec.Kind: SessionKind.Game };

    /// <summary>Ends the game session's capture but keeps its Rewind for saves: it becomes the drain, and a timer ends it.</summary>
    private void DrainActive(string reason, string recordingReason)
    {
        var active = _active!;
        _active = null;
        StopRecovery(active);
        active.Follow?.Dispose();
        active.Follow = null;
        MarkSessionEnded(active, recordingReason);
        ReleaseDrain();

        SessionOutcome outcome;
        try
        {
            outcome = active.Session.Drain(reason);
        }
        catch (Exception e)
        {
            outcome = new SessionOutcome(null, reason, e);
        }

        if (outcome.Failure is not null)
        {
            // It never anchored, failed or couldn't stop cleanly: there is nothing to keep.
            SetState(_state with { Replay = ReplayState.Off });
            TryRelease(active);
            active.Ended.TrySetResult(outcome);
            return;
        }

        _drain = active;
        SetState(_state with { Replay = ReplayState.Draining });
        active.Ended.TrySetResult(outcome);
        var id = active.Id;
        _drainTimer = new Timer(_ => Post(new DrainExpired(id)), null, _clips!.ExitDrain ?? ReplayClips.DefaultExitDrain, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Frees the drained session's Rewind, if any; <see cref="ReplayState.Off"/> unless a session is active.</summary>
    private void ReleaseDrain()
    {
        if (_drain is not { } drain)
        {
            return;
        }

        _drain = null;
        _drainTimer?.Dispose();
        _drainTimer = null;
        TryRelease(drain);
        if (_active is null)
        {
            SetState(_state with { Replay = ReplayState.Off });
        }
    }

    private static void TryRelease(Active session)
    {
        try
        {
            session.Session.Release();
        }
        catch (Exception)
        {
            // Releasing is best effort: a save in flight keeps its own lease either way.
        }
    }

    /// <summary>The saved mic mute; muted when there is no store or it fails.</summary>
    private static bool LoadMicMuted(IMicMuteStore? store)
    {
        if (store is null)
        {
            return true;
        }

        try
        {
            return store.Load();
        }
        catch (Exception)
        {
            // The store reports its own failures; a faulty one can't leave the mic open.
            return true;
        }
    }

    /// <summary>Flips mic mute: the session first, then the store, then the published state; a failure of either is swallowed.</summary>
    private void ToggleMicMute(long qpc)
    {
        var muted = !_state.MicMuted;
        if (_active is { } active)
        {
            try
            {
                active.Session.SetMicMuted(muted, qpc);
            }
            catch (Exception)
            {
                // The session's own failure path ends it if it is broken; mute is still committed.
            }
        }

        if (_monitor is { } monitor)
        {
            try
            {
                monitor.Session.SetMicMuted(muted, qpc);
            }
            catch (Exception)
            {
                // As for the game session.
            }
        }

        try
        {
            _micMute?.Save(muted);
        }
        catch (Exception)
        {
            // The store logs its own failures; a faulty one must not stop the orchestrator.
        }

        SetState(_state with { MicMuted = muted });
    }

    /// <summary>Commits a proposed Target Game; while a Recording runs, a switch to another game is held. docs/code-notes.md.</summary>
    private void CommitTarget(ProposeTarget propose)
    {
        var current = _target;
        var to = propose.To;
        if (to is not null && current is not null && to.ProcessId == current.ProcessId)
        {
            _pendingTarget = null;
            if (to.Window != current.Window)
            {
                _target = current with { Window = to.Window };
                FollowTargetWindow(_target);
            }

            return;
        }

        if ((_recording is not null || _ending is not null) && current is not null && to is not null && propose.Reason is not (TargetChangeReason.Exit or TargetChangeReason.Ignored))
        {
            _pendingTarget = propose;
            return;
        }

        _pendingTarget = null;
        if (to is null && current is null)
        {
            return;
        }

        _target = to;

        // A Rewind given up on a GPU reset stays off only for the Target it stopped on.
        _gpuStoppedPid = null;

        // A wait for room for the ring belongs to the Target it stopped on, as does a wait for its window.
        ClearRingWait();
        EndCaptureWait(captured: false);
        _targetChanged?.Invoke(new TargetChanged(current?.ToTargetGame(), to?.ToTargetGame(), propose.Reason));
        if (_targetCapture is null)
        {
            return;
        }

        // Only an exit to no game leaves a drain running (one a closed window started, say).
        if (to is not null || propose.Reason != TargetChangeReason.Exit)
        {
            ReleaseDrain();
        }

        if (_active is { Spec.Kind: SessionKind.Game })
        {
            // Only an exit with no other game drains; a switch or Ignored ends at once.
            var recordingReason = propose.Reason switch
            {
                TargetChangeReason.Exit => RecordingStopReasons.GameExit,
                TargetChangeReason.Ignored => RecordingStopReasons.Ignored,
                _ => RecordingStopReasons.SessionEnded,
            };
            if (to is null && propose.Reason == TargetChangeReason.Exit && Drains(_active))
            {
                DrainActive("target exited", recordingReason);
            }
            else
            {
                EndActive("target changed", failure: null, recordingReason);
            }
        }

        if (to is not null)
        {
            StartOnTarget(to);
        }
    }

    /// <summary>Detection named a new window of the Target's process: the game session starts on it, or moves to it.</summary>
    private void FollowTargetWindow(TargetCandidate target)
    {
        if (_targetCapture is null)
        {
            return;
        }

        if (_active is null)
        {
            // A Rewind a full disk stopped waits for its recheck; one a GPU reset stopped, for the next Target.
            if (_ringWait is null && _gpuStoppedPid != target.ProcessId)
            {
                StartOnTarget(target);
            }

            return;
        }

        if (_active.Spec.Kind != SessionKind.Game || _active.Spec.Target.ProcessId != target.ProcessId)
        {
            return;
        }

        if (Follows(_active))
        {
            FollowWindow(_active, _targetCapture.Wait!, target.Window);
            return;
        }

        try
        {
            Retarget(new CaptureTarget(target.ProcessId, target.Window, target.ProfileId));
        }
        catch (Exception e)
        {
            // A failed retarget has already ended the session where it couldn't go on.
            _targetCapture.Failed(target, e);
        }
    }

    /// <summary>Starts the game session on the Target, waiting for a window that can be captured when the options say so.</summary>
    private void StartOnTarget(TargetCandidate target)
    {
        var capture = _targetCapture!;
        var wait = capture.Wait;

        // A new window, a ring recheck or the timer: this try replaces any one still pending.
        _captureTimer?.Dispose();
        _captureTimer = null;
        if (wait is not null)
        {
            var window = Find(wait, target);
            if (!window.Found)
            {
                WaitForCapture(wait, target, window.Why ?? "no window of it can be captured", failed: false);
                return;
            }

            target = target with { Window = window.Window };
            if (_target?.ProcessId == target.ProcessId)
            {
                _target = target;
            }
        }

        try
        {
            Start(SessionKind.Game, new CaptureTarget(target.ProcessId, target.Window, target.ProfileId), capture.Audio, target.Name);
        }
        catch (Exception e) when (wait is not null && wait.Retry(e))
        {
            WaitForCapture(wait, target, e.Message, failed: true);
        }
        catch (Exception e)
        {
            // The Target stays committed; only its capture failed.
            capture.Failed(target, e);
        }
    }

    /// <summary>The window of <paramref name="target"/>'s process to capture; a search that throws keeps the given window.</summary>
    private static CaptureWindow Find(CaptureWait wait, TargetCandidate target)
    {
        try
        {
            return wait.Find(target);
        }
        catch (Exception)
        {
            // The session's own start or retarget reports a window it can't capture.
            return CaptureWindow.Of(target.Window);
        }
    }

    /// <summary>Whether a game session follows the Target's process: capture waits, and it was started on that process.</summary>
    private bool Follows(Active active) =>
        _targetCapture?.Wait is not null
        && Volatile.Read(ref _disposed) == 0
        && active is { Spec.Kind: SessionKind.Game, GameName: not null }
        && _target is { } target
        && target.ProcessId == active.Spec.Target.ProcessId;

    /// <summary>The game session failed; one that follows the Target, with a failure worth retrying, restarts after a backoff.</summary>
    private void OnActiveFailed(Active active, Exception failure)
    {
        var retry = Follows(active) && _targetCapture!.Wait!.Retry(failure);
        EndActive("failure: " + failure.Message, failure);
        if (retry)
        {
            WaitForCapture(_targetCapture!.Wait!, _target!, failure.Message, failed: true);
        }
    }

    /// <summary>The followed window closed: before the first frame the session restarts; after it, it moves to the next window.</summary>
    private void OnWindowClosed(Active active)
    {
        var wait = _targetCapture!.Wait!;
        if (!active.Anchored)
        {
            EndActive("window closed", failure: null);
            WaitForCapture(wait, _target!, "the window closed before its first frame", failed: true);
            return;
        }

        active.WindowClosed = true;
        FollowWindow(active, wait, active.Spec.Target.Window);
    }

    /// <summary>The game session got its first frame: a wait for its Target's window is over, and its window is now watched.</summary>
    private void OnActiveAnchored(Active active)
    {
        active.Anchored = true;
        if (Follows(active) && active.Follow is null)
        {
            var poll = _targetCapture!.Wait!.Poll;
            var id = active.Id;
            active.Follow = new Timer(_ => Post(new FollowCheck(id)), null, poll, poll);
        }

        if (_captureWait is { } waiting && waiting.ProcessId == active.Spec.Target.ProcessId)
        {
            EndCaptureWait(captured: true);
        }
    }

    /// <summary>Keeps the game session on a window it can capture, retargeting so Rewind is kept; with none, it waits.</summary>
    private void FollowWindow(Active active, CaptureWait wait, nint prefer)
    {
        if (active.LostAt is not null || active.Suspended || _target is not { } target)
        {
            return;
        }

        var found = Find(wait, target with { Window = prefer });
        if (!found.Found)
        {
            BeginCaptureWait(wait, target, found.Why ?? "no window of it can be captured");
            return;
        }

        if (found.Window == active.Spec.Target.Window && !active.WindowClosed)
        {
            if (_captureWait is { } same && same.ProcessId == target.ProcessId)
            {
                EndCaptureWait(captured: true);
            }

            return;
        }

        if (Stopwatch.GetTimestamp() < active.FollowAfter)
        {
            return;
        }

        try
        {
            active.Session.Retarget(new CaptureTarget(target.ProcessId, found.Window, target.ProfileId));
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.SessionNotRunning)
        {
            // It had already stopped itself (its post-back is still queued): start again.
            EndActive("retarget after stop", failure: null);
            WaitForCapture(wait, target, e.Message, failed: true);
            return;
        }
        catch (Exception e)
        {
            // No capture runs now, but the session and its Rewind go on: try again later.
            active.FollowFailures++;
            active.FollowAfter = Stopwatch.GetTimestamp() + (long)(wait.Backoff(active.FollowFailures).TotalSeconds * Stopwatch.Frequency);
            active.WindowClosed = true;
            BeginCaptureWait(wait, target, e.Message);
            return;
        }

        active.WindowClosed = false;
        active.FollowFailures = 0;
        active.FollowAfter = 0;
        active.Spec = active.Spec with { Target = active.Spec.Target with { Window = found.Window } };
        _target = target with { Window = found.Window };
        if (_captureWait is { } waiting && waiting.ProcessId == target.ProcessId)
        {
            EndCaptureWait(captured: true);
        }
    }

    /// <summary>Starts a run of waits for a Target's window, reported once; a run for another process ends first.</summary>
    private void BeginCaptureWait(CaptureWait wait, TargetCandidate target, string why)
    {
        if (_captureWait is { } running && running.ProcessId != target.ProcessId)
        {
            EndCaptureWait(captured: false);
        }

        if (_captureWait is null)
        {
            _captureFailures = 0;
            wait.Waiting(target, why);
        }

        _captureWait = target;
    }

    /// <summary>Waits for the Target's window with no session: the run's first wait is reported, and the next try is posted.</summary>
    private void WaitForCapture(CaptureWait wait, TargetCandidate target, string why, bool failed)
    {
        BeginCaptureWait(wait, target, why);
        if (failed)
        {
            _captureFailures++;
        }

        _captureTimer?.Dispose();
        _captureTimer = new Timer(_ => Post(new CaptureRetry()), null, failed ? wait.Backoff(_captureFailures) : wait.Poll, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>The capture wait's timer fired: the game session starts on the Target if it is still the one waited for.</summary>
    private void RetryCapture(TargetCandidate waiting)
    {
        _captureTimer?.Dispose();
        _captureTimer = null;
        if (_active is { Spec.Kind: SessionKind.Game } running && running.Spec.Target.ProcessId == waiting.ProcessId)
        {
            // A tick posted before the session started: the session follows the window now.
            return;
        }

        if (_active is not null || _targetCapture is null || _target is not { } target || target.ProcessId != waiting.ProcessId)
        {
            // A session runs (one started by hand), or the Target moved on without a commit ending the wait.
            EndCaptureWait(captured: false);
            return;
        }

        if (_ringWait is not null || _gpuStoppedPid == target.ProcessId)
        {
            // A full disk or a given-up GPU reset holds Rewind off; their own waits restart it.
            EndCaptureWait(captured: false);
            return;
        }

        StartOnTarget(target);
    }

    /// <summary>Ends a wait for the Target's window, if any, and reports how it ended.</summary>
    private void EndCaptureWait(bool captured)
    {
        if (_captureWait is not { } waiting)
        {
            return;
        }

        _captureWait = null;
        _captureFailures = 0;
        _captureTimer?.Dispose();
        _captureTimer = null;
        _targetCapture?.Wait?.Ended(waiting, captured);
    }

    /// <summary>A save press: checks the cooldown and limits, reserves the path, then hands the session the write.</summary>
    private void SaveClip(long qpc)
    {
        var active = _active is { Spec.Kind: SessionKind.Game } game ? game : _drain;
        if (_clips is null || active is null)
        {
            return;
        }

        // Read at each press (Global, Immediate); measured in Hotkey QPC from the accepted press.
        var document = _currentDocument();
        var global = SettingsResolver.Resolve(document, null);
        var cooldown = Math.Max(0, global.SaveCooldown) * Stopwatch.Frequency;
        if (_cooldownFrom is { } from && qpc - from < cooldown)
        {
            _clips.Ignored?.Invoke(TimeSpan.FromSeconds((qpc - from) / (double)Stopwatch.Frequency));
            return;
        }

        _cooldownFrom = qpc;
        DateTimeOffset? until = cooldown == 0
            ? null
            : DateTimeOffset.UtcNow + TimeSpan.FromSeconds((qpc + cooldown - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
        SetState(_state with { CooldownUntil = until });
        _clips.Saving?.Invoke(new ClipSaving(DateTimeOffset.UtcNow));

        if (_writing >= ReplayClips.MaxWriters)
        {
            // The ring keeps capturing; nothing is cut and nothing is Reset.
            _clips.NotSaved?.Invoke(new ClipFailed(ErrorCodes.SaveBusy));
            return;
        }

        if (active.ResetQpc is { } reset && qpc - reset < Stopwatch.Frequency)
        {
            _clips.NotSaved?.Invoke(new ClipFailed(ErrorCodes.ClipTooShort));
            return;
        }

        IClipWrite? write;
        string path;
        try
        {
            write = active.Session.Save(qpc);
            if (write is null)
            {
                // Nothing to save yet; the accepted press still gets its outcome.
                _clips.NotSaved?.Invoke(new ClipFailed(ErrorCodes.ClipTooShort));
                return;
            }

            var folder = SettingsResolver.Resolve(document, null).OutputFolder ?? _clips.DefaultOutputFolder();
            var profileId = active.Spec.Target.ProfileId;
            path = _clips.Paths.AllocateForGame(document, profileId, GameName(active), folder, qpc);
        }
        catch (Exception e)
        {
            ReportSaveFailure(e);
            return;
        }

        var sessionId = active.Id;
        var library = _clips.Library ?? NoLibraryWrites.Instance;
        library.BeginWrite(path, active.Spec.Target.ProfileId, LibraryItemKinds.Clip);
        try
        {
            write.Write(path, error => Post(new ClipWritten(sessionId, qpc, write, path, error)));
            _writing++;
        }
        catch (Exception e)
        {
            // It never started: nothing else will remove the reserved file.
            TryDelete(path);
            library.EndWrite(path, WriteOutcome.Failed);
            TakeSwitch(active, write);
            ReportSaveFailure(e);
            return;
        }

        TakeSwitch(active, write);
        SetState(_state with { ClipsWriting = _writing });
    }

    /// <summary>When a write's press switched the encoder chain, the spec takes the new snapshot and audio; the Target is kept.</summary>
    private static void TakeSwitch(Active active, IClipWrite write)
    {
        if (write.SwitchesTo is { } switched)
        {
            active.Spec = active.Spec with { Snapshot = switched.Snapshot, Audio = switched.Audio };
            active.Pending = false;
        }
    }

    /// <summary>A save that made no Clip: a real failure is logged, and every one is reported with its reason.</summary>
    private void ReportSaveFailure(Exception e)
    {
        if (e is not ErrorCodeException { Code: ErrorCodes.ClipTooShort or ErrorCodes.SaveBusy })
        {
            _clips?.Failed(e);
        }

        _clips?.NotSaved?.Invoke(new ClipFailed(WriteFailures.ReasonOf(e)));
    }

    /// <summary>The game's name: the one the session started on, else the Target's, else the profile's executable name.</summary>
    private string GameName(Active active)
    {
        if (active.GameName is { } name)
        {
            return name;
        }

        var target = active.Spec.Target;
        return _target is { } candidate && candidate.ProcessId == target.ProcessId
            ? candidate.Name
            : target.ProfileId is { } profileId ? Path.GetFileName(profileId) : "Game";
    }

    /// <summary>A writer's result: a written Clip Resets Rewind at its end; a failed write Resets nothing.</summary>
    private void OnClipWritten(ClipWritten written)
    {
        // Whatever the session: the write is no longer in flight.
        _writing--;
        SetState(_state with { ClipsWriting = _writing });
        (_clips?.Library ?? NoLibraryWrites.Instance).EndWrite(written.Path, written.Error is null ? WriteOutcome.Written : WriteOutcome.Failed);
        if (written.Error is not null)
        {
            ReportSaveFailure(written.Error);
            return;
        }

        // The session still running, or the drained one: either still holds its Rewind.
        var active = _active?.Id == written.SessionId ? _active : _drain?.Id == written.SessionId ? _drain : null;
        if (active is not null)
        {
            // The running session takes the Effective Buffer Window at each Reset; a drained one keeps its ring as is.
            if (ReferenceEquals(active, _active))
            {
                active.Session.SetBufferWindow(SettingsResolver.Resolve(_currentDocument(), active.Spec.Target.ProfileId).BufferWindow);
            }

            written.Write.Reset();
            active.ResetQpc = active.ResetQpc is { } reset ? Math.Max(reset, written.Qpc) : written.Qpc;

            // Each Reset resolves the settings again, so a failed build is retried and a held change switches here.
            if (ReferenceEquals(active, _active))
            {
                ApplySettings(active, atReset: true);
            }
        }

        _clips?.Saved(new ClipSaved(written.Path));
    }

    /// <summary>Resolves the game session's settings again and prepares or drops an encoder chain to match.</summary>
    private void ApplySettings(Active active, bool atReset = false)
    {
        if (_clips is null || active.Spec.Kind != SessionKind.Game)
        {
            return;
        }

        SessionSpec next;
        try
        {
            var snapshot = SettingsResolver.Resolve(_currentDocument(), active.Spec.Target.ProfileId);
            next = active.Spec with { Snapshot = snapshot, Audio = active.Audio(snapshot) };
        }
        catch (Exception)
        {
            // An invalid Track Layout: the session keeps its chain.
            return;
        }

        try
        {
            if (ChainKey.SameChain(next, active.Spec))
            {
                if (active.Pending)
                {
                    active.Pending = false;
                    active.Session.PrepareChain(null);
                }
            }
            else if (_recording?.SessionId == active.Id)
            {
                // A Recording keeps the session on one chain until its writer posts back.
                if (active.Pending)
                {
                    active.Pending = false;
                    active.Session.PrepareChain(null);
                }

                if (!active.Deferred)
                {
                    active.Deferred = true;
                    _recordings?.Info?.Invoke("This game's video quality or audio tracks changed during a Recording; the change applies once the Recording stops");
                }
            }
            else if (_monitor is not null)
            {
                if (active.Pending)
                {
                    active.Pending = false;
                    active.Session.PrepareChain(null);
                }

                if (atReset && active.Session.SwitchChain(next))
                {
                    active.Spec = active.Spec with { Snapshot = next.Snapshot, Audio = next.Audio };
                }
            }
            else
            {
                active.Pending = true;
                active.Session.PrepareChain(next);
            }
        }
        catch (Exception)
        {
            // The session's own failure path ends it if it is broken.
        }
    }

    /// <summary>A Record press, in the mode read live from settings: toggle, start or stop. A stop is never gated.</summary>
    private void OnRecordPress(HotkeyAction action, long qpc)
    {
        if (_recordings is not { } recordings)
        {
            return;
        }

        // One document per press, so its mode and its profile's switch come from one revision.
        var document = _currentDocument();
        var mode = SettingsResolver.Resolve(document, null).RecordHotkeyMode;
        if (!HotkeyPlan.IsActive(action, mode))
        {
            recordings.Info?.Invoke($"The {action} press isn't used in the {mode} Record Hotkey mode; it was ignored");
            return;
        }

        if (_recording is not { } running)
        {
            if (action == HotkeyAction.RecordStop)
            {
                recordings.Info?.Invoke("The Record Stop press came with no Recording running; it was ignored");
                return;
            }

            StartRecording(qpc, document);
            return;
        }

        if (action == HotkeyAction.RecordStart)
        {
            recordings.Info?.Invoke("The Record Start press came while a Recording runs; it was ignored");
            return;
        }

        if (running.Reason is not null)
        {
            recordings.Info?.Invoke("The Record press came while the Recording was stopping; it was ignored");
            return;
        }

        running.Reason = RecordingStopReasons.Stopped;
        try
        {
            running.Write.Stop(qpc);
        }
        catch (Exception e)
        {
            // The writer still finishes when the session's output ends.
            recordings.Failed(e);
        }
    }

    /// <summary>Starts a Recording on the game session when its profile allows it; otherwise only the Info callback says why.</summary>
    private void StartRecording(long qpc, SettingsDocument document)
    {
        var recordings = _recordings!;
        if (_active is not { Spec.Kind: SessionKind.Game } active)
        {
            recordings.Info?.Invoke(_drain is not null
                ? "The Record press came while the game session was draining; no Recording was started"
                : "The Record press came with no game session; no Recording was started");
            return;
        }

        var profileId = active.Spec.Target.ProfileId;
        if (profileId is null || !document.Profiles.TryGetValue(profileId, out var profile))
        {
            recordings.Info?.Invoke("The Record key was pressed before Encore knew the game; no Recording was started");
            return;
        }

        if (!profile.RecordingsEnabled)
        {
            recordings.Info?.Invoke($"Recordings are off for {profile.Name}; the Record press was ignored");
            return;
        }

        if (BeginRecording(active, profileId, qpc, document) is { } recording)
        {
            SetState(_state with { Recording = RecordingState.Running });
            recordings.Started(new RecordingStarted(recording.Path));
        }
    }

    /// <summary>Begins the session's Recording at a path reserved now; null, with the reason reported, when none started.</summary>
    private RecordingRun? BeginRecording(Active active, string profileId, long qpc, SettingsDocument document)
    {
        var recordings = _recordings!;
        IRecordingWrite? write;
        string path;
        try
        {
            write = active.Session.Record(qpc);
            if (write is null)
            {
                recordings.Info?.Invoke("The game session can't record yet; no Recording was started");
                return null;
            }

            // One snapshot for the press; the Recording shares the session's chain whatever it asks for.
            var snapshot = SettingsResolver.Resolve(document, profileId);
            if (ChainKey.Of(SessionKind.Game, active.Spec.Target, snapshot) != ChainKey.Of(active.Spec))
            {
                recordings.Info?.Invoke("The profile's video or Tracks differ from the game session's; the Recording shares the session's");
            }

            var folder = snapshot.OutputFolder ?? recordings.DefaultOutputFolder();
            path = recordings.Paths.AllocateForGame(document, profileId, GameName(active), folder, qpc);
        }
        catch (Exception e)
        {
            recordings.Failed(e);
            return null;
        }

        if (OpenWrite(recordings, new RecordingRun(active.Id, path, write), profileId) is not { } recording)
        {
            return null;
        }

        _recording = recording;

        // A chain prepared before the press is dropped; the change waits for the Recording's end.
        if (active.Pending)
        {
            active.Pending = false;
            try
            {
                active.Session.PrepareChain(null);
            }
            catch (Exception)
            {
                // The session's own failure path ends it if it is broken.
            }
        }

        active.Deferred = false;
        ApplySettings(active);
        return recording;
    }

    /// <summary>The chain was forcibly rebuilt: the running Recording ends, and a new one starts at once in a new file.</summary>
    private void OnChainRestarted(Active active, long qpc)
    {
        if (_recordings is not { } recordings || _recording is not { } running || running.SessionId != active.Id || running.Reason is not null)
        {
            return;
        }

        running.Reason = RecordingStopReasons.EncoderRestart;
        if (_ending is not null)
        {
            // The last restart's Recording is still finishing: this one stops without a replacement.
            recordings.Info?.Invoke("A second encoder restart came while the last one's Recording was still finishing; the Recording stops");
            return;
        }

        _ending = running;
        _recording = null;
        var profileId = active.Spec.Target.ProfileId;
        var next = profileId is null ? null : BeginRecording(active, profileId, qpc, _currentDocument());
        if (next is null)
        {
            recordings.Info?.Invoke($"The encoder restart ended the Recording at {running.Path}, and no new one could start");
            return;
        }

        next.Follows = running;
    }

    /// <summary>A restart's new Recording, once the one it replaced is accounted for: published as started, then the handover.</summary>
    private void Announce(RecordingRun recording)
    {
        if (recording.Follows is not { } follows)
        {
            return;
        }

        recording.Follows = null;
        var recordings = _recordings!;
        var started = recording.Monitor ? recordings.MonitorStarted ?? recordings.Started : recordings.Started;
        started(new RecordingStarted(recording.Path));
        recordings.Restarted?.Invoke(new RecordingRestart(follows.Path, recording.Path));
    }

    /// <summary>A session ending under its running Recording stops it with the given reason.</summary>
    private void MarkSessionEnded(Active active, string reason)
    {
        if (_recording is { Reason: null } running && running.SessionId == active.Id)
        {
            running.Reason = reason;
        }
    }

    /// <summary>A Recording writer's result: tells the Library, publishes the stop, then commits a held Target switch.</summary>
    private void OnRecordingWritten(RecordingWritten written, bool commitPending)
    {
        var recording = written.Recording;
        if (recording.Monitor)
        {
            OnMonitorRecordingWritten(written);
            return;
        }

        var restarted = ReferenceEquals(_ending, recording);
        if (!restarted && !ReferenceEquals(_recording, recording))
        {
            return;
        }

        if (restarted)
        {
            _ending = null;
        }
        else
        {
            // A restart's replacement that ends before the one it replaced: started before it stopped.
            Announce(recording);
            _recording = null;
        }

        var recordings = _recordings!;
        var reason = CloseWrite(recordings, written);
        var off = _recording is null && _ending is null;
        if (off)
        {
            SetState(_state with { Recording = RecordingState.Off });
        }

        recordings.Stopped(new RecordingStopped(recording.Path, reason));
        if (reason == ErrorCodes.DiskFull)
        {
            OnDiskFull(recordings, recording);
        }

        if (restarted && _recording is { } next && ReferenceEquals(next.Follows, recording))
        {
            Announce(next);
        }

        if (!off)
        {
            return;
        }

        // The session still runs and no switch is held: a change deferred during the Recording is prepared now.
        if (commitPending && _pendingTarget is null && _active is { } active && active.Id == recording.SessionId)
        {
            active.Deferred = false;
            ApplySettings(active);
        }

        if (commitPending && _pendingTarget is { } pending)
        {
            CommitTarget(pending);
        }
    }

    /// <summary>A Monitor Record press, handled as a Record press; a stop before the session anchored just ends it.</summary>
    private void OnMonitorPress(HotkeyAction action, long qpc)
    {
        if (_recordings is not { } recordings)
        {
            return;
        }

        var document = _currentDocument();
        var mode = SettingsResolver.Resolve(document, null).RecordHotkeyMode;
        if (!HotkeyPlan.IsActive(action, mode))
        {
            recordings.Info?.Invoke($"The {action} press isn't used in the {mode} Record Hotkey mode; it was ignored");
            return;
        }

        if (_monitor is null)
        {
            if (action == HotkeyAction.MonitorRecordStop)
            {
                recordings.Info?.Invoke("The Monitor Record Stop press came with no Monitor Recording running; it was ignored");
                return;
            }

            if (_monitorRecording is not null || _monitorEnding is not null)
            {
                // Its session ended (closed or failed) and its writer hasn't posted back yet.
                recordings.Info?.Invoke("The Monitor Record press came while the last Monitor Recording was still finishing; it was ignored");
                return;
            }

            StartMonitor(document);
            return;
        }

        if (action == HotkeyAction.MonitorRecordStart)
        {
            recordings.Info?.Invoke("The Monitor Record Start press came while a Monitor Recording runs; it was ignored");
            return;
        }

        if (_monitorRecording is not { } running)
        {
            // Not anchored yet: nothing was written, and nothing is left.
            EndMonitor("monitor recording stopped", RecordingStopReasons.Stopped);
            return;
        }

        if (running.Reason is not null)
        {
            recordings.Info?.Invoke("The Monitor Record press came while the Monitor Recording was stopping; it was ignored");
            return;
        }

        running.Reason = RecordingStopReasons.Stopped;
        try
        {
            running.Write.Stop(qpc);
        }
        catch (Exception e)
        {
            // The writer still finishes when the session's output ends.
            recordings.Failed(e);
        }
    }

    /// <summary>Starts the Monitor session on the Global settings, unless the encoder budget is spent.</summary>
    private void StartMonitor(SettingsDocument document)
    {
        var recordings = _recordings!;

        // No size move builds a chain ahead from here on, so none slips in before the budget check.
        _active?.Session.SetBuildAhead(false);
        if (_active is { } game)
        {
            int chains;
            try
            {
                if (game.Pending)
                {
                    game.Pending = false;
                    game.Session.PrepareChain(null);
                }

                chains = game.Session.Chains;
            }
            catch (Exception)
            {
                // The session's own failure path ends it if it is broken.
                chains = 1;
            }

            if (chains + 1 > MaxChains)
            {
                recordings.Warn?.Invoke(
                    ErrorCodes.EncoderBudget,
                    $"The Monitor Recording didn't start: your graphics card's video encoder is already busy with this game ({chains} videos).");

                // The chain dropped above is asked for again: nothing runs beside the game session.
                game.Session.SetBuildAhead(true);
                ApplySettings(game);
                return;
            }
        }

        Active monitor;
        try
        {
            var snapshot = SettingsResolver.Resolve(document, null);
            if (!ResolveScreens(snapshot, out var screens))
            {
                _active?.Session.SetBuildAhead(true);
                return;
            }

            var spec = new SessionSpec(SessionKind.Monitor, new CaptureTarget(0, 0, null), snapshot, MonitorAudio(snapshot), _state.MicMuted) { Screens = screens };
            var id = ++_nextSessionId;
            var session = _factory.Start(spec, new Events(this, id));
            monitor = new Active(id, session, spec, MonitorAudio);
        }
        catch (Exception e)
        {
            _active?.Session.SetBuildAhead(true);
            recordings.Failed(e);
            return;
        }

        _monitor = monitor;
        SetState(_state with { MonitorRecording = RecordingState.Running });
    }

    /// <summary>Resolves the screens a Monitor Recording composites; a missing one is skipped with a warning.</summary>
    private bool ResolveScreens(EffectiveSettings snapshot, out IReadOnlyList<DisplayMonitor>? screens)
    {
        screens = null;
        if (snapshot.MonitorRecordingScreens is not { } chosen || _displays is not { } displays)
        {
            return true;
        }

        var recordings = _recordings!;
        var attached = displays.List().Where(m => m.Width > 0 && m.Height > 0).ToList();
        var resolved = MonitorScreens.Resolve(chosen, snapshot.MonitorRecordingMonitor, attached);
        foreach (var missing in resolved.Missing)
        {
            recordings.Warn?.Invoke(ErrorCodes.MonitorMissing, $"The chosen screen {missing} isn't connected; the Monitor Recording skips it");
        }

        if (resolved.Screens.Count == 0)
        {
            recordings.Failed(new ErrorCodeException(
                ErrorCodes.MonitorRecordingNoScreens, "The Monitor Recording didn't start: none of the screens chosen to record is connected."));
            return false;
        }

        screens = resolved.Screens;
        return true;
    }

    /// <summary>The Monitor session's audio: the Global Track Layout's.</summary>
    private static SessionAudio MonitorAudio(EffectiveSettings snapshot) => SessionAudio.FromLayout(snapshot.TrackLayout);

    /// <summary>The Monitor session's first frame: its Recording starts there; one that can't start ends the session.</summary>
    private void OnMonitorAnchored(Active monitor, long qpc)
    {
        if (_monitorRecording is not null)
        {
            return;
        }

        if (BeginMonitorRecording(monitor, qpc) is not { } recording)
        {
            EndMonitor("monitor recording failed", RecordingStopReasons.SessionEnded);
            return;
        }

        var recordings = _recordings!;
        (recordings.MonitorStarted ?? recordings.Started)(new RecordingStarted(recording.Path));
    }

    /// <summary>Begins a Monitor Recording at a path reserved now; null, with the reason reported, when none started.</summary>
    private RecordingRun? BeginMonitorRecording(Active monitor, long qpc)
    {
        var recordings = _recordings!;
        IRecordingWrite? write;
        string path;
        try
        {
            write = monitor.Session.Record(qpc);
            if (write is null)
            {
                recordings.Info?.Invoke("The Monitor session can't record; no Monitor Recording was started");
                return null;
            }

            var folder = SettingsResolver.Resolve(_currentDocument(), null).OutputFolder ?? recordings.DefaultOutputFolder();
            path = recordings.Paths.AllocateForMonitor(folder, qpc);
        }
        catch (Exception e)
        {
            recordings.Failed(e);
            return null;
        }

        if (OpenWrite(recordings, new RecordingRun(monitor.Id, path, write) { Monitor = true }, profileId: null) is not { } recording)
        {
            return null;
        }

        _monitorRecording = recording;
        return recording;
    }

    /// <summary>The Monitor session's chain was rebuilt: its Recording ends and a new one starts at once in a new file.</summary>
    private void OnMonitorChainRestarted(Active monitor, long qpc)
    {
        if (_recordings is not { } recordings || _monitorRecording is not { } running || running.SessionId != monitor.Id || running.Reason is not null)
        {
            return;
        }

        running.Reason = RecordingStopReasons.EncoderRestart;
        if (_monitorEnding is not null)
        {
            recordings.Info?.Invoke("A second encoder restart came while the last one's Monitor Recording was still finishing; the Monitor Recording stops");
            return;
        }

        _monitorEnding = running;
        _monitorRecording = null;
        if (BeginMonitorRecording(monitor, qpc) is not { } next)
        {
            recordings.Info?.Invoke($"The encoder restart ended the Monitor Recording at {running.Path}, and no new one could start");
            return;
        }

        next.Follows = running;
    }

    /// <summary>A session's GPU device was lost, or a recovery attempt failed: retry on a timer, and give up after a limit.</summary>
    private void OnDeviceLost(Active session, Exception failure, bool monitor)
    {
        // An attempt that failed as the PC went to sleep: the resume retries.
        if (session.Suspended)
        {
            return;
        }

        if (session.LostAt is null)
        {
            session.LostAt = Stopwatch.GetTimestamp();
            session.ResetRaised = true;
            _deviceRecovery.Reset?.Invoke(monitor
                ? $"The GPU driver reset; the Monitor Recording is rebuilding its capture and encoder ({failure.Message})"
                : $"The graphics driver reset; Encore is restarting its capture ({failure.Message})");
            TryRecover(session, monitor);
            return;
        }

        if (Stopwatch.GetElapsedTime(session.LostAt.Value) >= (_deviceRecovery.GiveUpAfter ?? DeviceRecovery.DefaultGiveUpAfter))
        {
            GiveUpRecovery(session, failure, monitor);
            return;
        }

        ScheduleRecovery(session);
    }

    /// <summary>Asks a lost session for one recovery attempt; a throw or refusal is retried, until the give-up time.</summary>
    private void TryRecover(Active session, bool monitor)
    {
        session.Retry?.Dispose();
        session.Retry = null;
        if (session.LostAt is not { } lostAt || session.Suspended)
        {
            return;
        }

        bool started;
        try
        {
            started = session.Session.RecoverDevice();
        }
        catch (Exception e)
        {
            Post(new SessionDeviceLost(session.Id, e));
            return;
        }

        if (started)
        {
            // Asked again after RetryEvery: refused while this attempt runs, so one that hangs still reaches the give-up.
            ScheduleRecovery(session);
            return;
        }

        if (Stopwatch.GetElapsedTime(lostAt) >= (_deviceRecovery.GiveUpAfter ?? DeviceRecovery.DefaultGiveUpAfter))
        {
            GiveUpRecovery(session, new TimeoutException("No recovery attempt succeeded in time."), monitor);
            return;
        }

        ScheduleRecovery(session);
    }

    /// <summary>A one-shot timer posting the session's next recovery attempt after <see cref="DeviceRecovery.RetryEvery"/>.</summary>
    private void ScheduleRecovery(Active session)
    {
        session.Retry?.Dispose();
        var id = session.Id;
        session.Retry = new Timer(_ => Post(new RetryDeviceRecovery(id)), null, _deviceRecovery.RetryEvery ?? DeviceRecovery.DefaultRetryEvery, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Recovery took too long: the game session ends and stays off until the Target's process changes.</summary>
    private void GiveUpRecovery(Active session, Exception failure, bool monitor)
    {
        var after = (_deviceRecovery.GiveUpAfter ?? DeviceRecovery.DefaultGiveUpAfter).TotalSeconds;
        string message;
        if (monitor)
        {
            message = $"The Monitor Recording couldn't recover from a GPU driver reset within {after:0} s; it stopped ({failure.Message})";
            EndMonitor("gpu reset failed", RecordingStopReasons.SessionEnded);
        }
        else
        {
            message = session.Resuming
                ? $"Encore couldn't restart capture within {after:0} s of the PC waking; Clips are off until your next game ({failure.Message})"
                : $"Encore couldn't recover from a graphics driver reset within {after:0} s; Clips are off until your next game ({failure.Message})";
            _gpuStoppedPid = session.Spec.Target.ProcessId;
            EndActive("gpu reset failed", new ErrorCodeException(ErrorCodes.GpuResetFailed, message, failure));
        }

        _deviceRecovery.Failed?.Invoke(message);
    }

    /// <summary>A session's chain restarted: when that was a GPU reset's recovery, the wait ends and the time taken is reported.</summary>
    private void OnRecovered(Active session)
    {
        if (session.LostAt is not { } lostAt)
        {
            return;
        }

        StopRecovery(session);

        // One that installed just as the PC went to sleep is torn down by the suspend, and the resume rebuilds.
        if (session.Suspended)
        {
            return;
        }

        // A resume's rebuild ends a GPU reset's outage too when one ran into the sleep: its Reset is answered.
        session.Resuming = false;
        if (!session.ResetRaised)
        {
            return;
        }

        session.ResetRaised = false;
        _deviceRecovery.Recovered?.Invoke(Stopwatch.GetElapsedTime(lostAt));
    }

    /// <summary>The PC is going to sleep: Recordings stop, the Monitor session ends, and the game session suspends.</summary>
    private void OnSuspend(long qpc)
    {
        var ran = false;
        if (_recording is { Reason: null } running)
        {
            ran = true;
            running.Reason = RecordingStopReasons.Suspend;
            try
            {
                running.Write.Stop(qpc);
            }
            catch (Exception e)
            {
                // The writer still finishes when the session's output ends.
                _recordings?.Failed(e);
            }
        }

        if (_monitor is { } monitor)
        {
            ran |= _monitorRecording is { Reason: null } monitorRunning && monitorRunning.SessionId == monitor.Id;
            EndMonitor("suspend", RecordingStopReasons.Suspend);
        }

        if (ran)
        {
            _recordings?.Warn?.Invoke(ErrorCodes.RecordingSuspended, "The PC went to sleep; the Recording ended at its last fragment and isn't restarted");
        }

        if (_active is not { Suspended: false } active)
        {
            return;
        }

        try
        {
            active.Suspended = active.Session.Suspend();
        }
        catch (Exception e)
        {
            _recordings?.Info?.Invoke($"The game session couldn't suspend for sleep: {e.Message}");
        }

        // Kept lost: the resume restarts the wait from then. A session that didn't suspend keeps its retries.
        if (active.Suspended)
        {
            active.Retry?.Dispose();
            active.Retry = null;
        }
    }

    /// <summary>The PC woke: a suspended game session rebuilds as after a GPU reset; a second resume is ignored.</summary>
    private void OnResume()
    {
        if (_active is not { Suspended: true } active)
        {
            return;
        }

        active.Suspended = false;
        active.Resuming = true;
        active.LostAt = Stopwatch.GetTimestamp();
        TryRecover(active, monitor: false);
    }

    /// <summary>Ends the Monitor session when its monitor is no longer attached; false when it is, or it can't be told.</summary>
    private bool EndMonitorIfRemoved(Active monitor)
    {
        if (_displays is not { } displays)
        {
            return false;
        }

        IReadOnlyList<string> devices;
        IReadOnlyList<DisplayMonitor> attached;
        try
        {
            devices = monitor.Session.MonitorDevices;
            attached = displays.List();
        }
        catch (Exception e)
        {
            _recordings?.Info?.Invoke($"The monitors couldn't be read after a display change: {e.Message}");
            return false;
        }

        // An empty list is a read that failed mid-change, not every monitor gone.
        if (devices.Count == 0 || attached.Count == 0)
        {
            return false;
        }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Under All screens, a monitor that arrives joins at the next Monitor Recording.
        if (monitor.Spec.Screens is not null && monitor.Spec.Snapshot.MonitorRecordingScreens?.Mode == MonitorScreensMode.All)
        {
            foreach (var arrived in attached.Where(m => m.Width > 0 && !devices.Any(d => Same(d, m.DeviceName))))
            {
                if (monitor.Announced.Add(arrived.DeviceName))
                {
                    _recordings?.Warn?.Invoke(
                        ErrorCodes.MonitorAddedNextRecording,
                        $"Screen {arrived.DeviceName} was connected; it joins at the next Monitor Recording (the video's size can't change inside a file)");
                }
            }
        }

        var gone = devices.Where(d => !attached.Any(m => Same(m.DeviceName, d))).ToList();
        if (gone.Count == 0)
        {
            return false;
        }

        // Some of several composited monitors gone: their areas turn black and the Recording goes on.
        if (gone.Count < devices.Count)
        {
            foreach (var device in gone)
            {
                if (monitor.Session.DropMonitor(device))
                {
                    _recordings?.Warn?.Invoke(
                        ErrorCodes.MonitorRemoved,
                        $"Screen {device} was removed; its part of the Monitor Recording is black and the Monitor Recording goes on");
                }
            }

            return false;
        }

        EndMonitor("monitor removed", RecordingStopReasons.MonitorRemoved);
        _recordings?.Warn?.Invoke(
            ErrorCodes.MonitorRemoved,
            $"Screen {string.Join(", ", gone)} was removed; the Monitor Recording ended at its last frame and its file was kept");
        return true;
    }

    /// <summary>Ends a session's recovery wait, if any: no retry is posted after this.</summary>
    private static void StopRecovery(Active session)
    {
        session.LostAt = null;
        session.Retry?.Dispose();
        session.Retry = null;
    }

    /// <summary>Ends the Monitor session and stops its Recording; the game session's settings are then resolved again.</summary>
    private void EndMonitor(string reason, string recordingReason)
    {
        var monitor = _monitor!;
        _monitor = null;
        _active?.Session.SetBuildAhead(true);
        StopRecovery(monitor);
        if (_monitorRecording is { Reason: null } running && running.SessionId == monitor.Id)
        {
            running.Reason = recordingReason;
        }

        SessionOutcome outcome;
        try
        {
            outcome = monitor.Session.End(reason);
        }
        catch (Exception e)
        {
            outcome = new SessionOutcome(null, reason, e);
        }

        if (_monitorRecording is null && _monitorEnding is null)
        {
            SetState(_state with { MonitorRecording = RecordingState.Off });
        }

        monitor.Ended.TrySetResult(outcome);
        if (Volatile.Read(ref _disposed) == 0 && _active is { } game)
        {
            ApplySettings(game);
        }
    }

    /// <summary>A Monitor Recording writer's result: published as stopped, then the Monitor session ends.</summary>
    private void OnMonitorRecordingWritten(RecordingWritten written)
    {
        var recording = written.Recording;
        var restarted = ReferenceEquals(_monitorEnding, recording);
        if (!restarted && !ReferenceEquals(_monitorRecording, recording))
        {
            return;
        }

        if (restarted)
        {
            _monitorEnding = null;
        }
        else
        {
            // A restart's replacement that ends before the one it replaced: started before it stopped.
            Announce(recording);
            _monitorRecording = null;
        }

        var recordings = _recordings!;
        var reason = CloseWrite(recordings, written);
        var off = _monitorRecording is null && _monitorEnding is null;
        if (off)
        {
            SetState(_state with { MonitorRecording = RecordingState.Off });
        }

        (recordings.MonitorStopped ?? recordings.Stopped)(new RecordingStopped(recording.Path, reason));
        if (reason == ErrorCodes.DiskFull)
        {
            recordings.DiskFull?.Invoke();
        }

        if (restarted && _monitorRecording is { } next && ReferenceEquals(next.Follows, recording))
        {
            Announce(next);
        }

        if (off && _monitor?.Id == recording.SessionId)
        {
            EndMonitor("monitor recording stopped", RecordingStopReasons.SessionEnded);
        }
    }

    /// <summary>Begins a Recording's write in the Library and starts its writer; null, the reserved file removed, on failure.</summary>
    private RecordingRun? OpenWrite(Recordings recordings, RecordingRun recording, string? profileId)
    {
        var path = recording.Path;
        var library = recordings.Library ?? NoLibraryWrites.Instance;
        library.BeginWrite(path, profileId, recording.Monitor ? LibraryItemKinds.MonitorRecording : LibraryItemKinds.Recording);
        recordings.Open?.Opened(path);
        try
        {
            recording.Write.Start(path, error =>
            {
                // Before the post, so a shutdown that closed the channel still sees the result.
                recording.Done.TrySetResult(error);
                Post(new RecordingWritten(recording, error));
            });
        }
        catch (Exception e)
        {
            // It never started: nothing else will remove the reserved file.
            TryDelete(path);
            library.EndWrite(path, WriteOutcome.Failed);
            recordings.Open?.Closed(path);
            recordings.Failed(e);
            return null;
        }

        return recording;
    }

    /// <summary>Ends a finished Recording's write in the Library and returns the stop's reason: the failure's, else its own.</summary>
    private static string CloseWrite(Recordings recordings, RecordingWritten written)
    {
        var (recording, error) = (written.Recording, written.Error);
        var kept = error is null || File.Exists(recording.Path);
        (recordings.Library ?? NoLibraryWrites.Instance).EndWrite(recording.Path, kept ? WriteOutcome.Written : WriteOutcome.Failed);
        recordings.Open?.Closed(recording.Path);
        if (error is not null)
        {
            recordings.Failed(error);
        }

        return error is not null ? WriteFailures.ReasonOf(error) : recording.Reason ?? RecordingStopReasons.SessionEnded;
    }

    /// <summary>A Recording stopped on a full disk: with the ring on that disk, the session ends and waits for room.</summary>
    private void OnDiskFull(Recordings recordings, RecordingRun recording)
    {
        recordings.DiskFull?.Invoke();
        if (Volatile.Read(ref _disposed) != 0
            || _clips is null
            || _targetCapture is null
            || _target is not { } target
            || _active is not { Spec.Kind: SessionKind.Game } active
            || active.Id != recording.SessionId
            || active.Spec.Snapshot.ReplayStorage != ReplayStorage.Disk)
        {
            return;
        }

        // A restart's replacement still running here stops as session-ended: its own writer didn't fail.
        EndActive("disk full", failure: null, RecordingStopReasons.SessionEnded);
        _ringWait = target;
        var every = recordings.RingRecheck ?? Recordings.DefaultRingRecheck;
        _ringTimer = new Timer(_ => Post(new RingRecheck()), null, every, every);
        recordings.ReplayDiskFull?.Invoke(true);
        recordings.Info?.Invoke($"The disk filled during the Recording at {recording.Path}; Clips stopped and start again once the cache folder fits");
    }

    /// <summary>A recheck after a full disk: Rewind restarts on the Target once its ring fits, or the wait ends with the Target.</summary>
    private void RecheckRing(TargetCandidate wait)
    {
        if (_active is not null || _target is not { } target || target.ProcessId != wait.ProcessId)
        {
            ClearRingWait();
            return;
        }

        var snapshot = SettingsResolver.Resolve(_currentDocument(), target.ProfileId);
        if (snapshot.ReplayStorage != ReplayStorage.Disk || (_recordings?.RingFits?.Invoke(snapshot) ?? true))
        {
            ClearRingWait();
            StartOnTarget(target);
        }
    }

    /// <summary>Ends a wait for room for the ring, if any, and reports it.</summary>
    private void ClearRingWait()
    {
        if (_ringWait is null)
        {
            return;
        }

        _ringWait = null;
        _ringTimer?.Dispose();
        _ringTimer = null;
        _recordings?.ReplayDiskFull?.Invoke(false);
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
