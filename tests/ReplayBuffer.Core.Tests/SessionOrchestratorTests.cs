using System.Collections.Concurrent;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

public sealed class SessionOrchestratorTests : IDisposable
{
    private const int Pid = 4242;
    private const string ProfileId = @"c:\games\game.exe";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly SessionAudio NoAudio = SessionAudio.None;

    private readonly FakeFactory _factory = new();
    private readonly ConcurrentQueue<(CaptureState State, int ThreadId)> _published = new();
    private readonly System.Threading.Channels.Channel<(HotkeyPress Press, int ThreadId)> _presses =
        System.Threading.Channels.Channel.CreateUnbounded<(HotkeyPress, int)>();

    private readonly SessionOrchestrator _orchestrator;
    private SettingsDocument _document = Revision(3);

    public SessionOrchestratorTests()
    {
        _orchestrator = new SessionOrchestrator(_factory, () => Volatile.Read(ref _document), state => _published.Enqueue((state, Environment.CurrentManagedThreadId)),
            press => _presses.Writer.TryWrite((press, Environment.CurrentManagedThreadId)));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _orchestrator.Dispose();

    [Fact]
    public async Task StartResolvesTheSnapshotAndPublishesBufferingOnce()
    {
        var running = await Start(Target(window: 1));

        Assert.Equal(SessionKind.Game, running.Spec.Kind);
        Assert.Equal(3, running.Spec.Snapshot.SettingsRevision);
        Assert.Equal(ProfileId, running.Spec.Snapshot.ProfileId);
        Assert.Same(running.Spec, Assert.Single(_factory.Sessions).Spec);
        var (state, _) = Assert.Single(_published);
        Assert.Equal(CaptureState.Initial with { Replay = ReplayState.Buffering }, state);
    }

    [Fact]
    public async Task ASecondStartIsRefusedWithSessionRunning()
    {
        await Start(Target(window: 1));

        var error = await Assert.ThrowsAsync<ErrorCodeException>(() => Start(Target(window: 2)));

        Assert.Equal(ErrorCodes.SessionRunning, error.Code);
        Assert.Single(_factory.Sessions);
        Assert.Single(_published);
    }

    [Fact]
    public async Task ARetargetToTheSameProcessMovesCaptureAndKeepsTheSnapshot()
    {
        var running = await Start(Target(window: 1));

        var spec = await _orchestrator.RetargetAsync(Target(window: 2)).WaitAsync(Timeout, Token);

        Assert.Equal([(nint)2], _factory.Sessions[0].Retargets);
        Assert.Equal((nint)2, spec.Target.Window);
        Assert.Same(running.Spec.Snapshot, spec.Snapshot);
        Assert.Single(_published);
    }

    [Fact]
    public async Task ARetargetToAnotherProcessIsRefusedAndLeavesTheSessionAlone()
    {
        await Start(Target(window: 1));

        var error = await Assert.ThrowsAsync<ErrorCodeException>(
            () => _orchestrator.RetargetAsync(new CaptureTarget(Pid + 1, 2, ProfileId)).WaitAsync(Timeout, Token));

        Assert.Equal(ErrorCodes.RetargetOtherProcess, error.Code);
        Assert.Empty(_factory.Sessions[0].Retargets);
        Assert.Null(_factory.Sessions[0].EndReason);
        Assert.Single(_published);
    }

    [Fact]
    public async Task RetargetAndEndWithNoSessionAreRefusedWithSessionNotRunning()
    {
        var retarget = await Assert.ThrowsAsync<ErrorCodeException>(() => _orchestrator.RetargetAsync(Target(window: 1)).WaitAsync(Timeout, Token));
        var end = await Assert.ThrowsAsync<ErrorCodeException>(() => _orchestrator.EndAsync().WaitAsync(Timeout, Token));

        Assert.Equal(ErrorCodes.SessionNotRunning, retarget.Code);
        Assert.Equal(ErrorCodes.SessionNotRunning, end.Code);
        Assert.Empty(_factory.Sessions);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task EndReturnsTheOutcomeAndPublishesOff()
    {
        var running = await Start(Target(window: 1));

        var outcome = await _orchestrator.EndAsync("done").WaitAsync(Timeout, Token);

        Assert.Equal("fake-1.mp4", outcome.Path);
        Assert.Equal("done", outcome.Reason);
        Assert.Null(outcome.Failure);
        Assert.Same(outcome, await running.Ended.WaitAsync(Timeout, Token));
        Assert.Equal([ReplayState.Buffering, ReplayState.Off], _published.Select(p => p.State.Replay));
        Assert.Equal(CaptureState.Initial, _published.Last().State);
    }

    [Fact]
    public async Task AClosedWindowEndsTheSessionWithItsReason()
    {
        var running = await Start(Target(window: 1));

        _factory.Sessions[0].Events.Closed("window closed");
        var outcome = await running.Ended.WaitAsync(Timeout, Token);

        Assert.Equal("window closed", outcome.Reason);
        Assert.Null(outcome.Failure);
        Assert.Equal("window closed", _factory.Sessions[0].EndReason);
        Assert.Equal(ReplayState.Off, _published.Last().State.Replay);
        var end = await Assert.ThrowsAsync<ErrorCodeException>(() => _orchestrator.EndAsync().WaitAsync(Timeout, Token));
        Assert.Equal(ErrorCodes.SessionNotRunning, end.Code);
    }

    [Fact]
    public async Task AFailureEndsTheSessionAndIsCarriedInTheOutcome()
    {
        var running = await Start(Target(window: 1));
        var failure = new InvalidOperationException("boom");

        _factory.Sessions[0].Events.Failed(failure);
        var outcome = await running.Ended.WaitAsync(Timeout, Token);

        Assert.Same(failure, outcome.Failure);
        Assert.Equal(ReplayState.Off, _published.Last().State.Replay);
    }

    [Fact]
    public async Task APostBackFromAnEndedSessionIsIgnored()
    {
        await Start(Target(window: 1));
        await _orchestrator.EndAsync().WaitAsync(Timeout, Token);
        var second = await Start(Target(window: 2));

        _factory.Sessions[0].Events.Closed("late");
        _factory.Sessions[0].Events.Failed(new InvalidOperationException("late"));

        // A command posted after them is handled after them: the second session is still running.
        var spec = await _orchestrator.RetargetAsync(Target(window: 3)).WaitAsync(Timeout, Token);
        Assert.Equal((nint)3, spec.Target.Window);
        Assert.False(second.Ended.IsCompleted);
        Assert.Null(_factory.Sessions[1].EndReason);
    }

    [Fact]
    public async Task AFactoryFailureStartsNothing()
    {
        _factory.FailNextStart = new ErrorCodeException(ErrorCodes.CaptureFailed, "no device");

        var error = await Assert.ThrowsAsync<ErrorCodeException>(() => Start(Target(window: 1)));

        Assert.Equal(ErrorCodes.CaptureFailed, error.Code);
        Assert.Empty(_published);
        await Start(Target(window: 1));
        Assert.Single(_published);
    }

    [Fact]
    public async Task PostsFromManyThreadsAreHandledOneAtATimeOnOneThreadInArrivalOrder()
    {
        await Start(Target(window: 0));
        var gate = new Lock();
        var next = 1;
        var replies = new ConcurrentBag<Task<SessionSpec>>();
        const int threads = 8;
        const int perThread = 200;
        using var go = new ManualResetEventSlim();

        var posters = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            go.Wait(Token);
            for (var i = 0; i < perThread; i++)
            {
                // The ticket is taken with the post, under one lock: ticket order is channel order.
                lock (gate)
                {
                    replies.Add(_orchestrator.RetargetAsync(Target(window: next++)));
                }
            }
        })).ToList();
        posters.ForEach(t => t.Start());
        go.Set();
        posters.ForEach(t => t.Join());
        await Task.WhenAll(replies).WaitAsync(Timeout, Token);

