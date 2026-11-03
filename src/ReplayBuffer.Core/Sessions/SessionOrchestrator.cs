using System.Diagnostics;
using System.Threading.Channels;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
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
/// <see cref="Recordings"/>, a <see cref="HotkeyAction.RecordToggle"/> press on a game session whose
/// profile has <c>recordingsEnabled</c> on (read live, R1) reserves a path beside the game's Clips
/// and has the session start a Recording there; the next press stops it, and the writer posts its
/// result back before <see cref="CaptureState.Recording"/> goes off. A session that ends mid-Recording
/// finishes it with everything encoded.
/// </summary>
public sealed class SessionOrchestrator : IDisposable
{
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
    private readonly Thread _thread;

    // Orchestrator thread only.
    private Active? _active;

    // The game session drained after its Target exited (R1), still holding its Replay Buffer, and
    // the one-shot timer that posts its expiry. Never set while a session is active.
    private Active? _drain;
    private Timer? _drainTimer;
    private TargetCandidate? _target;
    private bool _recordingRunning;
    private ProposeTarget? _pendingTarget;
    private CaptureState _state = CaptureState.Initial;
    private long _nextSessionId;

    // The Hotkey QPC of the last accepted save press, which the Save Cooldown runs from (R5); null
    // before the first. Global: a new session doesn't clear it.
    private long? _cooldownFrom;

    // Clip writes started and not yet posted back, across sessions (R6).
    private int _writing;

    // The Recording started and not yet posted back; null when none runs.
    private RecordingRun? _recording;

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
    /// <param name="recordings">When given, a <c>recordToggle</c> press on the game session starts or stops a Recording.</param>
    public SessionOrchestrator(
        ICaptureSessionFactory factory,
        Func<SettingsDocument> currentDocument,
        Action<CaptureState> publish,
        Action<HotkeyPress>? hotkeyPressed = null,
        IMicMuteStore? micMute = null,
        Action<TargetChanged>? targetChanged = null,
        TargetCapture? targetCapture = null,
        ReplayClips? clips = null,
        Recordings? recordings = null)
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

        // Before the thread runs, so no session can start with any other mute.
        _state = CaptureState.Initial with { MicMuted = LoadMicMuted(micMute) };
        if (_state != CaptureState.Initial)
        {
            // The first publish, before the thread exists: nothing else can publish yet.
            _publish(_state);
        }

