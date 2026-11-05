using System.Diagnostics;
using System.Threading.Channels;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Hotkeys;
using ReplayBuffer.Core.Library;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Settings;

namespace ReplayBuffer.Core.Sessions;

/// <summary>A message to the orchestrator. Any thread may post one; they are handled one at a time, in arrival order.</summary>
public abstract record SessionMessage;

/// <summary>Starts a session; replies with it, or fails with <see cref="ErrorCodes.SessionRunning"/> or the factory's failure.</summary>
public sealed record StartSession(SessionKind Kind, CaptureTarget Target, SessionAudio Audio, TaskCompletionSource<RunningSession> Reply) : SessionMessage;

/// <summary>
/// Moves the running session to another window of its process; replies with the updated spec, or
/// fails with <see cref="ErrorCodes.SessionNotRunning"/> or <see cref="ErrorCodes.RetargetOtherProcess"/>.
/// </summary>
public sealed record RetargetSession(CaptureTarget Target, TaskCompletionSource<SessionSpec> Reply) : SessionMessage;

/// <summary>Ends the running session; replies with its outcome, or fails with <see cref="ErrorCodes.SessionNotRunning"/>.</summary>
public sealed record EndSession(string Reason, TaskCompletionSource<SessionOutcome> Reply) : SessionMessage;

/// <summary>
/// A registered Hotkey was pressed (AD-11); <paramref name="Qpc"/> was read when <c>WM_HOTKEY</c>
/// arrived (AD-6). <see cref="HotkeyAction.MicMute"/> toggles mic mute from that QPC; every press,
/// whatever its action, is then handed to the press callback.
/// </summary>
public sealed record HotkeyPressed(HotkeyAction Action, long Qpc) : SessionMessage;

/// <summary>
/// The PC is going to sleep (T-02); <paramref name="Qpc"/> was read when the notification arrived.
/// A running Recording or Monitor Recording stops there, and the game session suspends.
/// </summary>
public sealed record PowerSuspending(long Qpc) : SessionMessage;

/// <summary>The PC woke from sleep (T-02): a suspended game session rebuilds its capture into a new ring generation.</summary>
public sealed record PowerResumed : SessionMessage;

/// <summary>
/// The displays changed (T-03): a Monitor Recording whose monitor is no longer attached ends with
/// <see cref="RecordingStopReasons.MonitorRemoved"/>; a game session is left as it is.
/// </summary>
public sealed record DisplaysChanged : SessionMessage;

/// <summary>
/// The settings document was committed; any thread. The game session's settings are resolved again,
/// and a profile's new Tracks or video apply from its next Clip (R7, R8).
/// </summary>
public sealed record SettingsUpdated : SessionMessage;

/// <summary>
/// A started session: its spec as of Start, and a task that completes with its outcome however it
/// ends (an <c>End</c>, its window closing, a failure, or the orchestrator stopping).
/// </summary>
public sealed record RunningSession(SessionSpec Spec, Task<SessionOutcome> Ended);

