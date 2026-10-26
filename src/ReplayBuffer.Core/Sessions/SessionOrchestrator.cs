using System.Threading.Channels;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
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
/// arrived (AD-6). The orchestrator hands it to its press callback; no action is bound to it yet.
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
/// single-reader channel; callers and media threads only post to it.
/// </summary>
public sealed class SessionOrchestrator : IDisposable
{
    private readonly Channel<SessionMessage> _channel = Channel.CreateUnbounded<SessionMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    private readonly ICaptureSessionFactory _factory;
    private readonly Func<SettingsDocument> _currentDocument;
    private readonly Action<CaptureState> _publish;
    private readonly Action<HotkeyPress>? _hotkeyPressed;
    private readonly Thread _thread;

    // Orchestrator thread only.
    private Active? _active;
    private CaptureState _state = CaptureState.Initial;
    private long _nextSessionId;

    private int _disposed;

    /// <param name="factory">Starts the capture sessions.</param>
    /// <param name="currentDocument">The current settings document, read once per Start for the snapshot (AD-5).</param>
    /// <param name="publish">Receives each changed <see cref="CaptureState"/>, on the orchestrator thread, once per change.</param>
    /// <param name="hotkeyPressed">Receives each <see cref="HotkeyPressed"/>, on the orchestrator thread, in arrival order.</param>
    public SessionOrchestrator(
        ICaptureSessionFactory factory,
        Func<SettingsDocument> currentDocument,
        Action<CaptureState> publish,
        Action<HotkeyPress>? hotkeyPressed = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(currentDocument);
        ArgumentNullException.ThrowIfNull(publish);
        _factory = factory;
        _currentDocument = currentDocument;
        _publish = publish;
        _hotkeyPressed = hotkeyPressed;
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

    /// <summary>Stops taking messages, handles those already posted, ends any running session, and joins the thread.</summary>
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
                _hotkeyPressed?.Invoke(new HotkeyPress(pressed.Action, pressed.Qpc));
                break;

            case SessionClosed closed when _active?.Id == closed.SessionId:
                EndActive(closed.Reason, failure: null);
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

    private RunningSession Start(StartSession start)
    {
        if (_active is not null)
        {
            throw new ErrorCodeException(ErrorCodes.SessionRunning, "A session is already running.");
        }

        // AD-5: resolved once, here; a retarget or a later settings change never touches it.
        var snapshot = SettingsResolver.Resolve(_currentDocument(), start.Target.ProfileId);
        var spec = new SessionSpec(start.Kind, start.Target, snapshot, start.Audio);
        var id = ++_nextSessionId;
        var session = _factory.Start(spec, new Events(this, id));
        var active = new Active(id, session, spec);
        _active = active;
        SetState(_state with { Replay = start.Kind == SessionKind.Game ? ReplayState.Buffering : ReplayState.Off });
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

        public TaskCompletionSource<SessionOutcome> Ended { get; } = NewReply<SessionOutcome>();
    }

    private sealed record SessionClosed(long SessionId, string Reason) : SessionMessage;

    private sealed record SessionFailed(long SessionId, Exception Failure) : SessionMessage;

    /// <summary>One session's post-backs, tagged with its id so a late one from an ended session is ignored.</summary>
    private sealed class Events(SessionOrchestrator owner, long id) : ISessionEvents
    {
        public void Closed(string reason) => owner.Post(new SessionClosed(id, reason));

        public void Failed(Exception failure) => owner.Post(new SessionFailed(id, failure));
    }
}