        var session = _factory.Sessions[0];
        var tickets = session.Retargets.ToList();
        Assert.Equal(threads * perThread, tickets.Count);
        for (var i = 1; i < tickets.Count; i++)
        {
            Assert.True(tickets[i] > tickets[i - 1], $"ticket {tickets[i]} was handled after {tickets[i - 1]}");
        }

        Assert.Equal([_orchestrator.ThreadId], session.ThreadIds.Distinct());
        Assert.Equal(1, session.MaxConcurrency);
    }

    [Fact]
    public async Task TheSnapshotSurvivesASettingsChangeAndARetarget()
    {
        var running = await Start(Target(window: 1));

        Volatile.Write(ref _document, Revision(9));
        var spec = await _orchestrator.RetargetAsync(Target(window: 2)).WaitAsync(Timeout, Token);

        Assert.Equal(3, spec.Snapshot.SettingsRevision);
        Assert.Same(running.Spec.Snapshot, spec.Snapshot);

        // The next session takes the new document.
        await _orchestrator.EndAsync().WaitAsync(Timeout, Token);
        var next = await Start(Target(window: 3));
        Assert.Equal(9, next.Spec.Snapshot.SettingsRevision);
    }

    [Fact]
    public async Task StateIsPublishedOnlyOnTheOrchestratorThread()
    {
        var running = await Start(Target(window: 1));
        await _orchestrator.EndAsync().WaitAsync(Timeout, Token);
        await Start(Target(window: 2));
        _factory.Sessions[1].Events.Failed(new InvalidOperationException("boom"));
        await _factory.Sessions[1].Ended.Task.WaitAsync(Timeout, Token);
        await running.Ended.WaitAsync(Timeout, Token);
        await Assert.ThrowsAsync<ErrorCodeException>(() => _orchestrator.EndAsync().WaitAsync(Timeout, Token));

        // Buffering, off, buffering, off: published once per change.
        Assert.Equal(4, _published.Count);
        Assert.All(_published, p => Assert.Equal(_orchestrator.ThreadId, p.ThreadId));
        Assert.NotEqual(Environment.CurrentManagedThreadId, _orchestrator.ThreadId);
    }

    [Fact]
    public async Task DisposeEndsARunningSessionAndRefusesLaterPosts()
    {
        var running = await Start(Target(window: 1));

        _orchestrator.Dispose();

        Assert.True(running.Ended.IsCompleted);
        Assert.Equal("engine stopping", (await running.Ended).Reason);
        Assert.Equal(ReplayState.Off, _published.Last().State.Replay);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Start(Target(window: 2)));
    }

    [Fact]
    public async Task ARetargetThatThrowsEndsTheSessionWithThatFailure()
    {
        var running = await Start(Target(window: 1));
        var failure = new InvalidOperationException("no capture");
        _factory.Sessions[0].RetargetFailure = failure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.RetargetAsync(Target(window: 2)).WaitAsync(Timeout, Token));

        Assert.Same(failure, thrown);
        Assert.Same(failure, (await running.Ended.WaitAsync(Timeout, Token)).Failure);
        Assert.Equal(ReplayState.Off, _published.Last().State.Replay);
    }

    [Fact]
    public async Task ARetargetOfAStoppedSessionEndsItCleanlyAndFailsWithSessionNotRunning()
    {
        var running = await Start(Target(window: 1));
        _factory.Sessions[0].RetargetFailure = new ErrorCodeException(ErrorCodes.SessionNotRunning, "stopped");

        var error = await Assert.ThrowsAsync<ErrorCodeException>(() => _orchestrator.RetargetAsync(Target(window: 2)).WaitAsync(Timeout, Token));

        Assert.Equal(ErrorCodes.SessionNotRunning, error.Code);
        Assert.Null((await running.Ended.WaitAsync(Timeout, Token)).Failure);
        Assert.Equal(ReplayState.Off, _published.Last().State.Replay);
    }

    [Fact]
    public async Task ARetargetTheSessionRefusesAsAnotherProcessLeavesItRunning()
    {
        var running = await Start(Target(window: 1));
        _factory.Sessions[0].RetargetFailure = new ErrorCodeException(ErrorCodes.RetargetOtherProcess, "not ours");

        var error = await Assert.ThrowsAsync<ErrorCodeException>(() => _orchestrator.RetargetAsync(Target(window: 2)).WaitAsync(Timeout, Token));

        Assert.Equal(ErrorCodes.RetargetOtherProcess, error.Code);
        Assert.False(running.Ended.IsCompleted);
        Assert.Null(_factory.Sessions[0].EndReason);
        Assert.Single(_published);
        _factory.Sessions[0].RetargetFailure = null;
        var spec = await _orchestrator.RetargetAsync(Target(window: 3)).WaitAsync(Timeout, Token);
        Assert.Equal((nint)3, spec.Target.Window);
    }

    [Fact]
    public async Task ARetargetKeepsTheSessionsProfile()
    {
        await Start(Target(window: 1));

        var spec = await _orchestrator.RetargetAsync(new CaptureTarget(Pid, 2, @"c:\other.exe")).WaitAsync(Timeout, Token);

        Assert.Equal(ProfileId, spec.Target.ProfileId);
        Assert.Equal(ProfileId, spec.Snapshot.ProfileId);
    }

    [Fact]
    public async Task AnEndThatThrowsYieldsAnOutcomeCarryingTheExceptionAndStateOff()
    {
        var running = await Start(Target(window: 1));
        var failure = new InvalidOperationException("trailer");
        _factory.Sessions[0].EndFailure = failure;

        var outcome = await _orchestrator.EndAsync("done").WaitAsync(Timeout, Token);

        Assert.Same(failure, outcome.Failure);
        Assert.Null(outcome.Path);
        Assert.Same(outcome, await running.Ended.WaitAsync(Timeout, Token));
        Assert.Equal(CaptureState.Initial, _published.Last().State);
    }

    [Fact]
    public async Task AHotkeyPressReachesTheCallbackOnTheOrchestratorThreadInOrder()
    {
        Assert.True(_orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, 100)));
        Assert.True(_orchestrator.Post(new HotkeyPressed(HotkeyAction.MicMute, 200)));

        var first = await _presses.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
        var second = await _presses.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

        Assert.Equal(new HotkeyPress(HotkeyAction.Save, 100), first.Press);
        Assert.Equal(new HotkeyPress(HotkeyAction.MicMute, 200), second.Press);
        Assert.Equal(_orchestrator.ThreadId, first.ThreadId);
        Assert.Equal(_orchestrator.ThreadId, second.ThreadId);
        Assert.Empty(_published);
        Assert.Empty(_factory.Sessions);
    }

    private static CaptureTarget Target(nint window) => new(Pid, window, ProfileId);

    private static SettingsDocument Revision(long revision) => SettingsDocument.Empty with
    {
        Revision = revision,
        Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
        {
            Name = "Game",
            Overrides = SettingsMaps.EmptyValues.Add(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(60)),
        }),
    };

    private Task<RunningSession> Start(CaptureTarget target) =>
        _orchestrator.StartAsync(SessionKind.Game, target, NoAudio).WaitAsync(Timeout, Token);

    private sealed class FakeFactory : ICaptureSessionFactory
    {
        private readonly List<FakeSession> _sessions = [];

        public Exception? FailNextStart { get; set; }

        public IReadOnlyList<FakeSession> Sessions
        {
            get
            {
                lock (_sessions)
                {
                    return [.. _sessions];
                }
            }
        }

        public ICaptureSession Start(SessionSpec spec, ISessionEvents events)
        {
            if (FailNextStart is { } failure)
            {
                FailNextStart = null;
                throw failure;
            }

            lock (_sessions)
            {
                var session = new FakeSession(spec, events, _sessions.Count + 1);
                _sessions.Add(session);
                return session;
            }
        }
    }

    private sealed class FakeSession(SessionSpec spec, ISessionEvents events, int number) : ICaptureSession
    {
        private readonly ConcurrentQueue<nint> _retargets = new();
        private int _inside;
        private int _maxConcurrency;

        public SessionSpec Spec { get; } = spec;

        public ISessionEvents Events { get; } = events;

        public IEnumerable<nint> Retargets => _retargets;

        public ConcurrentQueue<int> ThreadIds { get; } = new();

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public string? EndReason { get; private set; }

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? RetargetFailure { get; set; }

        public Exception? EndFailure { get; set; }

        public void Retarget(CaptureTarget target)
        {
            if (RetargetFailure is { } failure)
            {
                throw failure;
            }

            var inside = Interlocked.Increment(ref _inside);
            InterlockedMax(ref _maxConcurrency, inside);
            ThreadIds.Enqueue(Environment.CurrentManagedThreadId);
            _retargets.Enqueue(target.Window);
            Interlocked.Decrement(ref _inside);
        }

        public SessionOutcome End(string reason)
        {
            EndReason = reason;
            Ended.TrySetResult();
            if (EndFailure is { } failure)
            {
                throw failure;
            }

            return new SessionOutcome($"fake-{number}.mp4", reason, null);
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int current;
            while ((current = Volatile.Read(ref location)) < value
                && Interlocked.CompareExchange(ref location, value, current) != current)
            {
            }
        }
    }
}