/// <summary>
/// The single-threaded SessionOrchestrator (AD-19): the only component that starts, retargets and
/// ends sessions and publishes <see cref="CaptureState"/>. It runs on one dedicated thread reading a
/// single-reader channel; callers and media threads only post to it. It owns mic mute (AD-19): loaded
/// from its <see cref="IMicMuteStore"/> before any session can start (muted when nothing is saved),
/// toggled by each <see cref="HotkeyAction.MicMute"/> press, saved, published as
/// <see cref="CaptureState.MicMuted"/>, put in each session's spec at Start and handed to the running
/// session with the press QPC. With <see cref="ReplayClips"/>, a <see cref="HotkeyAction.Save"/> press
/// on the game session reserves a path and has the session write its Clip there; the writer posts
/// its result back, and only then is the Clip published as saved and the session's Replay Buffer
/// Reset at its end (R5); a press less than 1 s after that Reset is refused as too short. A press
/// within the Global <c>saveCooldown</c> of the last accepted one is ignored (R5); an accepted one
/// publishes <see cref="CaptureState.CooldownUntil"/> and <c>clip.saving</c> at once, and
/// <see cref="CaptureState.ClipsWriting"/> follows each write as it starts and ends; it is refused
/// with <see cref="ErrorCodes.SaveBusy"/> while <see cref="ReplayClips.MaxWriters"/> Clips are being
/// written (R6). A game
/// session whose Target exits, or whose window closes, is drained rather than ended (R1): it keeps
/// its Replay Buffer, <see cref="ReplayState.Draining"/> is published, and <c>save</c> presses still
/// cut from it until <see cref="ReplayClips.ExitDrain"/> passes, a new session starts, or the
/// orchestrator stops. A switch to another game, or Ignored, ends it at once (FR-38). With
/// <see cref="Recordings"/>, a Record press that starts (<c>recordToggle</c>, or <c>recordStart</c>
/// in separate mode, as <c>recordHotkeyMode</c> reads live) on a game session whose
/// profile has <c>recordingsEnabled</c> on (read live, R1) reserves a path beside the game's Clips
/// and has the session start a Recording there; a later Record press stops it, and the writer posts its
/// result back before <see cref="CaptureState.Recording"/> goes off. A session that ends mid-Recording
/// finishes it with everything encoded, stopped with the reason of what ended it: its Target's exit
/// or its window closing (<see cref="RecordingStopReasons.GameExit"/>), an Ignored handover
/// (<see cref="RecordingStopReasons.Ignored"/>), the orchestrator stopping
/// (<see cref="RecordingStopReasons.Shutdown"/>), else <see cref="RecordingStopReasons.SessionEnded"/>.
/// While a Recording runs, a switch of the Target to another game is held, and committed the moment
/// its writer posts back. On <see cref="Dispose"/>, a running Recording's writer is waited for (up to
/// <see cref="ShutdownWait"/>) and its result handled before the thread ends; one not done by then is
/// left open. A forced encoder restart of the session (<see cref="ISessionEvents.ChainRestarted"/>,
/// R4) ends its running Recording with <see cref="RecordingStopReasons.EncoderRestart"/> and starts
/// a new one at once on the new chain, in a new file; once the old one's writer posts back, it is
/// published as stopped, the new one as started, and <see cref="Recordings.Restarted"/> names both,
/// while <see cref="CaptureState.Recording"/> stays on throughout.
/// With <see cref="ReplayClips"/>, the game session's settings
/// are resolved again for its profile on each <see cref="SettingsUpdated"/> and after each Reset
/// (R7, R8): when they need another encoder chain (<see cref="ChainKey.SameChain"/>), the session is
/// asked to prepare it (<see cref="ICaptureSession.PrepareChain"/>), and a later change back drops
/// it. The save press whose write switches the session to it (<see cref="IClipWrite.SwitchesTo"/>)
/// updates the session's spec: its snapshot and audio, its Target kept. While a Recording runs on
/// the session, it keeps one chain (AD-7): a chain prepared before the Recording starts is dropped,
/// a change is not prepared but logged once through <see cref="Recordings.Info"/>, and when the
/// Recording's writer posts back with its session still running, the settings are resolved again,
/// so the first save after the stop switches to the new chain.
/// A Recording whose disk fills (R5) is stopped with <see cref="RecordingStopReasons.DiskFull"/> and
/// reported through <see cref="Recordings.DiskFull"/>; when its session's ring is on disk, the session
/// is ended too, and a timer posts a recheck every <see cref="Recordings.RingRecheck"/> until the ring
/// fits its drive again (<see cref="Recordings.RingFits"/>), when it restarts on the Target.
/// With <see cref="Recordings"/>, a Monitor Record press that starts (<c>monitorRecordToggle</c>, or
/// <c>monitorRecordStart</c> in separate mode, as <c>recordHotkeyMode</c> reads live) starts the
/// Monitor session (6.9) in a second slot beside the game session: a <see cref="SessionKind.Monitor"/>
/// session on the Global snapshot and Global Track Layout, capturing the monitor its snapshot names.
/// At its anchor (<see cref="ISessionEvents.Anchored"/>) a Recording starts on it in
/// <c>&lt;Output Folder&gt;\Monitor Recordings</c>; a later press stops it, and once its writer posts
/// back the Monitor session ends. <see cref="CaptureState.MonitorRecording"/> is on from the press
/// to then. It never touches the Target, and never holds a Target switch. Encoder chains are counted
/// (AD-7, <see cref="ICaptureSession.Chains"/>): a Monitor press is refused with
/// <see cref="ErrorCodes.EncoderBudget"/> (<see cref="Recordings.Warn"/>) when the game session still
/// runs two once a chain it prepared is dropped. While the Monitor session runs, a chain change of
/// the game session is not prepared in advance but switched at its next Reset, tearing the old chain
/// down first (<see cref="ICaptureSession.SwitchChain"/>); a refused switch is retried at the next Reset.
/// A session whose GPU device was lost (<see cref="ISessionEvents.DeviceLost"/>, a driver reset, R3)
/// is recovered here (<see cref="DeviceRecovery"/>): the first loss calls
/// <see cref="DeviceRecovery.Reset"/> and asks the session to rebuild at once
/// (<see cref="ICaptureSession.RecoverDevice"/>); each failed attempt is retried after
/// <see cref="DeviceRecovery.RetryEvery"/>, until <see cref="DeviceRecovery.GiveUpAfter"/> has passed
/// since the loss, when the session is ended and <see cref="DeviceRecovery.Failed"/> called. A given-up
/// game session stays off until the Target's process changes; a new window of it doesn't restart it.
/// A recovery posts <see cref="ISessionEvents.ChainRestarted"/>, which calls
/// <see cref="DeviceRecovery.Recovered"/> and hands a running Recording over as any forced encoder
/// restart does; a Monitor Recording is handed over the same way, its replacement published through
/// <see cref="Recordings.MonitorStarted"/>, while <see cref="CaptureState.MonitorRecording"/> stays on.
/// A sleep (<see cref="PowerSuspending"/>, from its <see cref="IPowerNotifications"/>, T-02) stops a
/// running Recording with <see cref="RecordingStopReasons.Suspend"/>, ends the Monitor session, raises
/// <see cref="ErrorCodes.RecordingSuspended"/> once when either ran, and suspends the game session
/// (<see cref="ICaptureSession.Suspend"/>), keeping its Replay Buffer saveable; the resume
/// (<see cref="PowerResumed"/>) drives the GPU reset's recovery, with its retries and give-up but no
/// <see cref="DeviceRecovery.Reset"/> or <see cref="DeviceRecovery.Recovered"/>, and no Recording
/// starts again.
/// A display change (<see cref="DisplaysChanged"/>, from its <see cref="IDisplayMonitors"/>, T-03)
/// ends a Monitor session whose monitor is gone, its Monitor Recording stopping at its last frame with
/// <see cref="RecordingStopReasons.MonitorRemoved"/> and <see cref="ErrorCodes.MonitorRemoved"/> raised;
/// a Monitor session that closes or fails while its monitor is gone ends the same way. Game capture is
/// of a window, so a display change never touches the game session.
/// With <see cref="TargetCapture.Wait"/>, the game session follows the Target's process, not one
/// window (<see cref="CaptureWait"/>): it starts only on a window of the process that can be captured
/// (not a startup placeholder, minimized or cloaked), polled for until there is one. A failure or a
/// window that closes before the first frame starts it again after a bounded backoff; after the
/// first frame, a window that closes or can no longer be captured is replaced by a retarget to the
/// process's next one, keeping the Replay Buffer. Only the Target's exit or change, Ignored, or a
/// GPU give-up ends it.
/// </summary>
public sealed class SessionOrchestrator : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for a running Recording's writer, inside the App's 5 s Quit.</summary>
    internal static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(3);

    /// <summary>AD-7's encoder budget: at most this many encoder chains run at once, across sessions.</summary>
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

    // The game session drained after its Target exited (R1), still holding its Replay Buffer, and
    // the one-shot timer that posts its expiry. Never set while a session is active.
    private Active? _drain;
    private Timer? _drainTimer;
    private TargetCandidate? _target;
    private ProposeTarget? _pendingTarget;

    // The Target whose Disk-mode Replay Buffer a full disk stopped (R5), and the periodic timer that
    // posts its rechecks; null when nothing waits.
    private TargetCandidate? _ringWait;
    private Timer? _ringTimer;

    // A run of waits for the Target's window (TargetCapture.Wait): the Target it waits on, the
    // one-shot timer posting its next try (null while a session on it runs), and how many sessions
    // on it failed before their first frame. Null when nothing waits.
    private TargetCandidate? _captureWait;
    private Timer? _captureTimer;
    private int _captureFailures;
    private CaptureState _state = CaptureState.Initial;
    private long _nextSessionId;

    // The Hotkey QPC of the last accepted save press, which the Save Cooldown runs from (R5); null
    // before the first. Global: a new session doesn't clear it.
    private long? _cooldownFrom;

    // Clip writes started and not yet posted back, across sessions (R6).
    private int _writing;

    // The Recording started and not yet posted back; null when none runs.
    private RecordingRun? _recording;

    // The Recording a forced encoder restart ended, until its writer posts back; null for none.
    private RecordingRun? _ending;

    // The Monitor session (6.9), from its press until its Monitor Recording's writer posts back; null for none.
    private Active? _monitor;

    // The Monitor Recording started at the Monitor session's anchor and not yet posted back; null for none.
    private RecordingRun? _monitorRecording;

    // The Monitor Recording a forced encoder restart ended, until its writer posts back; null for none.
    private RecordingRun? _monitorEnding;

    // The process of the Target whose game session gave up on a GPU reset (R3): no new window of it
    // restarts the Replay Buffer; null once the Target's process changes.
    private int? _gpuStoppedPid;

    private int _disposed;

    /// <param name="factory">Starts the capture sessions.</param>
    /// <param name="currentDocument">The current settings document, read once per Start for the snapshot (AD-5).</param>
    /// <param name="publish">
    /// Receives each changed <see cref="CaptureState"/>, on the orchestrator thread, once per change; a
    /// loaded mute that differs from <see cref="CaptureState.Initial"/> is published from here, before
    /// the thread starts, so it is in place before anyone can connect.
    /// </param>
    /// <param name="hotkeyPressed">Receives each <see cref="HotkeyPressed"/>, on the orchestrator thread, in arrival order.</param>
    /// <param name="micMute">
    /// Where mic mute is kept between runs, read once here; null keeps it in memory only, starting muted.
    /// </param>
    /// <param name="targetChanged">Receives each committed change of the Target Game, on the orchestrator thread.</param>
    /// <param name="targetCapture">When given, each new Target Game ends the game session and starts one on its window.</param>
    /// <param name="clips">When given, a <c>save</c> press on the game session writes a Clip.</param>
    /// <param name="recordings">When given, Record presses on the game session start and stop a Recording.</param>
    /// <param name="deviceRecovery">How a session whose GPU device was lost is recovered; null is the defaults, with no callbacks.</param>
    /// <param name="power">The PC's sleep and resume, posted as <see cref="PowerSuspending"/> and <see cref="PowerResumed"/>; null for none.</param>
    /// <param name="displays">The attached monitors, whose changes are posted as <see cref="DisplaysChanged"/>; null for none.</param>
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

    /// <summary>Starts a session, its snapshot resolved now for <paramref name="target"/>'s profile.</summary>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.SessionRunning"/>: a session is already running.</exception>
    public Task<RunningSession> StartAsync(SessionKind kind, CaptureTarget target, SessionAudio audio)
    {
        var reply = NewReply<RunningSession>();
        PostOrFail(new StartSession(kind, target, audio, reply), reply);
        return reply.Task;
    }

    /// <summary>Moves the running session to another window of the same process; the snapshot is kept.</summary>
    /// <exception cref="ErrorCodeException">
    /// <see cref="ErrorCodes.SessionNotRunning"/>, or <see cref="ErrorCodes.RetargetOtherProcess"/> when the window
    /// belongs to another process.
    /// </exception>
    public Task<SessionSpec> RetargetAsync(CaptureTarget target)
    {
        var reply = NewReply<SessionSpec>();
        PostOrFail(new RetargetSession(target, reply), reply);
        return reply.Task;
    }

    /// <summary>Ends the running session and returns its outcome.</summary>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.SessionNotRunning"/>.</exception>
    public Task<SessionOutcome> EndAsync(string reason = "ended")
    {
        var reply = NewReply<SessionOutcome>();
        PostOrFail(new EndSession(reason, reply), reply);
        return reply.Task;
    }

    /// <summary>Stops taking messages, handles those already posted, ends any running session, releases any drain, and joins the thread.</summary>
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

        if (_active is not null)
        {
            EndActive("engine stopping", failure: null, RecordingStopReasons.Shutdown);
        }

        if (_monitor is not null)
        {
            EndMonitor("engine stopping", RecordingStopReasons.Shutdown);
        }

        // The channel is complete, so the writers' post-backs can't arrive: wait for them here (one
        // bounded wait for all), a restart's ending Recording first.
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

            _recordings?.Info?.Invoke($"The Recording at {recording.Path} didn't finish within {ShutdownWait.TotalSeconds:0} s of shutdown; it was left open");
        }

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
                if (pressed.Action == HotkeyAction.MicMute)
                {
                    ToggleMicMute(pressed.Qpc);
                }
                else if (pressed.Action == HotkeyAction.Save)
                {
                    SaveClip(pressed.Qpc);
                }
                else if (pressed.Action is HotkeyAction.RecordToggle or HotkeyAction.RecordStart or HotkeyAction.RecordStop)
                {
                    OnRecordPress(pressed.Action, pressed.Qpc);
                }
                else if (pressed.Action is HotkeyAction.MonitorRecordToggle or HotkeyAction.MonitorRecordStart or HotkeyAction.MonitorRecordStop)
                {
                    OnMonitorPress(pressed.Action, pressed.Qpc);
                }

                _hotkeyPressed?.Invoke(new HotkeyPress(pressed.Action, pressed.Qpc));
                break;

            case ProposeTarget propose:
                CommitTarget(propose);
                break;

            case RecordingRunning recording:
                // dev.simulate's display state only: a real Recording's state wins, and it never holds the Target.
                if (_recording is null && _ending is null)
                {
                    SetState(_state with { Recording = recording.Running ? RecordingState.Running : RecordingState.Off });
                }

                break;

            case ClipWritten written:
                OnClipWritten(written);
                break;

            case SettingsUpdated:
                if (_active is { } active)
                {
                    ApplySettings(active);
                }

                break;

            case RecordingWritten recorded:
                // Not once Dispose has begun: a held Target must never start a session during Quit.
                OnRecordingWritten(recorded, commitPending: Volatile.Read(ref _disposed) == 0);
                break;

            case SessionClosed closed when _monitor?.Id == closed.SessionId:
                // WGC can see the monitor go before the display change arrives.
                if (!EndMonitorIfRemoved(_monitor))
                {
                    EndMonitor(closed.Reason, RecordingStopReasons.SessionEnded);
                }

                break;

            case SessionFailed failed when _monitor?.Id == failed.SessionId:
                if (!EndMonitorIfRemoved(_monitor))
                {
                    _recordings?.Failed(failed.Failure);
                    EndMonitor("failure: " + failed.Failure.Message, RecordingStopReasons.SessionEnded);
                }

                break;

            case SessionAnchored anchored when _monitor?.Id == anchored.SessionId:
                OnMonitorAnchored(_monitor, anchored.Qpc);
                break;

            case SessionDeviceLost lost when _monitor?.Id == lost.SessionId:
                OnDeviceLost(_monitor, lost.Failure, monitor: true);
                break;

            case RetryDeviceRecovery retry when _monitor?.Id == retry.SessionId:
                TryRecover(_monitor, monitor: true);
                break;

            case SessionChainRestarted restarted when _monitor?.Id == restarted.SessionId:
                OnRecovered(_monitor);
                OnMonitorChainRestarted(_monitor, restarted.Qpc);
                break;

            case SessionClosed closed when _active?.Id == closed.SessionId:
                // A game whose process still runs is followed to its next window (TargetCapture.Wait).
                if (Follows(_active))
                {
                    OnWindowClosed(_active);
                }

                // WGC can see the window close before Game Detection sees the exit: drain here too.
                else if (Drains(_active))
                {
                    DrainActive(closed.Reason, RecordingStopReasons.GameExit);
                }
                else
                {
                    EndActive(closed.Reason, failure: null, RecordingStopReasons.GameExit);
                }

                break;

            case DrainExpired expired when _drain?.Id == expired.SessionId:
                ReleaseDrain();
                break;

            case SessionFailed failed when _active?.Id == failed.SessionId:
                OnActiveFailed(_active, failed.Failure);
                break;

            case SessionAnchored anchored when _active?.Id == anchored.SessionId:
                OnActiveAnchored(_active);
                break;

            case CaptureRetry when _captureWait is { } waiting:
                RetryCapture(waiting);
                break;

            case FollowCheck check when _active?.Id == check.SessionId:
                if (Follows(_active))
                {
                    FollowWindow(_active, _targetCapture!.Wait!, _active.Spec.Target.Window);
                }

                break;

            case SessionChainRestarted restarted when _active?.Id == restarted.SessionId:
                OnRecovered(_active);
                OnChainRestarted(_active, restarted.Qpc);
                break;

            case SessionDeviceLost lost when _active?.Id == lost.SessionId:
                OnDeviceLost(_active, lost.Failure, monitor: false);
                break;

            case RetryDeviceRecovery retry when _active?.Id == retry.SessionId:
                TryRecover(_active, monitor: false);
                break;

            case RingRecheck when _ringWait is { } wait:
                RecheckRing(wait);
                break;

            case PowerSuspending suspending:
                OnSuspend(suspending.Qpc);
                break;

            case PowerResumed:
                OnResume();
                break;

            case DisplaysChanged:
                if (_monitor is { } changedMonitor)
                {
                    EndMonitorIfRemoved(changedMonitor);
                }

                break;

            default:
                // A post-back from a session that has already ended.
                break;
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

    /// <param name="audio">The session's audio, from the snapshot resolved here.</param>
    /// <param name="gameName">The Target's display name, kept for the session's Clips; null when not started on the Target.</param>
    private RunningSession Start(SessionKind kind, CaptureTarget target, Func<EffectiveSettings, SessionAudio> audio, string? gameName = null)
    {
        if (_active is not null)
        {
            throw new ErrorCodeException(ErrorCodes.SessionRunning, "A session is already running.");
        }

        // A drained ring never outlives the start of a new session, nor does a wait for room for one.
        ReleaseDrain();
        ClearRingWait();

        // AD-5: resolved here; a retarget never touches it, and a later settings change reaches a
        // game session only through a chain switch at a save (ApplySettings).
        var snapshot = SettingsResolver.Resolve(_currentDocument(), target.ProfileId);
        var spec = new SessionSpec(kind, target, snapshot, audio(snapshot), _state.MicMuted);
        var id = ++_nextSessionId;
        var session = _factory.Start(spec, new Events(this, id));
        var active = new Active(id, session, spec, audio) { GameName = gameName };
        _active = active;
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

    /// <summary>Whether <paramref name="active"/> is drained, not ended, when its Target goes (R1): a game session with Replay Clips.</summary>
    private bool Drains(Active? active) => _clips is not null && active is { Spec.Kind: SessionKind.Game };

    /// <summary>
    /// Ends the active game session's capture but keeps its Replay Buffer for <c>save</c> presses
    /// (R1): it becomes the drain, <see cref="ReplayState.Draining"/> is published, and a timer posts
    /// <see cref="DrainExpired"/>, tagged with its id, after <see cref="ReplayClips.ExitDrain"/>.
    /// </summary>
    /// <param name="recordingReason">The stop reason of a Recording running on the session.</param>
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

    /// <summary>Frees the drained session's Replay Buffer, if any; <see cref="ReplayState.Off"/> unless a session is active.</summary>
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
            // Releasing is best effort: a save in flight keeps its own lease either way (AD-8).
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

    /// <summary>
    /// Flips mic mute: the running session first, so as few frames as possible pass unchanged after
    /// <paramref name="qpc"/>, then the store, then the published state. A failure of the session or
    /// the store is swallowed: the new state stands either way.
    /// </summary>
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

    /// <summary>
    /// Commits a proposed Target Game (AD-19). Another process, or none, becomes the Target and is
    /// published; a new window of the Target's own process only moves the debug capture to it. While
    /// a Recording runs, until its writer posts back, a switch from the Target to another game is held
    /// as pending, each later one replacing it; a proposal of the Target itself drops it. An exit or
    /// Ignored handover is never held: the Target is gone, or must never be captured (AD-5); the
    /// Recording stops with <see cref="RecordingStopReasons.GameExit"/> or
    /// <see cref="RecordingStopReasons.Ignored"/>.
    /// </summary>
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

        // A Replay Buffer given up on a GPU reset stays off only for the Target it stopped on.
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
            // Only an exit with no other game drains (R1); a switch or Ignored ends at once (FR-38).
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

    /// <summary>
    /// Detection named a new window of the Target's process. With no session, the game session
    /// starts; a running one moves to it, or, with <see cref="TargetCapture.Wait"/>, to the window
    /// <see cref="CaptureWait.Find"/> names, preferring this one.
    /// </summary>
    private void FollowTargetWindow(TargetCandidate target)
    {
        if (_targetCapture is null)
        {
            return;
        }

        if (_active is null)
        {
            // A Replay Buffer a full disk stopped waits for its recheck; one a GPU reset stopped, for the next Target.
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

    /// <summary>
    /// Starts the game session on the Target. With <see cref="TargetCapture.Wait"/>, on the window of
    /// its process <see cref="CaptureWait.Find"/> names; with none it waits, and looks again after
    /// <see cref="CaptureWait.Poll"/>. A start that fails with a failure the wait retries is retried
    /// after its backoff.
    /// </summary>
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

    /// <summary>
    /// Whether the game session <paramref name="active"/> follows the Target's process: the capture
    /// waits, it was started on the Target, and that is still its process.
    /// </summary>
    private bool Follows(Active active) =>
        _targetCapture?.Wait is not null
        && Volatile.Read(ref _disposed) == 0
        && active is { Spec.Kind: SessionKind.Game, GameName: not null }
        && _target is { } target
        && target.ProcessId == active.Spec.Target.ProcessId;

    /// <summary>
    /// The game session failed. One that follows the Target's process, with a failure
    /// <see cref="TargetCapture.Wait"/> retries, is started again after its backoff instead of
    /// staying off for the rest of the Target's run.
    /// </summary>
    private void OnActiveFailed(Active active, Exception failure)
    {
        var retry = Follows(active) && _targetCapture!.Wait!.Retry(failure);
        EndActive("failure: " + failure.Message, failure);
        if (retry)
        {
            WaitForCapture(_targetCapture!.Wait!, _target!, failure.Message, failed: true);
        }
    }

    /// <summary>
    /// The window of a game session that follows the Target's process closed. Before its first frame
    /// the session ends and another starts after a backoff; after it the session keeps its Replay
    /// Buffer and moves to the process's next window, or waits for one.
    /// </summary>
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

    /// <summary>The game session got its first frame: a wait for its Target's window is over, and its window is watched from here.</summary>
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

    /// <summary>
    /// Keeps the game session on a window of the Target's process it can capture: its window while it
    /// can be, else the one <see cref="CaptureWait.Find"/> names (preferring <paramref name="prefer"/>),
    /// retargeted to so its Replay Buffer is kept. With none, it waits, its last frame repeated; a
    /// failed retarget is tried again after its backoff. A session whose GPU device is lost is left to
    /// its recovery, which captures the window it was last given.
    /// </summary>
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
            // No capture runs now, but the session and its Replay Buffer go on: try again later.
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

    /// <summary>Starts a run of waits for <paramref name="target"/>'s window, reported once; a run for another process ends first.</summary>
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

    /// <summary>
    /// Waits for <paramref name="target"/>'s window with no session: the run's first wait is reported,
    /// and the next try is posted after <see cref="CaptureWait.Poll"/>, or after the backoff for a failure.
    /// </summary>
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
            // A full disk or a given-up GPU reset holds the Replay Buffer off; their own waits restart it.
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

    /// <summary>
    /// A <c>save</c> press (R4): with a game session holding a Replay Buffer, reserves the Clip's
    /// path (AD-21) in the Global Output Folder, else the default one, and hands the session the
    /// write; the writer posts its result back as a <see cref="ClipWritten"/>. Ignored with no game
    /// session, or one with nothing to save yet. A press less than 1 s after the session's last
    /// Reset (R5) is refused here, before the session is asked for anything: no keyframe, no Clip,
    /// no Reset, only <see cref="ReplayClips.NotSaved"/> with <see cref="ErrorCodes.ClipTooShort"/>.
    /// With no game session, a drained one (R1) takes the press and cuts to the end of what it holds.
    /// Before any of that, the Save Cooldown (R5): a press less than the Global <c>saveCooldown</c>
    /// after the last accepted one is ignored, with only <see cref="ReplayClips.Ignored"/>. Any other
    /// press is accepted: it starts the next cooldown, publishes <see cref="CaptureState.CooldownUntil"/>
    /// and calls <see cref="ReplayClips.Saving"/>, and an outcome always follows. A write that starts
    /// publishes <see cref="CaptureState.ClipsWriting"/>. With
    /// <see cref="ReplayClips.MaxWriters"/> writes in flight it is refused with
    /// <see cref="ErrorCodes.SaveBusy"/> (R6), before the session is asked for anything. Once the
    /// path is reserved, <see cref="ReplayClips.Library"/> is told the write begins, and
    /// <see cref="OnClipWritten"/> (or a write that never started) tells it how it ended.
    /// </summary>
    private void SaveClip(long qpc)
    {
        var active = _active is { Spec.Kind: SessionKind.Game } game ? game : _drain;
        if (_clips is null || active is null)
        {
            return;
        }

        // R5: read at each press (Global, Immediate); measured in Hotkey QPC from the accepted press.
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
            // R6: the ring keeps capturing; nothing is cut and nothing is Reset.
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
        library.BeginWrite(path, active.Spec.Target.ProfileId);
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

    /// <summary>
    /// When <paramref name="write"/>'s press switched the session's encoder chain (4.9), the spec
    /// takes the prepared one's snapshot and audio, its Target kept: the session's later Clips have
    /// its Tracks and video, whatever this write's outcome.
    /// </summary>
    private static void TakeSwitch(Active active, IClipWrite write)
    {
        if (write.SwitchesTo is { } switched)
        {
            active.Spec = active.Spec with { Snapshot = switched.Snapshot, Audio = switched.Audio };
            active.Pending = false;
        }
    }

    /// <summary>
    /// A save that made no Clip: a failure that isn't a refusal (<see cref="ErrorCodes.ClipTooShort"/>,
    /// <see cref="ErrorCodes.SaveBusy"/>) goes to <see cref="ReplayClips.Failed"/> for logging, and
    /// every one to <see cref="ReplayClips.NotSaved"/> with <see cref="WriteFailures.ReasonOf"/>.
    /// </summary>
    private void ReportSaveFailure(Exception e)
    {
        if (e is not ErrorCodeException { Code: ErrorCodes.ClipTooShort or ErrorCodes.SaveBusy })
        {
            _clips?.Failed(e);
        }

        _clips?.NotSaved?.Invoke(new ClipFailed(WriteFailures.ReasonOf(e)));
    }

    /// <summary>
    /// The Target's name the session was started on (kept after the Target exits), else the Target's
    /// when the session is on it, else the profile's executable name.
    /// </summary>
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

    /// <summary>
    /// A writer's result. A Clip written for the still-running session Resets its Replay Buffer
    /// (R5, AD-19) at the Clip's end, and its press starts the 1 s a next press must wait; a failed
    /// write Resets nothing, so the next Clip still starts at the last Reset.
    /// </summary>
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

        // The session still running, or the drained one (R1): either still holds its Replay Buffer.
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

            // Each Reset resolves the settings again, so a failed in-advance build is asked for once
            // more, and a change held while the Monitor session runs switches here.
            if (ReferenceEquals(active, _active))
            {
                ApplySettings(active, atReset: true);
            }
        }

        _clips?.Saved(new ClipSaved(written.Path));
    }

    /// <summary>
    /// Resolves the game session's settings again for its profile (R7, R8). When they need another
    /// encoder chain than the session's (<see cref="ChainKey.SameChain"/>), the session is asked to
    /// prepare it for its next save press; when they no longer do, a chain asked for is dropped.
    /// Only for a game session with Replay Clips; a Track Layout that can't be resolved is skipped,
    /// and a failure of the session is swallowed, as for mic mute. While the Monitor session runs,
    /// nothing is prepared in advance (AD-7: it holds a chain of its own); a chain prepared is
    /// dropped, and only <paramref name="atReset"/>, just after a Reset, is the session switched,
    /// tearing down first (<see cref="ICaptureSession.SwitchChain"/>); its spec then takes the new
    /// snapshot and audio. A refused switch waits for the next Reset.
    /// </summary>
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
                // AD-7: a Recording keeps the session on one chain until its writer posts back.
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

    /// <summary>
    /// A Record press (R1), in <c>recordHotkeyMode</c> read live from the current document, so a
    /// committed mode change applies at the next press. An action the mode doesn't use is ignored.
    /// <c>recordToggle</c> stops the running Recording, else starts one; <c>recordStart</c> starts
    /// one only while none runs, and <c>recordStop</c> stops only a running one. A stop isn't gated
    /// by <c>recordingsEnabled</c>, so a Recording switched off keeps running until it is stopped.
    /// A press while a Recording is stopping is ignored.
    /// </summary>
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

    /// <summary>
    /// Starts a Recording on the game session (R1, R2) when its profile's <c>recordingsEnabled</c>
    /// is on, read live from the current document. One snapshot resolved for the profile (AD-5)
    /// gives the Output Folder, else the default one, where the path is reserved (AD-21) beside the
    /// game's Clips, and the chain the profile asks for: a mismatch with the session's still shares
    /// the session's chain, and <see cref="Recordings.Info"/> says so. Once the path is reserved,
    /// <see cref="Recordings.Library"/> is told the write begins, then <see cref="Recordings.Open"/>
    /// keeps the path, and <see cref="OnRecordingWritten"/> (or a write that never started) tells the
    /// Library how it ended, then lets the path go. No game session, a drain only, no
    /// profile, the flag off, or a session that can't record yet: nothing happens but the Info.
    /// </summary>
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
            recordings.Info?.Invoke("The Record key was pressed before ReplayBuffer knew the game; no Recording was started");
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

    /// <summary>
    /// <see cref="StartRecording"/>'s start once the press is allowed, shared with a forced encoder
    /// restart's new Recording: the session's Recording from <paramref name="qpc"/> at a path
    /// reserved now, which becomes <see cref="_recording"/>; null, with the Info or the failure
    /// reported, when none started. Nothing is published here.
    /// </summary>
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

            // AD-5: one snapshot for the press; the Recording shares the session's chain whatever it asks for.
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

        // A chain prepared before the press is dropped (AD-7), even when the settings no longer
        // resolve; the change waits for the Recording's end.
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

    /// <summary>
    /// The session's encoder chain was forcibly rebuilt at <paramref name="qpc"/> (R4): a Recording
    /// running on it has ended at the old chain's last packet. It becomes <see cref="_ending"/>,
    /// stopped with <see cref="RecordingStopReasons.EncoderRestart"/>, and a new one starts at once
    /// on the new chain, in a new file, without reading <c>recordingsEnabled</c> again (it continues
    /// the running one). Nothing is published until the old one's writer posts back. With no
    /// Recording, or one already stopping, nothing happens; the Replay Buffer goes on by itself.
    /// </summary>
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

    /// <summary>
    /// A session ending under its running Recording stops it with <paramref name="reason"/>, one of
    /// <see cref="RecordingStopReasons"/>; the writer finishes as the output ends.
    /// </summary>
    private void MarkSessionEnded(Active active, string reason)
    {
        if (_recording is { Reason: null } running && running.SessionId == active.Id)
        {
            running.Reason = reason;
        }
    }

    /// <summary>
    /// A Recording writer's result: the Library is told how it ended (written while its file holds
    /// data), the Recording is published as stopped, with its stop reason or, when the writer
    /// failed, <see cref="WriteFailures.ReasonOf"/>, and <see cref="CaptureState.Recording"/> goes off
    /// once no Recording is left. A restart's ended Recording (<see cref="_ending"/>) then announces
    /// its replacement; a replacement that posts back before it is announced first. Then, with
    /// <paramref name="commitPending"/> and none left, a Target switch held while it ran is
    /// committed. A duplicate (the shutdown wait and the post-back both) is ignored.
    /// </summary>
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

        // Its session still running, and not about to end for a held Target switch: a change deferred
        // while it ran is prepared now, so the next save switches to it.
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

    /// <summary>
    /// A Monitor Record press (6.9), in <c>recordHotkeyMode</c> read live, exactly as a Record press:
    /// an action the mode doesn't use is ignored; <c>monitorRecordToggle</c> stops the running
    /// Monitor Recording, else starts one; <c>monitorRecordStart</c> starts one only while none runs,
    /// and <c>monitorRecordStop</c> stops only a running one. A press while one is stopping is ignored.
    /// A stop before the Monitor session anchored ends it at once, with no Recording.
    /// </summary>
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

    /// <summary>
    /// Starts the Monitor session (6.9) on the Global snapshot (AD-5) and its Track Layout, unless the
    /// encoder budget (AD-7) is spent: a chain the game session prepared is dropped first, and when
    /// the game session still runs <see cref="MaxChains"/>, the press is refused with
    /// <see cref="ErrorCodes.EncoderBudget"/>. <see cref="CaptureState.MonitorRecording"/> goes on;
    /// the Recording starts at the session's anchor.
    /// </summary>
    private void StartMonitor(SettingsDocument document)
    {
        var recordings = _recordings!;
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
                ApplySettings(game);
                return;
            }
        }

        Active monitor;
        try
        {
            var snapshot = SettingsResolver.Resolve(document, null);
            var spec = new SessionSpec(SessionKind.Monitor, new CaptureTarget(0, 0, null), snapshot, MonitorAudio(snapshot), _state.MicMuted);
            var id = ++_nextSessionId;
            var session = _factory.Start(spec, new Events(this, id));
            monitor = new Active(id, session, spec, MonitorAudio);
        }
        catch (Exception e)
        {
            recordings.Failed(e);
            return;
        }

        _monitor = monitor;
        SetState(_state with { MonitorRecording = RecordingState.Running });
    }

    /// <summary>The Monitor session's audio: the Global Track Layout's.</summary>
    private static SessionAudio MonitorAudio(EffectiveSettings snapshot) => SessionAudio.FromLayout(snapshot.TrackLayout);

    /// <summary>
    /// The Monitor session's first frame, at <paramref name="qpc"/>: its Recording starts there
    /// (<see cref="BeginMonitorRecording"/>) and is published as started. One that can't start ends
    /// the Monitor session.
    /// </summary>
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

    /// <summary>
    /// A Monitor Recording from <paramref name="qpc"/> on the Monitor session, at a path reserved now
    /// in <c>&lt;Output Folder&gt;\Monitor Recordings</c> (AD-21), with the Library and
    /// <see cref="Recordings.Open"/> told as for a game Recording; it becomes
    /// <see cref="_monitorRecording"/>. Null, with the Info or the failure reported, when none
    /// started. Nothing is published here, and the session is left running.
    /// </summary>
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

    /// <summary>
    /// The Monitor session's encoder chain was forcibly rebuilt at <paramref name="qpc"/> (a GPU
    /// reset's recovery), as <see cref="OnChainRestarted"/> for the game session: its running Monitor
    /// Recording becomes <see cref="_monitorEnding"/>, stopped with
    /// <see cref="RecordingStopReasons.EncoderRestart"/>, and a new one starts at once in a new file.
    /// Nothing is published until the old one's writer posts back; when no new one could start, the
    /// Monitor session ends then.
    /// </summary>
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

    /// <summary>
    /// A session's GPU device was lost, or an attempt to recover it failed (R3, AD-19). The first
    /// loss is reported through <see cref="DeviceRecovery.Reset"/> and recovered at once; after a
    /// failed attempt, the next starts after <see cref="DeviceRecovery.RetryEvery"/>, unless
    /// <see cref="DeviceRecovery.GiveUpAfter"/> has passed since the loss, when the session is given up.
    /// </summary>
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
                : $"The graphics driver reset; ReplayBuffer is restarting its capture ({failure.Message})");
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

    /// <summary>
    /// Asks a lost session for one recovery attempt (<see cref="ICaptureSession.RecoverDevice"/>). A
    /// throw counts as a failed attempt; a refusal (one still running, say) is asked again after
    /// <see cref="DeviceRecovery.RetryEvery"/>, and past <see cref="DeviceRecovery.GiveUpAfter"/> the
    /// session is given up. Nothing happens once the session has recovered.
    /// </summary>
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

    /// <summary>
    /// Recovery took too long: the game session ends with <see cref="ErrorCodes.GpuResetFailed"/>, and
    /// stays off until the Target's process changes; the Monitor session just ends. Then
    /// <see cref="DeviceRecovery.Failed"/>.
    /// </summary>
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
                ? $"ReplayBuffer couldn't restart capture within {after:0} s of the PC waking; Clips are off until your next game ({failure.Message})"
                : $"ReplayBuffer couldn't recover from a graphics driver reset within {after:0} s; Clips are off until your next game ({failure.Message})";
            _gpuStoppedPid = session.Spec.Target.ProcessId;
            EndActive("gpu reset failed", new ErrorCodeException(ErrorCodes.GpuResetFailed, message, failure));
        }

        _deviceRecovery.Failed?.Invoke(message);
    }

    /// <summary>A session's chain restarted: when it was a GPU reset's recovery, the wait ends and <see cref="DeviceRecovery.Recovered"/> is told how long it took.</summary>
    private void OnRecovered(Active session)
    {
        if (session.LostAt is not { } lostAt)
        {
            return;
        }

        StopRecovery(session);

        // One that installed just as the PC went to sleep is torn down by the suspend: the session stays suspended, and the resume rebuilds.
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

    /// <summary>
    /// The PC is going to sleep (T-02, AD-19): a running Recording stops at <paramref name="qpc"/>
    /// with <see cref="RecordingStopReasons.Suspend"/>, the Monitor session ends, and when either ran
    /// <see cref="ErrorCodes.RecordingSuspended"/> is raised once. Then the game session stops its
    /// recovery timer, if any (it stays lost), and suspends, keeping its Replay Buffer saveable. A
    /// session that can't suspend is left as it is.
    /// </summary>
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

    /// <summary>
    /// The PC woke (T-02): a suspended game session rebuilds as after a GPU reset, from now, with
    /// its retries and give-up. A second resume, or one with nothing suspended, is ignored.
    /// </summary>
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

    /// <summary>
    /// Ends the Monitor session when the monitor it captures is no longer attached (T-03): its Monitor
    /// Recording stops at its last frame with <see cref="RecordingStopReasons.MonitorRemoved"/>, its file
    /// kept, and <see cref="ErrorCodes.MonitorRemoved"/> is raised. False, with nothing done, when the
    /// monitor is still attached, the session's monitor is unknown, or no display source was given.
    /// </summary>
    private bool EndMonitorIfRemoved(Active monitor)
    {
        if (_displays is not { } displays)
        {
            return false;
        }

        string? device;
        IReadOnlyList<DisplayMonitor> attached;
        try
        {
            device = monitor.Session.MonitorDevice;
            attached = displays.List();
        }
        catch (Exception e)
        {
            _recordings?.Info?.Invoke($"The monitors couldn't be read after a display change: {e.Message}");
            return false;
        }

        // An empty list is a read that failed mid-change, not every monitor gone.
        if (string.IsNullOrEmpty(device) || attached.Count == 0
            || attached.Any(m => string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        EndMonitor("monitor removed", RecordingStopReasons.MonitorRemoved);
        _recordings?.Warn?.Invoke(
            ErrorCodes.MonitorRemoved,
            $"Monitor {device} was removed; the Monitor Recording ended at its last frame and its file was kept");
        return true;
    }

    /// <summary>Ends a session's recovery wait, if any: no retry is posted after this.</summary>
    private static void StopRecovery(Active session)
    {
        session.LostAt = null;
        session.Retry?.Dispose();
        session.Retry = null;
    }

    /// <summary>
    /// Ends the Monitor session: a Monitor Recording running on it stops with
    /// <paramref name="recordingReason"/>, its writer finishing as the output ends; with none left,
    /// <see cref="CaptureState.MonitorRecording"/> goes off. The game session's settings are then
    /// resolved again, so a chain change held while it ran is prepared in advance.
    /// </summary>
    private void EndMonitor(string reason, string recordingReason)
    {
        var monitor = _monitor!;
        _monitor = null;
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

    /// <summary>
    /// A Monitor Recording writer's result: the Library and <see cref="Recordings.Open"/> are told as
    /// for a game Recording, <see cref="CaptureState.MonitorRecording"/> goes off, it is published as
    /// stopped (<see cref="Recordings.MonitorStopped"/>), a full disk is reported
    /// (<see cref="Recordings.DiskFull"/>), and the Monitor session ends. Nothing of the game
    /// session's Recording, Target or ring is touched. A duplicate is ignored.
    /// </summary>
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

    /// <summary>
    /// Begins <paramref name="recording"/>'s write in the Library and the open-Recordings record and
    /// starts its writer, which posts <see cref="RecordingWritten"/> when it finishes. Returns null,
    /// the failure reported and the reserved file removed, when the writer doesn't start.
    /// </summary>
    private RecordingRun? OpenWrite(Recordings recordings, RecordingRun recording, string? profileId)
    {
        var path = recording.Path;
        var library = recordings.Library ?? NoLibraryWrites.Instance;
        library.BeginWrite(path, profileId);
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

    /// <summary>
    /// Ends a finished Recording's write in the Library and the open-Recordings record, reports a
    /// writer failure, and returns the stop's reason: the failure's, else the one it was stopped with.
    /// A failed writer deletes a file that holds nothing; one with data is kept, finished as far as it got.
    /// </summary>
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

    /// <summary>
    /// A Recording stopped on a full disk (R5): reported, and when its session is still running on the
    /// Target with its ring on disk, the session ends and waits, rechecked every
    /// <see cref="Recordings.RingRecheck"/>, for room for the ring.
    /// </summary>
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

    /// <summary>A recheck after a full disk: the Replay Buffer restarts on the Target once its ring fits, or the wait ends when the Target has gone.</summary>
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

    private sealed class Active(long id, ICaptureSession session, SessionSpec spec, Func<EffectiveSettings, SessionAudio> audio)
    {
        public long Id { get; } = id;

        public ICaptureSession Session { get; } = session;

        /// <summary>The session's spec: its Target follows a retarget, its snapshot and audio a chain switch.</summary>
        public SessionSpec Spec { get; set; } = spec;

        /// <summary>The session's audio from a snapshot, as at Start.</summary>
        public Func<EffectiveSettings, SessionAudio> Audio { get; } = audio;

        /// <summary>Whether the session was last asked to prepare a chain, not to drop one.</summary>
        public bool Pending { get; set; }

        /// <summary>Whether a chain change was deferred, and logged, during the running Recording.</summary>
        public bool Deferred { get; set; }

        /// <summary>The Target's display name at <see cref="StartOnTarget"/>; null for a session started otherwise.</summary>
        public string? GameName { get; init; }

        /// <summary>Whether its output anchored at its first frame.</summary>
        public bool Anchored { get; set; }

        /// <summary>The periodic timer posting its window checks once it anchored on a Target it follows; null for none.</summary>
        public Timer? Follow { get; set; }

        /// <summary>Whether it has no capture: its window closed, or a retarget failed, and no retarget has succeeded since.</summary>
        public bool WindowClosed { get; set; }

        /// <summary>Its failed retargets in a row, for the backoff.</summary>
        public int FollowFailures { get; set; }

        /// <summary>The <see cref="Stopwatch"/> timestamp before which no retarget is tried; 0 for none.</summary>
        public long FollowAfter { get; set; }

        /// <summary>The press QPC of the session's last Reset (R5); null before its first Clip.</summary>
        public long? ResetQpc { get; set; }

        /// <summary>The <see cref="Stopwatch"/> timestamp its GPU device was lost at; null while it isn't lost.</summary>
        public long? LostAt { get; set; }

        /// <summary>The one-shot timer posting its next recovery attempt; null for none.</summary>
        public Timer? Retry { get; set; }

        /// <summary>Whether it was suspended for sleep (T-02) and hasn't been asked to resume yet.</summary>
        public bool Suspended { get; set; }

        /// <summary>Whether its recovery is a resume's, not a GPU reset's, until it recovers or gives up.</summary>
        public bool Resuming { get; set; }

        /// <summary>Whether <see cref="DeviceRecovery.Reset"/> was raised for its current outage and not yet answered by <see cref="DeviceRecovery.Recovered"/>.</summary>
        public bool ResetRaised { get; set; }

        public TaskCompletionSource<SessionOutcome> Ended { get; } = NewReply<SessionOutcome>();
    }

    private sealed record SessionClosed(long SessionId, string Reason) : SessionMessage;

    /// <summary>A Recording started on session <paramref name="sessionId"/>; orchestrator thread only.</summary>
    private sealed class RecordingRun(long sessionId, string path, IRecordingWrite write)
    {
        public long SessionId { get; } = sessionId;

        public string Path { get; } = path;

        public IRecordingWrite Write { get; } = write;

        /// <summary>Why it is stopping (<see cref="RecordingStopReasons"/>); null while it runs.</summary>
        public string? Reason { get; set; }

        /// <summary>Completed by the writer's callback, from its thread, with its error, before it posts back.</summary>
        public TaskCompletionSource<Exception?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The Recording a forced encoder restart ended and this one replaced, until this one is announced.</summary>
        public RecordingRun? Follows { get; set; }

        /// <summary>Whether it is the Monitor session's Monitor Recording, not the game session's.</summary>
        public bool Monitor { get; init; }
    }

    /// <summary>A Recording writer's result, posted from its thread.</summary>
    private sealed record RecordingWritten(RecordingRun Recording, Exception? Error) : SessionMessage;

    /// <summary>A Clip writer's result, posted from its thread, with the session, press and write it came from.</summary>
    private sealed record ClipWritten(long SessionId, long Qpc, IClipWrite Write, string Path, Exception? Error) : SessionMessage;

    private sealed record SessionFailed(long SessionId, Exception Failure) : SessionMessage;

    /// <summary>Session <paramref name="SessionId"/>'s output anchored at its first frame, at <paramref name="Qpc"/>.</summary>
    private sealed record SessionAnchored(long SessionId, long Qpc) : SessionMessage;

    /// <summary>Session <paramref name="SessionId"/>'s encoder chain was forcibly rebuilt; its frames from <paramref name="Qpc"/> on are the new chain's.</summary>
    private sealed record SessionChainRestarted(long SessionId, long Qpc) : SessionMessage;

    /// <summary>Session <paramref name="SessionId"/>'s GPU device was lost, or an attempt to recover it failed.</summary>
    private sealed record SessionDeviceLost(long SessionId, Exception Failure) : SessionMessage;

    /// <summary>The recovery retry timer of session <paramref name="SessionId"/> fired; ignored once that session is gone or recovered.</summary>
    private sealed record RetryDeviceRecovery(long SessionId) : SessionMessage;

    /// <summary>The ring recheck timer fired; ignored once no Replay Buffer waits for room.</summary>
    private sealed record RingRecheck : SessionMessage;

    /// <summary>The capture wait's timer fired; ignored once nothing waits for the Target's window.</summary>
    private sealed record CaptureRetry : SessionMessage;

    /// <summary>The window watch of session <paramref name="SessionId"/> fired; ignored once that session is gone.</summary>
    private sealed record FollowCheck(long SessionId) : SessionMessage;

    /// <summary>The drain timer of session <paramref name="SessionId"/> fired; ignored once that drain is gone.</summary>
    private sealed record DrainExpired(long SessionId) : SessionMessage;

    /// <summary>One session's post-backs, tagged with its id so a late one from an ended session is ignored.</summary>
    private sealed class Events(SessionOrchestrator owner, long id) : ISessionEvents
    {
        public void Closed(string reason) => owner.Post(new SessionClosed(id, reason));

        public void Failed(Exception failure) => owner.Post(new SessionFailed(id, failure));

        public void ChainRestarted(long qpc) => owner.Post(new SessionChainRestarted(id, qpc));

        public void Anchored(long qpc) => owner.Post(new SessionAnchored(id, qpc));

        public void DeviceLost(Exception failure) => owner.Post(new SessionDeviceLost(id, failure));
    }
}