        _thread = new Thread(Run) { Name = "SessionOrchestrator", IsBackground = true };
        _thread.Start();
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
            EndActive("engine stopping", failure: null);
        }

        ReleaseDrain();
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
                else if (pressed.Action == HotkeyAction.RecordToggle)
                {
                    ToggleRecording(pressed.Qpc);
                }

                _hotkeyPressed?.Invoke(new HotkeyPress(pressed.Action, pressed.Qpc));
                break;

            case ProposeTarget propose:
                CommitTarget(propose);
                break;

            case RecordingRunning recording:
                _recordingRunning = recording.Running;
                SetState(_state with { Recording = recording.Running ? RecordingState.Running : RecordingState.Off });
                if (!_recordingRunning && _pendingTarget is { } pending)
                {
                    CommitTarget(pending);
                }

                break;

            case ClipWritten written:
                OnClipWritten(written);
                break;

            case RecordingWritten recorded:
                OnRecordingWritten(recorded);
                break;

            case SessionClosed closed when _active?.Id == closed.SessionId:
                // WGC can see the window close before Game Detection sees the exit: drain here too.
                if (Drains(_active))
                {
                    DrainActive(closed.Reason);
                }
                else
                {
                    EndActive(closed.Reason, failure: null);
                }

                break;

            case DrainExpired expired when _drain?.Id == expired.SessionId:
                ReleaseDrain();
                break;

            case SessionFailed failed when _active?.Id == failed.SessionId:
                EndActive("failure: " + failed.Failure.Message, failed.Failure);
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

        // A drained ring never outlives the start of a new session.
        ReleaseDrain();

        // AD-5: resolved once, here; a retarget or a later settings change never touches it.
        var snapshot = SettingsResolver.Resolve(_currentDocument(), target.ProfileId);
        var spec = new SessionSpec(kind, target, snapshot, audio(snapshot), _state.MicMuted);
        var id = ++_nextSessionId;
        var session = _factory.Start(spec, new Events(this, id));
        var active = new Active(id, session, spec) { GameName = gameName };
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

    private SessionOutcome EndActive(string reason, Exception? failure)
    {
        var active = _active!;
        _active = null;
        MarkSessionEnded(active);

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
    private void DrainActive(string reason)
    {
        var active = _active!;
        _active = null;
        MarkSessionEnded(active);
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
    /// a Recording runs, a switch from the Target to another game is held as pending, each later one
    /// replacing it; a proposal of the Target itself drops it. An exit or Ignored handover is never
    /// held: the Target is gone, or must never be captured (AD-5).
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

        if (_recordingRunning && current is not null && to is not null && propose.Reason is not (TargetChangeReason.Exit or TargetChangeReason.Ignored))
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
            if (to is null && propose.Reason == TargetChangeReason.Exit && Drains(_active))
            {
                DrainActive("target exited");
            }
            else
            {
                EndActive("target changed", failure: null);
            }
        }

        if (to is not null)
        {
            StartOnTarget(to);
        }
    }

    /// <summary>Moves the debug capture to the Target's new window, or starts it there when none is running.</summary>
    private void FollowTargetWindow(TargetCandidate target)
    {
        if (_targetCapture is null)
        {
            return;
        }

        if (_active is null)
        {
            StartOnTarget(target);
            return;
        }

        if (_active.Spec.Kind != SessionKind.Game || _active.Spec.Target.ProcessId != target.ProcessId)
        {
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

    private void StartOnTarget(TargetCandidate target)
    {
        try
        {
            Start(SessionKind.Game, new CaptureTarget(target.ProcessId, target.Window, target.ProfileId), _targetCapture!.Audio, target.Name);
        }
        catch (Exception e)
        {
            // The Target stays committed; only its capture failed.
            _targetCapture!.Failed(target, e);
        }
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
            ReportSaveFailure(e);
            return;
        }

        SetState(_state with { ClipsWriting = _writing });
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
        }

        _clips?.Saved(new ClipSaved(written.Path));
    }

    /// <summary>
    /// A <c>recordToggle</c> press (R1): stops the running Recording, else starts one. A press while
    /// a Recording is stopping is ignored.
    /// </summary>
    private void ToggleRecording(long qpc)
    {
        if (_recordings is null)
        {
            return;
        }

        if (_recording is not { } running)
        {
            StartRecording(qpc);
            return;
        }

        if (running.Reason is not null)
        {
            _recordings.Info?.Invoke("The Record press came while the Recording was stopping; it was ignored");
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
            _recordings.Failed(e);
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
    private void StartRecording(long qpc)
    {
        var recordings = _recordings!;
        if (_active is not { Spec.Kind: SessionKind.Game } active)
        {
            recordings.Info?.Invoke(_drain is not null
                ? "The Record press came while the game session was draining; no Recording was started"
                : "The Record press came with no game session; no Recording was started");
            return;
        }

        var document = _currentDocument();
        var profileId = active.Spec.Target.ProfileId;
        if (profileId is null || !document.Profiles.TryGetValue(profileId, out var profile))
        {
            recordings.Info?.Invoke("The Record press came for a game with no Game Profile; no Recording was started");
            return;
        }

        if (!profile.RecordingsEnabled)
        {
            recordings.Info?.Invoke($"Recordings are off for {profile.Name}; the Record press was ignored");
            return;
        }

        IRecordingWrite? write;
        string path;
        try
        {
            write = active.Session.Record(qpc);
            if (write is null)
            {
                recordings.Info?.Invoke("The game session can't record yet; no Recording was started");
                return;
            }

            // AD-5: one snapshot for the press; 6.5 waits for a chain that matches it.
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
            return;
        }

        var library = recordings.Library ?? NoLibraryWrites.Instance;
        library.BeginWrite(path, profileId);
        recordings.Open?.Opened(path);
        var recording = new RecordingRun(active.Id, path, write);
        try
        {
            write.Start(path, error => Post(new RecordingWritten(recording, error)));
        }
        catch (Exception e)
        {
            // It never started: nothing else will remove the reserved file.
            TryDelete(path);
            library.EndWrite(path, WriteOutcome.Failed);
            recordings.Open?.Closed(path);
            recordings.Failed(e);
            return;
        }

        _recording = recording;
        SetState(_state with { Recording = RecordingState.Running });
        recordings.Started(new RecordingStarted(path));
    }

    /// <summary>
    /// A session ending under its running Recording stops it with
    /// <see cref="RecordingStopReasons.SessionEnded"/>; the writer finishes as the output ends.
    /// </summary>
    private void MarkSessionEnded(Active active)
    {
        if (_recording is { Reason: null } running && running.SessionId == active.Id)
        {
            running.Reason = RecordingStopReasons.SessionEnded;
        }
    }

    /// <summary>
    /// A Recording writer's result: the Library is told how it ended (written while its file holds
    /// data), the Recording is published as stopped, with its stop reason or, when the writer
    /// failed, <see cref="WriteFailures.ReasonOf"/>, and <see cref="CaptureState.Recording"/> goes off.
    /// </summary>
    private void OnRecordingWritten(RecordingWritten written)
    {
        var recording = written.Recording;
        if (!ReferenceEquals(_recording, recording))
        {
            return;
        }

        _recording = null;
        var recordings = _recordings!;
        var error = written.Error;

        // A failed writer deletes a file that holds nothing; one with data is kept, finished as far as it got.
        var kept = error is null || File.Exists(recording.Path);
        (recordings.Library ?? NoLibraryWrites.Instance).EndWrite(recording.Path, kept ? WriteOutcome.Written : WriteOutcome.Failed);
        recordings.Open?.Closed(recording.Path);
        if (error is not null)
        {
            recordings.Failed(error);
        }

        var reason = error is not null ? WriteFailures.ReasonOf(error) : recording.Reason ?? RecordingStopReasons.SessionEnded;
        SetState(_state with { Recording = RecordingState.Off });
        recordings.Stopped(new RecordingStopped(recording.Path, reason));
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

    private sealed class Active(long id, ICaptureSession session, SessionSpec spec)
    {
        public long Id { get; } = id;

        public ICaptureSession Session { get; } = session;

        public SessionSpec Spec { get; set; } = spec;

        /// <summary>The Target's display name at <see cref="StartOnTarget"/>; null for a session started otherwise.</summary>
        public string? GameName { get; init; }

        /// <summary>The press QPC of the session's last Reset (R5); null before its first Clip.</summary>
        public long? ResetQpc { get; set; }

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
    }

    /// <summary>A Recording writer's result, posted from its thread.</summary>
    private sealed record RecordingWritten(RecordingRun Recording, Exception? Error) : SessionMessage;

    /// <summary>A Clip writer's result, posted from its thread, with the session, press and write it came from.</summary>
    private sealed record ClipWritten(long SessionId, long Qpc, IClipWrite Write, string Path, Exception? Error) : SessionMessage;

    private sealed record SessionFailed(long SessionId, Exception Failure) : SessionMessage;

    /// <summary>The drain timer of session <paramref name="SessionId"/> fired; ignored once that drain is gone.</summary>
    private sealed record DrainExpired(long SessionId) : SessionMessage;

    /// <summary>One session's post-backs, tagged with its id so a late one from an ended session is ignored.</summary>
    private sealed class Events(SessionOrchestrator owner, long id) : ISessionEvents
    {
        public void Closed(string reason) => owner.Post(new SessionClosed(id, reason));

        public void Failed(Exception failure) => owner.Post(new SessionFailed(id, failure));
    }
}
