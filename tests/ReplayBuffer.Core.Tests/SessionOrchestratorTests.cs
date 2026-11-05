using System.Collections.Concurrent;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Library;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Core.Settings;

namespace ReplayBuffer.Core.Tests;

public sealed partial class SessionOrchestratorTests : IDisposable
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

        // Only the mic mute press changed the state.
        var (state, _) = Assert.Single(_published);
        Assert.Equal(CaptureState.Initial with { MicMuted = false }, state);
        Assert.Empty(_factory.Sessions);
    }

    [Fact]
    public async Task TheTargetFollowsProposalsAndTheDebugCaptureFollowsTheTarget()
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
        var failures = new ConcurrentQueue<Exception>();
        using var orchestrator = new SessionOrchestrator(
            _factory,
            () => Volatile.Read(ref _document),
            _ => { },
            targetChanged: c => changes.Writer.TryWrite(c),
            targetCapture: new TargetCapture(_ => NoAudio, (_, e) => failures.Enqueue(e)));
        var a = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
        var b = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");

        orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
        orchestrator.Post(new ProposeTarget(a with { Window = 2 }, TargetChangeReason.Detected));
        orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Detected));
        orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));

        Assert.Equal(new TargetChanged(null, a.ToTargetGame(), TargetChangeReason.Detected), await NextAsync(changes));
        Assert.Equal(new TargetChanged(a.ToTargetGame(), b.ToTargetGame(), TargetChangeReason.Detected), await NextAsync(changes));
        Assert.Equal(new TargetChanged(b.ToTargetGame(), null, TargetChangeReason.Exit), await NextAsync(changes));
        await _factory.Sessions[1].Ended.Task.WaitAsync(Timeout, Token);

        var (first, second) = (_factory.Sessions[0], _factory.Sessions[1]);
        Assert.Equal(new CaptureTarget(Pid, 1, ProfileId), first.Spec.Target);
        Assert.Equal([(nint)2], first.Retargets);
        Assert.Equal("target changed", first.EndReason);
        Assert.Equal(new CaptureTarget(Pid + 1, 3, b.ProfileId), second.Spec.Target);
        Assert.Equal(2, _factory.Sessions.Count);
        Assert.False(changes.Reader.TryRead(out _));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ARunningRecordingHoldsASwitchUntilItsWriterPostsBackThenItCommitsAtOnce()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
            var (orchestrator, events, presses, _, published, factory) = NewRecordingOrchestrator(c => changes.Writer.TryWrite(c));
            using var owned = orchestrator;
            var a = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
            var b = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");
            var c = new TargetCandidate(Pid + 2, 5, @"c:\games\third.exe", "Third.exe");
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();

            orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
            Assert.Equal(new TargetChanged(null, a.ToTargetGame(), TargetChangeReason.Detected), await NextAsync(changes));
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));

            orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Focus));
            orchestrator.Post(new ProposeTarget(c, TargetChangeReason.Focus));

            // A proposal of the Target itself drops the held switch; the dev stub neither holds nor commits.
            orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Focus));
            orchestrator.Post(new RecordingRunning(false));
            orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Focus));
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 1);
            Assert.False(changes.Reader.TryRead(out _));
            Assert.Equal(RecordingState.Running, published.Last().Recording);
            Assert.Single(factory.Sessions);

            orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, qpc + 2));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            Assert.Equal(new TargetChanged(a.ToTargetGame(), b.ToTargetGame(), TargetChangeReason.Focus), await NextAsync(changes));
            Assert.Equal("target changed", factory.Sessions[0].EndReason);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ARecordingStopsWithTheReasonOfWhatEndedItsSession()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
            var (orchestrator, events, presses, made, _, factory) = NewRecordingOrchestrator(c => changes.Writer.TryWrite(c), drains: true);
            using var owned = orchestrator;
            var a = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
            var b = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();

            async Task<RecordingStarted> RecordOnA(TargetChangeReason reason)
            {
                orchestrator.Post(new ProposeTarget(a, reason));
                Assert.Equal(a.ToTargetGame(), (await NextAsync(changes)).To);
                await Press(orchestrator, presses, HotkeyAction.RecordToggle, ++qpc);
                return Assert.IsType<RecordingStarted>(await NextAsync(events));
            }

            // The Target exits: the session drains (R1), game-exit, and the exit isn't held.
            var first = await RecordOnA(TargetChangeReason.Detected);
            orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));
            Assert.Equal(new TargetChanged(a.ToTargetGame(), null, TargetChangeReason.Exit), await NextAsync(changes));
            made.Last().Finish();
            Assert.Equal(new RecordingStopped(first.Path, RecordingStopReasons.GameExit), await NextAsync(events));
            Assert.Equal(1, factory.Sessions[^1].Drains);

            // Ignored hands over to another game at once: ignored.
            var second = await RecordOnA(TargetChangeReason.Detected);
            orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Ignored));
            Assert.Equal(new TargetChanged(a.ToTargetGame(), b.ToTargetGame(), TargetChangeReason.Ignored), await NextAsync(changes));
            made.Last().Finish();
            Assert.Equal(new RecordingStopped(second.Path, RecordingStopReasons.Ignored), await NextAsync(events));

            // The session's window closes: it drains too, game-exit.
            var third = await RecordOnA(TargetChangeReason.Focus);
            factory.Sessions[^1].Events.Closed("window closed");
            made.Last().Finish();
            Assert.Equal(new RecordingStopped(third.Path, RecordingStopReasons.GameExit), await NextAsync(events));
            Assert.Equal("window closed", factory.Sessions[^1].EndReason);
            Assert.Equal(1, factory.Sessions[^1].Drains);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeMidRecordingFinishesItAsShutdownBeforeItReturns()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var library = new FakeLibraryWrites();
            var (orchestrator, events, presses, made, _, factory) = NewRecordingOrchestrator(library: library);
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, System.Diagnostics.Stopwatch.GetTimestamp());
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));

            // The writer finishes once the session's output ends, after the channel has closed.
            var session = factory.Sessions[0];
            var recording = made.Single();
            _ = session.Ended.Task.ContinueWith(_ => recording.Finish(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            orchestrator.Dispose();

            Assert.Equal("engine stopping", session.EndReason);
            Assert.True(events.Reader.TryRead(out var stopped));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Shutdown), stopped);
            Assert.Equal([$"begin {started.Path} {ProfileId}", $"opened {started.Path}", $"end {started.Path} Written", $"closed {started.Path}"], library.Calls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnEncoderRestartEndsTheRecordingAndStartsAnotherWithTheStateRunningThroughout()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var library = new FakeLibraryWrites();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(library: library);
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var session = factory.Sessions[0];
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();

            // With no Recording, a restart publishes nothing.
            session.Events.ChainRestarted(qpc);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 1);
            Assert.Empty(made);
            Assert.False(events.Reader.TryRead(out _));

            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 2);
            var a = Assert.IsType<RecordingStarted>(await NextAsync(events));

            // B starts at once; nothing is published until A's writer posts back.
            session.Events.ChainRestarted(qpc + 3);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 4);
            Assert.Equal(2, made.Count);
            Assert.False(events.Reader.TryRead(out _));

            made.First().Finish();
            Assert.Equal(new RecordingStopped(a.Path, RecordingStopReasons.EncoderRestart), await NextAsync(events));
            var b = Assert.IsType<RecordingStarted>(await NextAsync(events));
            Assert.Equal(new RecordingRestart(a.Path, b.Path), await NextAsync(events));
            Assert.NotEqual(a.Path, b.Path);
            Assert.Equal(RecordingState.Running, published.Last().Recording);

            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 5);
            Assert.Equal(new RecordingStopped(b.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            Assert.Equal([RecordingState.Off, RecordingState.Running, RecordingState.Off], RecordingStates(published));
            Assert.Equal(
                [$"begin {a.Path} {ProfileId}", $"opened {a.Path}", $"begin {b.Path} {ProfileId}", $"opened {b.Path}",
                 $"end {a.Path} Written", $"closed {a.Path}", $"end {b.Path} Written", $"closed {b.Path}"],
                library.Calls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnEncoderRestartWhoseNewRecordingCantStartStopsTheOldOneAndGoesOff()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(maxRecordings: 1);
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            var a = Assert.IsType<RecordingStarted>(await NextAsync(events));

            factory.Sessions[0].Events.ChainRestarted(qpc + 1);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 2);
            made.Single().Finish();

            Assert.Equal(new RecordingStopped(a.Path, RecordingStopReasons.EncoderRestart), await NextAsync(events));
            Assert.Equal(RecordingState.Off, published.Last().Recording);
            Assert.False(events.Reader.TryRead(out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ARestartsNewRecordingThatEndsFirstIsAnnouncedBeforeItsStop()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator();
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            var a = Assert.IsType<RecordingStarted>(await NextAsync(events));
            factory.Sessions[0].Events.ChainRestarted(qpc + 1);

            // B is stopped and posts back while A is still finishing.
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 2);
            var b = Assert.IsType<RecordingStarted>(await NextAsync(events));
            Assert.Equal(new RecordingRestart(a.Path, b.Path), await NextAsync(events));
            Assert.Equal(new RecordingStopped(b.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            Assert.Equal(RecordingState.Running, published.Last().Recording);

            made.First().Finish();
            Assert.Equal(new RecordingStopped(a.Path, RecordingStopReasons.EncoderRestart), await NextAsync(events));
            Assert.Equal(RecordingState.Off, published.Last().Recording);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeWhileARestartsOldRecordingFinishesHandlesBoth()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var (orchestrator, events, presses, made, _, factory) = NewRecordingOrchestrator();
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            var a = Assert.IsType<RecordingStarted>(await NextAsync(events));
            var session = factory.Sessions[0];
            session.Events.ChainRestarted(qpc + 1);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 2);

            // Both writers finish once the session's output ends, after the channel has closed.
            _ = session.Ended.Task.ContinueWith(
                _ =>
                {
                    foreach (var recording in made)
                    {
                        recording.Finish();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            orchestrator.Dispose();

            List<object> published = [];
            while (events.Reader.TryRead(out var item))
            {
                published.Add(item);
            }

            var b = made.Last().Path!;
            Assert.Equal(
                [new RecordingStopped(a.Path, RecordingStopReasons.EncoderRestart), new RecordingStarted(b), new RecordingRestart(a.Path, b), new RecordingStopped(b, RecordingStopReasons.Shutdown)],
                published);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AFullDiskStopsTheRecordingAndAMemoryReplayBufferGoesOn()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
            var saves = new ConcurrentQueue<long>();
            var diskFulls = 0;
            var replayDiskFull = new ConcurrentQueue<bool>();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                c => changes.Writer.TryWrite(c),
                drains: true,
                save: qpc =>
                {
                    saves.Enqueue(qpc);
                    return null;
                },
                recordingsWith: r => r with
                {
                    DiskFull = () => Interlocked.Increment(ref diskFulls),
                    ReplayDiskFull = replayDiskFull.Enqueue,
                });
            using var owned = orchestrator;
            var started = await RecordOnTarget(orchestrator, changes, events, presses);

            made.Last().Finish(DiskFullError());

            Assert.IsType<IOException>(await NextAsync(events));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.DiskFull), await NextAsync(events));
            await WaitUntil(() => Volatile.Read(ref diskFulls) == 1);
            Assert.Equal(RecordingState.Off, published.Last().Recording);
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);

            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.Save, qpc);
            Assert.Equal([qpc], saves);
            Assert.Single(factory.Sessions);
            Assert.Null(factory.Sessions[0].EndReason);
            Assert.Equal(1, Volatile.Read(ref diskFulls));
            Assert.Empty(replayDiskFull);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AFullDiskStopsADiskReplayBufferUntilItsRingFitsAgain()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with
            {
                Global = InFolder(folder, cooldown: 0).SetItem(Keys.ReplayStorage.Name, Keys.ReplayStorage.ToJson(ReplayStorage.Disk)),
            };
            SetRecordingsEnabled(true);
            var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
            var fits = new ConcurrentQueue<bool>([false]);
            var replayDiskFull = new ConcurrentQueue<bool>();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                c => changes.Writer.TryWrite(c),
                drains: true,
                recordingsWith: r => r with
                {
                    RingFits = snapshot => snapshot.ReplayStorage == ReplayStorage.Disk && (!fits.TryDequeue(out var fit) || fit),
                    ReplayDiskFull = replayDiskFull.Enqueue,
                    RingRecheck = TimeSpan.FromMilliseconds(50),
                });
            using var owned = orchestrator;
            var started = await RecordOnTarget(orchestrator, changes, events, presses);

            made.Last().Finish(DiskFullError());

            Assert.IsType<IOException>(await NextAsync(events));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.DiskFull), await NextAsync(events));
            await WaitUntil(() => factory.Sessions.Count == 2);
            Assert.Equal("disk full", factory.Sessions[0].EndReason);
            Assert.Equal([true, false], replayDiskFull);
            Assert.True(fits.IsEmpty);
            Assert.Equal(Pid, factory.Sessions[1].Spec.Target.ProcessId);
            Assert.Contains(published, state => state.Replay == ReplayState.Off);
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The Target becomes Game.exe, its session starts, and a Record press starts a Recording.</summary>
    private static async Task<RecordingStarted> RecordOnTarget(
        SessionOrchestrator orchestrator,
        System.Threading.Channels.Channel<TargetChanged> changes,
        System.Threading.Channels.Channel<object> events,
        System.Threading.Channels.Channel<HotkeyPress> presses)
    {
        orchestrator.Post(new ProposeTarget(new TargetCandidate(Pid, 1, ProfileId, "Game.exe"), TargetChangeReason.Detected));
        await NextAsync(changes);
        await Press(orchestrator, presses, HotkeyAction.RecordToggle, System.Diagnostics.Stopwatch.GetTimestamp());
        return Assert.IsType<RecordingStarted>(await NextAsync(events));
    }

    /// <summary>A write failure as a full disk throws it (<c>ERROR_DISK_FULL</c>).</summary>
    private static IOException DiskFullError() => new("There is not enough space on the disk.", unchecked((int)0x80070070));

    /// <summary>The published Recording states, each change once, from the initial Off.</summary>
    private static List<RecordingState> RecordingStates(IEnumerable<CaptureState> published)
    {
        List<RecordingState> states = [RecordingState.Off];
        foreach (var state in published)
        {
            if (state.Recording != states[^1])
            {
                states.Add(state.Recording);
            }
        }

        return states;
    }

    // Every message posted before it has been handled: an End with no session is refused in order.
    private static async Task HandledAsync(SessionOrchestrator orchestrator) =>
        await Assert.ThrowsAsync<ErrorCodeException>(() => orchestrator.EndAsync().WaitAsync(Timeout, Token));

    [Fact]
    public async Task AFailedStartOnTheTargetIsReportedAndTheTargetStaysCommitted()
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
        var failures = System.Threading.Channels.Channel.CreateUnbounded<(TargetCandidate, Exception)>();
        using var orchestrator = new SessionOrchestrator(
            _factory,
            () => Volatile.Read(ref _document),
            _ => { },
            targetChanged: c => changes.Writer.TryWrite(c),
            targetCapture: new TargetCapture(_ => NoAudio, (t, e) => failures.Writer.TryWrite((t, e))));
        var failure = new ErrorCodeException(ErrorCodes.CaptureFailed, "no window");
        _factory.FailNextStart = failure;
        var game = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");

        orchestrator.Post(new ProposeTarget(game, TargetChangeReason.Detected));

        Assert.Equal(game.ToTargetGame(), (await NextAsync(changes)).To);
        Assert.Equal((game, (Exception)failure), await failures.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));
        Assert.Empty(_factory.Sessions);
    }

    [Fact]
    public async Task ASavePressOnTheGameSessionSavesAClipInItsProfileFolderAndOneWithNoSessionIsIgnored()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            _document = Revision(3) with { Global = SettingsDocument.Empty.Global.SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(folder)) };
            var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
            var failures = new ConcurrentQueue<Exception>();
            var writes = new ConcurrentQueue<string>();
            var factory = new FakeFactory { Save = _ => new FakeWrite(writes) };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                clips: new ReplayClips(OutputPathAllocator.System, () => throw new InvalidOperationException("the Global folder is set"), s => saved.Writer.TryWrite(s), failures.Enqueue));

            // No session: nothing is reserved or written.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, System.Diagnostics.Stopwatch.GetTimestamp()));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, System.Diagnostics.Stopwatch.GetTimestamp()));

            var clip = await NextAsync(saved);
            Assert.Equal(Path.Combine(folder, "Game"), Path.GetDirectoryName(clip.Path));
            Assert.StartsWith("Game_", Path.GetFileName(clip.Path), StringComparison.Ordinal);
            Assert.Equal([clip.Path], writes);
            Assert.Equal([clip.Path], Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
            Assert.Empty(failures);
            Assert.False(saved.Reader.TryRead(out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task APressUnderASecondAfterAResetIsRefusedAndAFailedWriteResetsNothing()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
            var refused = System.Threading.Channels.Channel.CreateUnbounded<ClipFailed>();
            var failures = System.Threading.Channels.Channel.CreateUnbounded<Exception>();
            var writes = new ConcurrentQueue<string>();
            var made = new ConcurrentQueue<FakeWrite>();
            var factory = new FakeFactory
            {
                Save = _ =>
                {
                    // The third write fails.
                    var write = new FakeWrite(writes, made.Count == 2 ? new IOException("disk gone") : null);
                    made.Enqueue(write);
                    return write;
                },
            };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                clips: new ReplayClips(OutputPathAllocator.System, () => folder, s => saved.Writer.TryWrite(s), e => failures.Writer.TryWrite(e), f => refused.Writer.TryWrite(f)));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var press = System.Diagnostics.Stopwatch.GetTimestamp();
            var second = System.Diagnostics.Stopwatch.Frequency;

            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press));
            await NextAsync(saved);

            // 0.5 s after that Reset: refused before the session is asked for anything.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + (second / 2)));
            Assert.Equal(new ClipFailed(ErrorCodes.ClipTooShort), await NextAsync(refused));
            Assert.Single(made);
            Assert.Equal(1, made.Single().Resets);

            // 1.5 s after it: saved, and Reset again.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + (second * 3 / 2)));
            await NextAsync(saved);
            Assert.Equal([1, 1], made.Select(w => w.Resets));

            // A failed write Resets nothing.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + (second * 3)));
            Assert.IsType<IOException>(await NextAsync(failures));
            Assert.Equal([1, 1, 0], made.Select(w => w.Resets));
            Assert.Equal(new ClipFailed(ErrorCodes.SaveFailed), await NextAsync(refused));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AWrittenClipGivesTheSessionTheEffectiveBufferWindowBeforeItsReset()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
            var writes = new ConcurrentQueue<string>();
            var log = new ConcurrentQueue<string>();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new FakeFactory { Save = _ => new FakeWrite(writes, gate: gate.Task, log: log), Log = log };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                clips: new ReplayClips(OutputPathAllocator.System, () => folder, s => saved.Writer.TryWrite(s), _ => { }, _ => { }));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);

            // Press 1 writes; meanwhile the Game Profile's Buffer Window changes from 60 s to 30 s.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, System.Diagnostics.Stopwatch.GetTimestamp()));
            await WaitUntil(() => !writes.IsEmpty);
            Volatile.Write(ref _document, Revision(4) with
            {
                Global = InFolder(folder, cooldown: 0),
                Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
                {
                    Name = "Game",
                    Overrides = SettingsMaps.EmptyValues.Add(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(30)),
                }),
            });
            gate.SetResult();

            await NextAsync(saved);
            Assert.Equal(["window 30", "reset"], log);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnExitDrainsTheSessionSoASaveStillCutsUntilTheDrainExpiresAndReleasesIt()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var published = new ConcurrentQueue<CaptureState>();
            var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
            var writes = new ConcurrentQueue<string>();
            var saves = 0;
            var factory = new FakeFactory { Save = _ => { Interlocked.Increment(ref saves); return new FakeWrite(writes); } };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                published.Enqueue,
                targetCapture: new TargetCapture(_ => NoAudio, (_, _) => { }),
                clips: new ReplayClips(OutputPathAllocator.System, () => folder, s => saved.Writer.TryWrite(s), _ => { }, ExitDrain: TimeSpan.FromMilliseconds(200)));
            // No Game Profile: the Clip is named after the Target, which is gone by the press.
            var game = new TargetCandidate(Pid, 1, @"c:\games\unlisted.exe", "Unlisted Game");

            orchestrator.Post(new ProposeTarget(game, TargetChangeReason.Detected));
            orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, System.Diagnostics.Stopwatch.GetTimestamp()));

            // The drained session takes the press, and the Clip keeps the game's name.
            var clip = await NextAsync(saved);
            Assert.StartsWith("Unlisted Game_", Path.GetFileName(clip.Path), StringComparison.Ordinal);
            var session = Assert.Single(factory.Sessions);
            Assert.Equal(1, session.Drains);
            Assert.Equal(1, Volatile.Read(ref saves));

            // After ExitDrain the ring is released, the state is off, and a press reaches no session.
            await WaitUntil(() => session.Releases == 1);
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, System.Diagnostics.Stopwatch.GetTimestamp()));
            await HandledAsync(orchestrator);
            Assert.Equal(1, Volatile.Read(ref saves));
            // The write's own ClipsWriting publishes repeat Draining: collapse consecutive repeats only.
            var replays = published.Select(p => p.Replay).ToList();
            Assert.Equal([ReplayState.Buffering, ReplayState.Draining, ReplayState.Off], replays.Where((r, i) => i == 0 || r != replays[i - 1]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ANewTargetReleasesTheDrainWhileASwitchIgnoredOrDisposeNeverDrains()
    {
        var published = new ConcurrentQueue<CaptureState>();
        var orchestrator = new SessionOrchestrator(
            _factory,
            () => Volatile.Read(ref _document),
            published.Enqueue,
            targetCapture: new TargetCapture(_ => NoAudio, (_, _) => { }),
            clips: new ReplayClips(OutputPathAllocator.System, () => "unused", _ => { }, _ => { }, ExitDrain: TimeSpan.FromMinutes(5)));
        var a = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
        var b = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");
        try
        {
            // A drained, then B detected: the drain is released before B's session starts.
            orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
            orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));
            orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Detected));

            // A switch B -> A, and an exit to another game, end with no drain.
            orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Focus));
            orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Exit));

            // Ignored ends at once, no drain, and the state is off.
            orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Ignored));
            await HandledAsync(orchestrator);

            var sessions = _factory.Sessions;
            Assert.Equal(4, sessions.Count);
            Assert.Equal([1, 0, 0, 0], sessions.Select(s => s.Drains));
            Assert.Equal(1, sessions[0].Releases);
            Assert.All(sessions, s => Assert.NotNull(s.EndReason));
            Assert.Equal(ReplayState.Off, published.Last().Replay);
            Assert.Equal(
                [ReplayState.Buffering, ReplayState.Draining, ReplayState.Off, ReplayState.Buffering, ReplayState.Off, ReplayState.Buffering, ReplayState.Off, ReplayState.Buffering, ReplayState.Off],
                published.Select(p => p.Replay));

            // A closed window drains too; marking the Target Ignored then releases the drain.
            orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
            await WaitUntil(() => _factory.Sessions.Count == 5);
            var closed = _factory.Sessions[^1];
            closed.Events.Closed("window closed");
            orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Ignored));
            await HandledAsync(orchestrator);
            Assert.Equal((1, 1), (closed.Drains, closed.Releases));
            Assert.Equal(ReplayState.Off, published.Last().Replay);

            // A drain whose outcome is a failure holds nothing: released at once, and off.
            orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Detected));
            await WaitUntil(() => _factory.Sessions.Count == 6);
            var failed = _factory.Sessions[^1];
            failed.DrainFailure = new IOException("never anchored");
            orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));
            await HandledAsync(orchestrator);
            Assert.Equal((1, 1), (failed.Drains, failed.Releases));
            Assert.Equal(ReplayState.Off, published.Last().Replay);

            // Disposing mid-drain releases it.
            orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
            orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));
            await HandledAsync(orchestrator);
            var last = _factory.Sessions[^1];
            Assert.Equal((1, 0), (last.Drains, last.Releases));
        }
        finally
        {
            orchestrator.Dispose();
        }

        Assert.Equal(1, _factory.Sessions[^1].Releases);
        Assert.Equal(ReplayState.Off, published.Last().Replay);
    }

    [Fact]
    public async Task PressesInsideTheSaveCooldownAreIgnoredAndOneAfterItSaves()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            // The default saveCooldown: 5 s.
            _document = Revision(3) with { Global = InFolder(folder) };
            var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
            var ignored = new ConcurrentQueue<TimeSpan>();
            var published = new ConcurrentQueue<CaptureState>();
            var savings = 0;

            // How many clip.saving events had fired when each Save was called: synchronous means one per press, first.
            var savingsAtSave = new ConcurrentQueue<int>();
            var writes = new ConcurrentQueue<string>();
            var factory = new FakeFactory { Save = _ => { savingsAtSave.Enqueue(Volatile.Read(ref savings)); return new FakeWrite(writes); } };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                published.Enqueue,
                clips: new ReplayClips(
                    OutputPathAllocator.System,
                    () => folder,
                    s => events.Writer.TryWrite(s),
                    e => events.Writer.TryWrite(e),
                    f => events.Writer.TryWrite(f),
                    Saving: s =>
                    {
                        Interlocked.Increment(ref savings);
                        events.Writer.TryWrite(s);
                    },
                    Ignored: ignored.Enqueue));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            var second = System.Diagnostics.Stopwatch.Frequency;

            void Press(long qpc) => orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, qpc));

            Press(t);
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.IsType<ClipSaved>(await NextAsync(events));
            Assert.NotNull(published.Last().CooldownUntil);

            // 1 s and 4 s later: inside the cooldown, so no Save and no event.
            Press(t + second);
            Press(t + (second * 4));

            // 5.1 s later: accepted, and saved.
            Press(t + (second * 51 / 10));
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.IsType<ClipSaved>(await NextAsync(events));

            // Two Saves, each after its own press's clip.saving: emitted synchronously on the orchestrator thread.
            Assert.Equal([1, 2], savingsAtSave);
            Assert.Equal(2, ignored.Count);
            Assert.False(events.Reader.TryRead(out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnAcceptedPressPublishesTheCooldownAndTheWriteAtOnceAndTheWriteEnds()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            // The default saveCooldown: 5 s.
            _document = Revision(3) with { Global = InFolder(folder) };
            var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
            var ignored = System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
            var published = new ConcurrentQueue<CaptureState>();
            var gate = NewGate();
            var writes = new ConcurrentQueue<string>();
            var factory = new FakeFactory { Save = _ => new FakeWrite(writes, gate: gate.Task) };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                published.Enqueue,
                clips: new ReplayClips(
                    OutputPathAllocator.System,
                    () => folder,
                    s => events.Writer.TryWrite(s),
                    e => events.Writer.TryWrite(e),
                    f => events.Writer.TryWrite(f),
                    Ignored: i => ignored.Writer.TryWrite(i)));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);

            var before = published.Count;
            var press = System.Diagnostics.Stopwatch.GetTimestamp();
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press));

            // 1 s later: inside the cooldown. Messages are handled in order, so once it is ignored the
            // first press's turn is over, with the gate still closed.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + System.Diagnostics.Stopwatch.Frequency));
            await NextAsync(ignored);

            // Both states leave in the press's own orchestrator turn, which keeps them inside 250 ms;
            // the ignored press adds none.
            var states = published.Skip(before).ToList();
            Assert.Equal(2, states.Count);
            Assert.NotNull(states[1].CooldownUntil);
            Assert.Equal(1, states[1].ClipsWriting);

            gate.SetResult();
            Assert.IsType<ClipSaved>(await NextAsync(events));
            Assert.Equal(0, published.Last().ClipsWriting);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task APressWhileTwoClipsAreWritingIsRefusedAsBusyAndOneAfterThemSaves()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
            var writes = new ConcurrentQueue<string>();
            var made = new ConcurrentQueue<FakeWrite>();
            var gates = new[] { NewGate(), NewGate() };
            var factory = new FakeFactory
            {
                Save = _ =>
                {
                    // The first two writes wait on their gates; later ones complete at once.
                    var write = new FakeWrite(writes, gate: made.Count < gates.Length ? gates[made.Count].Task : null);
                    made.Enqueue(write);
                    return write;
                },
            };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                clips: new ReplayClips(
                    OutputPathAllocator.System,
                    () => folder,
                    s => events.Writer.TryWrite(s),
                    e => events.Writer.TryWrite(e),
                    f => events.Writer.TryWrite(f),
                    Saving: s => events.Writer.TryWrite(s)));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var press = System.Diagnostics.Stopwatch.GetTimestamp();
            var second = System.Diagnostics.Stopwatch.Frequency;

            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press));
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + (second * 2)));
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.IsType<ClipSaving>(await NextAsync(events));

            // Two writing: the third is accepted, then refused before the session is asked for anything.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + (second * 4)));
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.Equal(new ClipFailed(ErrorCodes.SaveBusy), await NextAsync(events));
            Assert.Equal(2, made.Count);
            Assert.Equal(2, writes.Distinct().Count());
            Assert.Equal([0, 0], made.Select(w => w.Resets));

            // Both finish: saved, each Reset once.
            Array.ForEach(gates, g => g.SetResult());
            Assert.IsType<ClipSaved>(await NextAsync(events));
            Assert.IsType<ClipSaved>(await NextAsync(events));
            Assert.Equal([1, 1], made.Select(w => w.Resets));

            // A next press saves.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + (second * 6)));
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.IsType<ClipSaved>(await NextAsync(events));
            Assert.Equal(3, made.Count);
            Assert.False(events.Reader.TryRead(out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AFailedWriteReportsItsReasonAndEndsItsLibraryWriteAndTheNextPressSaves()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
            var writes = new ConcurrentQueue<string>();
            var made = new ConcurrentQueue<FakeWrite>();
            var library = new FakeLibraryWrites();
            var factory = new FakeFactory
            {
                Save = _ =>
                {
                    // The first write fails with the disk full.
                    var write = new FakeWrite(writes, made.IsEmpty ? new IOException("disk full", unchecked((int)0x80070070)) : null);
                    made.Enqueue(write);
                    return write;
                },
            };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                clips: new ReplayClips(
                    OutputPathAllocator.System,
                    () => folder,
                    s => events.Writer.TryWrite(s),
                    _ => { },
                    f => events.Writer.TryWrite(f),
                    Saving: s => events.Writer.TryWrite(s),
                    Library: library));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var press = System.Diagnostics.Stopwatch.GetTimestamp();

            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press));
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.Equal(new ClipFailed(ErrorCodes.DiskFull), await NextAsync(events));
            Assert.Equal([0], made.Select(w => w.Resets));
            var path1 = writes.Single();
            Assert.Equal([$"begin {path1} {ProfileId}", $"end {path1} Failed"], library.Calls);

            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press + System.Diagnostics.Stopwatch.Frequency));
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.IsType<ClipSaved>(await NextAsync(events));
            Assert.Equal([0, 1], made.Select(w => w.Resets));
            var path2 = writes.Last();
            Assert.Equal([$"begin {path1} {ProfileId}", $"end {path1} Failed", $"begin {path2} {ProfileId}", $"end {path2} Written"], library.Calls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AReserveThatFailsReportsSaveFailedAndTellsTheLibraryNothing()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            // The Output Folder lies under a file, so it can't be created.
            var file = Path.Combine(folder, "a-file");
            File.WriteAllText(file, "x");
            _document = Revision(3) with { Global = SettingsDocument.Empty.Global.SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)) };
            var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
            var library = new FakeLibraryWrites();
            var factory = new FakeFactory { Save = _ => new FakeWrite(new ConcurrentQueue<string>()) };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                clips: new ReplayClips(
                    OutputPathAllocator.System,
                    () => Path.Combine(file, "clips"),
                    s => events.Writer.TryWrite(s),
                    _ => { },
                    f => events.Writer.TryWrite(f),
                    Saving: s => events.Writer.TryWrite(s),
                    Library: library));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);

            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, System.Diagnostics.Stopwatch.GetTimestamp()));
            Assert.IsType<ClipSaving>(await NextAsync(events));
            Assert.Equal(new ClipFailed(ErrorCodes.SaveFailed), await NextAsync(events));
            Assert.Empty(library.Calls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AProfilesVideoAndOwnMicApplyAtStartAndAnotherGameGetsTheGlobalOnes()
    {
        var globalMic = new LayoutDevice(Guid.NewGuid(), "global-mic", "Global mic", "", AudioFlow.Input, IsMic: true);
        var ownMic = new LayoutDevice(Guid.NewGuid(), "own-mic", "Own mic", "", AudioFlow.Input, IsMic: true);
        var plainGlobalMic = globalMic with { Key = Guid.NewGuid(), IsMic = false };
        var global = new TrackLayout([globalMic], [new Track(Guid.NewGuid(), "Mic", [globalMic.Key], MuteAffected: true)]);
        var own = new TrackLayout(
            [plainGlobalMic, ownMic],
            [new Track(Guid.NewGuid(), "Voice", [plainGlobalMic.Key], MuteAffected: true), new Track(Guid.NewGuid(), "Mic", [ownMic.Key], MuteAffected: true)]);
        var video = new VideoSettings(VideoCodec.H264, 1920, 1080, 60, 20_000, RateControl.Vbr);
        const string other = @"c:\games\other.exe";
        _document = Revision(3) with
        {
            Global = SettingsDocument.Empty.Global.SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(global)),
            Profiles = SettingsMaps.EmptyProfiles
                .Add(ProfileId, new GameProfile
                {
                    Name = "Game",
                    Overrides = SettingsMaps.EmptyValues
                        .Add(Keys.Video.Name, Keys.Video.ToJson(video))
                        .Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(own)),
                })
                .Add(other, new GameProfile { Name = "Other" }),
        };
        using var orchestrator = new SessionOrchestrator(
            _factory,
            () => Volatile.Read(ref _document),
            _ => { },
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, _) => { }));

        orchestrator.Post(new ProposeTarget(new TargetCandidate(Pid, 1, ProfileId, "Game.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => _factory.Sessions.Count == 1);
        orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));
        orchestrator.Post(new ProposeTarget(new TargetCandidate(Pid + 1, 2, other, "Other.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => _factory.Sessions.Count == 2);

        var (first, second) = (_factory.Sessions[0].Spec, _factory.Sessions[1].Spec);
        Assert.Equal(video, first.Snapshot.Video);
        Assert.Equal([("Mic", "own-mic")], first.Audio.GatedMicInputs().Select(i => (i.Track.Name, i.Device.EndpointId)));
        Assert.Equal(SettingsResolver.Resolve(_document, null).Video, second.Snapshot.Video);
        Assert.NotEqual(video, second.Snapshot.Video);
        Assert.Equal([("Mic", "global-mic")], second.Audio.GatedMicInputs().Select(i => (i.Track.Name, i.Device.EndpointId)));
    }

    [Fact]
    public async Task ASettingsChangeNeedingAnotherChainIsPreparedDroppedOnARevertAndTakenAtTheSwitchingSave()
    {
        var folder = Directory.CreateTempSubdirectory("rb-clips-").FullName;
        try
        {
            var game = new LayoutDevice(Guid.NewGuid(), "game", "Game", "", AudioFlow.Output, IsMic: false);
            var mic = new LayoutDevice(Guid.NewGuid(), "mic", "Mic", "", AudioFlow.Input, IsMic: true);
            var mic2 = new LayoutDevice(Guid.NewGuid(), "mic2", "Mic 2", "", AudioFlow.Input, IsMic: true);
            TrackLayout Global(bool muteAffected) => new(
                [game, mic],
                [new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false), new Track(Guid.NewGuid(), "Mic", [mic.Key], muteAffected)]);
            var changed = new TrackLayout(
                [game, mic with { IsMic = false }, mic2],
                [
                    new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false),
                    new Track(Guid.NewGuid(), "Voice", [mic.Key], MuteAffected: false),
                    new Track(Guid.NewGuid(), "Mic2", [mic2.Key], MuteAffected: true),
                ]);
            SettingsDocument With(TrackLayout globalLayout, TrackLayout? profileLayout)
            {
                var document = Revision(3) with { Global = InFolder(folder, cooldown: 0).SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(globalLayout)) };
                return profileLayout is null
                    ? document
                    : document with { Profiles = document.Profiles.SetItem(ProfileId, document.Profiles[ProfileId] with { Overrides = document.Profiles[ProfileId].Overrides.SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(profileLayout)) }) };
            }

            var start = With(Global(muteAffected: true), null);
            _document = start;
            var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
            var presses = System.Threading.Channels.Channel.CreateUnbounded<HotkeyPress>();
            var writes = new ConcurrentQueue<string>();
            FakeSession? session = null;
            var factory = new FakeFactory { Save = _ => new FakeWrite(writes) { SwitchesTo = session!.Prepares.Last() } };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                _ => { },
                press => presses.Writer.TryWrite(press),
                targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, _) => { }),
                clips: new ReplayClips(OutputPathAllocator.System, () => folder, s => saved.Writer.TryWrite(s), _ => { }));
            orchestrator.Post(new ProposeTarget(new TargetCandidate(Pid, 1, ProfileId, "Game.exe"), TargetChangeReason.Detected));
            await WaitUntil(() => factory.Sessions.Count == 1);
            session = factory.Sessions[0];

            // The profile's own layout: one chain with its Tracks.
            Volatile.Write(ref _document, With(Global(muteAffected: true), changed));
            orchestrator.Post(new SettingsUpdated());
            await WaitUntil(() => session.Prepares.Count == 1);
            Assert.Equal(["Game", "Voice", "Mic2"], session.Prepares.Last()!.Audio.Tracks.Select(t => t.Name));

            // Back to the session's own: the prepared chain is dropped.
            Volatile.Write(ref _document, start);
            orchestrator.Post(new SettingsUpdated());
            await WaitUntil(() => session.Prepares.Count == 2);
            Assert.Null(session.Prepares.Last());

            // Only muteAffected changes: still another chain.
            Volatile.Write(ref _document, With(Global(muteAffected: false), null));
            orchestrator.Post(new SettingsUpdated());
            await WaitUntil(() => session.Prepares.Count == 3);
            Assert.Empty(session.Prepares.Last()!.Audio.GatedMicInputs());

            // The save that switches to it: its Reset, and a later commit with no change, ask for nothing.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, System.Diagnostics.Stopwatch.GetTimestamp()));
            await NextAsync(saved);
            orchestrator.Post(new SettingsUpdated());
            orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, System.Diagnostics.Stopwatch.GetTimestamp()));
            while ((await NextPress(presses)).Action != HotkeyAction.RecordToggle)
            {
            }

            Assert.Equal(3, session.Prepares.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AChainChangeDuringARecordingWaitsForItsStopAndTheNextSaveSwitches()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            using var rig = NewChainRecordingRig(folder);
            var (orchestrator, session) = (rig.Orchestrator, await rig.StartAsync());
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            var second = System.Diagnostics.Stopwatch.Frequency;

            await Press(orchestrator, rig.Presses, HotkeyAction.RecordToggle, qpc);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(rig.Events));

            // A change mid-Recording: nothing prepared, one Info line.
            rig.ChangeVideo();
            orchestrator.Post(new SettingsUpdated());
            await Press(orchestrator, rig.Presses, HotkeyAction.MicMute, qpc + 1);
            Assert.Empty(session.Prepares);
            Assert.Single(rig.Deferrals);

            // Saves (and their Resets) during it prepare nothing and log nothing more.
            for (var i = 1; i <= 2; i++)
            {
                orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, qpc + (2 * i * second)));
                await NextAsync(rig.Saved);
            }

            Assert.Empty(session.Prepares);
            Assert.Single(rig.Deferrals);

            // The stop prepares it, and the next save switches to it.
            await Press(orchestrator, rig.Presses, HotkeyAction.RecordToggle, qpc + (6 * second));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await NextAsync(rig.Events));
            await WaitUntil(() => session.Prepares.Count == 1);
            Assert.Equal(ChainRecordingRig.Changed, session.Prepares.Single()!.Snapshot.Video);
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, qpc + (8 * second)));
            await NextAsync(rig.Saved);
            Assert.Equal([false, false, true], rig.Writes.Select(w => w.SwitchesTo is not null));
            Assert.Single(session.Prepares);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AChainPendingAtARecordingsStartIsDroppedAndPreparedAgainAfterItsStop()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            using var rig = NewChainRecordingRig(folder);
            var (orchestrator, session) = (rig.Orchestrator, await rig.StartAsync());
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            var second = System.Diagnostics.Stopwatch.Frequency;

            rig.ChangeVideo();
            orchestrator.Post(new SettingsUpdated());
            await WaitUntil(() => session.Prepares.Count == 1);

            // The Recording starts on the session's chain; the prepared one is dropped.
            await Press(orchestrator, rig.Presses, HotkeyAction.RecordToggle, qpc);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(rig.Events));
            Assert.Equal([ChainRecordingRig.Changed, null], session.Prepares.Select(p => p?.Snapshot.Video));
            Assert.Single(rig.Deferrals);
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, qpc + (2 * second)));
            await NextAsync(rig.Saved);
            Assert.Null(Assert.Single(rig.Writes).SwitchesTo);

            await Press(orchestrator, rig.Presses, HotkeyAction.RecordToggle, qpc + (4 * second));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await NextAsync(rig.Events));
            await WaitUntil(() => session.Prepares.Count == 3);
            Assert.Equal(ChainRecordingRig.Changed, session.Prepares.Last()!.Snapshot.Video);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A game-session orchestrator with Replay Clips and Recordings, whose writes switch to the chain last prepared.</summary>
    private ChainRecordingRig NewChainRecordingRig(string folder)
    {
        _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
        SetRecordingsEnabled(true);
        return new ChainRecordingRig(this, folder);
    }

    private sealed class ChainRecordingRig : IDisposable
    {
        public static readonly VideoSettings Changed = new(VideoCodec.H264, 1280, 720, 60, 20_000, RateControl.Vbr);

        private readonly SessionOrchestratorTests _owner;
        private readonly ConcurrentQueue<string> _info = new();
        private readonly ConcurrentQueue<FakeWrite> _writes = new();
        private FakeSession? _session;

        public ChainRecordingRig(SessionOrchestratorTests owner, string folder)
        {
            _owner = owner;
            var paths = new ConcurrentQueue<string>();
            var factory = new FakeFactory
            {
                Save = _ =>
                {
                    var write = new FakeWrite(paths) { SwitchesTo = _session!.Prepares.LastOrDefault() };
                    _writes.Enqueue(write);
                    return write;
                },
                Record = _ => new FakeRecording(),
            };
            Factory = factory;
            Orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref owner._document),
                _ => { },
                press => Presses.Writer.TryWrite(press),
                clips: new ReplayClips(OutputPathAllocator.System, () => folder, s => Saved.Writer.TryWrite(s), e => Events.Writer.TryWrite(e)),
                recordings: new Recordings(
                    OutputPathAllocator.System,
                    () => folder,
                    s => Events.Writer.TryWrite(s),
                    s => Events.Writer.TryWrite(s),
                    e => Events.Writer.TryWrite(e),
                    Info: _info.Enqueue));
        }

        public SessionOrchestrator Orchestrator { get; }

        public FakeFactory Factory { get; }

        public System.Threading.Channels.Channel<object> Events { get; } = System.Threading.Channels.Channel.CreateUnbounded<object>();

        public System.Threading.Channels.Channel<HotkeyPress> Presses { get; } = System.Threading.Channels.Channel.CreateUnbounded<HotkeyPress>();

        public System.Threading.Channels.Channel<ClipSaved> Saved { get; } = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();

        public IEnumerable<FakeWrite> Writes => _writes;

        /// <summary>The Info lines saying a chain change waits for the Recording.</summary>
        public IEnumerable<string> Deferrals => _info.Where(i => i.Contains("once the Recording stops", StringComparison.Ordinal));

        public async Task<FakeSession> StartAsync()
        {
            await Orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            return _session = Assert.Single(Factory.Sessions);
        }

        /// <summary>Commits the profile's video as <see cref="Changed"/>, which needs another chain.</summary>
        public void ChangeVideo()
        {
            var document = Volatile.Read(ref _owner._document);
            var profile = document.Profiles[ProfileId];
            Volatile.Write(ref _owner._document, document with { Profiles = document.Profiles.SetItem(ProfileId, profile with { Overrides = profile.Overrides.SetItem(Keys.Video.Name, Keys.Video.ToJson(Changed)) }) });
        }

        public void Dispose() => Orchestrator.Dispose();
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The Global values with the Output Folder <paramref name="folder"/> and, when given, <paramref name="cooldown"/> as <c>saveCooldown</c>.</summary>
    private static System.Collections.Immutable.ImmutableSortedDictionary<string, System.Text.Json.JsonElement> InFolder(string folder, int? cooldown = null)
    {
        var global = SettingsDocument.Empty.Global.SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(folder));
        return cooldown is { } seconds ? global.SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(seconds)) : global;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20, Token);
        }
    }

    private static async Task<T> NextAsync<T>(System.Threading.Channels.Channel<T> channel) =>
        await channel.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

    [Fact]
    public async Task ASavedUnmuteIsTheFirstStatePublishedAndInTheFirstSessionsSpec()
    {
        const bool saved = false;
        var store = new FakeMicMuteStore(saved);
        var published = new ConcurrentQueue<CaptureState>();
        var factory = new FakeFactory();
        using (var orchestrator = new SessionOrchestrator(factory, () => _document, published.Enqueue, micMute: store))
        {
            var running = await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);

            Assert.Equal(saved, running.Spec.MicMuted);
            Assert.Equal(saved, Assert.Single(factory.Sessions).Spec.MicMuted);
        }

        // An unmute differs from the initial state, so it is published before anything else.
        var expected = new List<CaptureState> { CaptureState.Initial with { MicMuted = false } };
        expected.Add(CaptureState.Initial with { MicMuted = saved, Replay = ReplayState.Buffering });
        expected.Add(CaptureState.Initial with { MicMuted = saved });
        Assert.Equal(expected, published);
        Assert.Equal(1, store.Loads);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task AMicMutePressMidSessionHandsItsQpcToTheSession()
    {
        var store = new FakeMicMuteStore(true);
        var (orchestrator, published, presses) = NewOrchestrator(store, out var factory);
        using (orchestrator)
        {
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);

            orchestrator.Post(new HotkeyPressed(HotkeyAction.MicMute, 1_000));
            orchestrator.Post(new HotkeyPressed(HotkeyAction.MicMute, 4_000));
            await NextPress(presses);
            await NextPress(presses);

            var session = Assert.Single(factory.Sessions);
            Assert.Equal([(false, 1_000L), (true, 4_000L)], session.MicMutes);
            Assert.Equal([orchestrator.ThreadId], session.ThreadIds.Distinct());
            Assert.Equal([true, false, true], published.Select(p => p.MicMuted));
            Assert.Equal([false, true], store.Saves);
        }
    }

    [Fact]
    public async Task ARecordTogglePressRecordsOnlyWhenTheProfileEnablesItAndTheNextOneStopsIt()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
            var presses = System.Threading.Channels.Channel.CreateUnbounded<HotkeyPress>();
            var published = new ConcurrentQueue<CaptureState>();
            var library = new FakeLibraryWrites();
            var made = new ConcurrentQueue<FakeRecording>();
            var factory = new FakeFactory
            {
                Record = _ =>
                {
                    var recording = new FakeRecording();
                    made.Enqueue(recording);
                    return recording;
                },
            };
            using var orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref _document),
                published.Enqueue,
                press => presses.Writer.TryWrite(press),
                recordings: new Recordings(
                    OutputPathAllocator.System,
                    () => throw new InvalidOperationException("the Global folder is set"),
                    s => events.Writer.TryWrite(s),
                    s => events.Writer.TryWrite(s),
                    e => events.Writer.TryWrite(e),
                    library,
                    Open: library));
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var press = System.Diagnostics.Stopwatch.GetTimestamp();

            // recordingsEnabled off: nothing at all.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, press));
            await NextPress(presses);
            Assert.Empty(made);
            Assert.Empty(library.Calls);
            Assert.False(events.Reader.TryRead(out _));
            Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
            Assert.Equal([RecordingState.Off], published.Select(p => p.Recording));

            // Read live at the press.
            var document = Volatile.Read(ref _document);
            Volatile.Write(ref _document, document with { Profiles = document.Profiles.SetItem(ProfileId, document.Profiles[ProfileId] with { RecordingsEnabled = true }) });
            orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, press + 1));
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));
            var recording = Assert.Single(made);
            Assert.Equal(started.Path, recording.Path);
            Assert.Equal(Path.Combine(folder, "Game"), Path.GetDirectoryName(started.Path));
            Assert.Equal([RecordingState.Off, RecordingState.Running], published.Select(p => p.Recording));

            orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, press + 2));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            Assert.Equal([press + 2], recording.Stops);
            // The path is open from just after the Library's begin to just after its end (FR-12).
            Assert.Equal([$"begin {started.Path} {ProfileId}", $"opened {started.Path}", $"end {started.Path} Written", $"closed {started.Path}"], library.Calls);
            Assert.Equal([RecordingState.Off, RecordingState.Running, RecordingState.Off], published.Select(p => p.Recording));
            Assert.Single(made);

            // A session ending under a Recording stops it as session-ended once its writer finishes.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, press + 3));
            var again = Assert.IsType<RecordingStarted>(await NextAsync(events));
            await orchestrator.EndAsync().WaitAsync(Timeout, Token);
            made.Last().Finish();
            Assert.Equal(new RecordingStopped(again.Path, RecordingStopReasons.SessionEnded), await NextAsync(events));
            Assert.Equal(RecordingState.Off, published.Last().Recording);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheRecordHotkeyModeIsReadAtEachPressAndOnlyItsActionsAct()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var (orchestrator, events, presses, made, published, _) = NewRecordingOrchestrator();
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();

            // Toggle mode (the default): Start and Stop do nothing; press, press gives start, stop.
            await Press(orchestrator, presses, HotkeyAction.RecordStart, qpc);
            await Press(orchestrator, presses, HotkeyAction.RecordStop, qpc + 1);
            Assert.Empty(made);
            Assert.False(events.Reader.TryRead(out _));
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 2);
            var first = Assert.IsType<RecordingStarted>(await NextAsync(events));
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 3);
            Assert.Equal(new RecordingStopped(first.Path, RecordingStopReasons.Stopped), await NextAsync(events));

            // Separate mode, committed mid-session: the toggle does nothing, Start twice gives one
            // Recording, Stop stops it, and Stop with none running does nothing.
            var document = Volatile.Read(ref _document);
            Volatile.Write(ref _document, document with { Global = document.Global.SetItem(Keys.RecordHotkeyMode.Name, Keys.RecordHotkeyMode.ToJson(RecordHotkeyMode.Separate)) });
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 4);
            Assert.Single(made);
            await Press(orchestrator, presses, HotkeyAction.RecordStart, qpc + 5);
            var second = Assert.IsType<RecordingStarted>(await NextAsync(events));
            await Press(orchestrator, presses, HotkeyAction.RecordStart, qpc + 6);
            Assert.Equal(2, made.Count);
            await Press(orchestrator, presses, HotkeyAction.RecordStop, qpc + 7);
            Assert.Equal(new RecordingStopped(second.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            Assert.Equal([qpc + 7], made.Last().Stops);
            await Press(orchestrator, presses, HotkeyAction.RecordStop, qpc + 8);
            Assert.Equal(2, made.Count);
            Assert.False(events.Reader.TryRead(out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheRecordingsSwitchIsReadAtEachPressAndGatesOnlyTheStart()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var (orchestrator, events, presses, made, published, _) = NewRecordingOrchestrator();
            using var owned = orchestrator;
            var qpc = System.Diagnostics.Stopwatch.GetTimestamp();

            // No Target Game, so no game session: nothing, even with the switch on.
            SetRecordingsEnabled(true);
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            Assert.Empty(made);
            Assert.False(events.Reader.TryRead(out _));

            // Off: the press does nothing, no event and no state change.
            SetRecordingsEnabled(false);
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 1);
            Assert.Empty(made);
            Assert.False(events.Reader.TryRead(out _));
            Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
            Assert.All(published, p => Assert.Equal(RecordingState.Off, p.Recording));

            // Switching it on starts nothing; the next press does.
            SetRecordingsEnabled(true);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 2);
            Assert.Empty(made);
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 3);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));

            // Switching it off mid-Recording leaves it running until it is stopped.
            SetRecordingsEnabled(false);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 4);
            Assert.Empty(made.Single().Stops);
            Assert.False(events.Reader.TryRead(out _));
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 5);
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await NextAsync(events));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private void SetRecordingsEnabled(bool enabled)
    {
        var document = Volatile.Read(ref _document);
        Volatile.Write(ref _document, document with { Profiles = document.Profiles.SetItem(ProfileId, document.Profiles[ProfileId] with { RecordingsEnabled = enabled }) });
    }

    private static async Task Press(SessionOrchestrator orchestrator, System.Threading.Channels.Channel<HotkeyPress> presses, HotkeyAction action, long qpc)
    {
        orchestrator.Post(new HotkeyPressed(action, qpc));
        await NextPress(presses);
    }

    /// <summary>
    /// An orchestrator whose sessions record with <see cref="FakeRecording"/>s; with
    /// <paramref name="targetChanged"/>, it also follows the Target with a game session, and with
    /// <paramref name="drains"/>, it has Replay Clips, so a game session drains on its Target's exit (R1).
    /// </summary>
    private (SessionOrchestrator Orchestrator, System.Threading.Channels.Channel<object> Events, System.Threading.Channels.Channel<HotkeyPress> Presses, ConcurrentQueue<FakeRecording> Made, ConcurrentQueue<CaptureState> Published, FakeFactory Factory) NewRecordingOrchestrator(
        Action<TargetChanged>? targetChanged = null,
        FakeLibraryWrites? library = null,
        bool drains = false,
        int maxRecordings = int.MaxValue,
        Func<long, IClipWrite?>? save = null,
        Func<Recordings, Recordings>? recordingsWith = null,
        DeviceRecovery? deviceRecovery = null,
        IPowerNotifications? power = null,
        IDisplayMonitors? displays = null)
    {
        var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
        var presses = System.Threading.Channels.Channel.CreateUnbounded<HotkeyPress>();
        var made = new ConcurrentQueue<FakeRecording>();
        var published = new ConcurrentQueue<CaptureState>();
        var factory = new FakeFactory
        {
            Save = save,
            Record = _ =>
            {
                if (made.Count >= maxRecordings)
                {
                    return null;
                }

                var recording = new FakeRecording();
                made.Enqueue(recording);
                return recording;
            },
        };
        var orchestrator = new SessionOrchestrator(
            factory,
            () => Volatile.Read(ref _document),
            published.Enqueue,
            press => presses.Writer.TryWrite(press),
            targetChanged: targetChanged,
            targetCapture: targetChanged is null ? null : new TargetCapture(_ => NoAudio, (_, e) => events.Writer.TryWrite(e)),
            clips: drains ? new ReplayClips(OutputPathAllocator.System, () => throw new InvalidOperationException("the Global folder is set"), _ => { }, e => events.Writer.TryWrite(e)) : null,
            recordings: (recordingsWith ?? (r => r))(new Recordings(
                OutputPathAllocator.System,
                () => throw new InvalidOperationException("the Global folder is set"),
                s => events.Writer.TryWrite(s),
                s => events.Writer.TryWrite(s),
                e => events.Writer.TryWrite(e),
                library,
                Open: library,
                Restarted: r => events.Writer.TryWrite(r))),
            deviceRecovery: deviceRecovery,
            power: power,
            displays: displays);
        return (orchestrator, events, presses, made, published, factory);
    }

    private static async Task<HotkeyPress> NextPress(System.Threading.Channels.Channel<HotkeyPress> presses) =>
        await presses.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

    private (SessionOrchestrator Orchestrator, ConcurrentQueue<CaptureState> Published, System.Threading.Channels.Channel<HotkeyPress> Presses) NewOrchestrator(
        IMicMuteStore store, out FakeFactory factory)
    {
        var published = new ConcurrentQueue<CaptureState>();
        var presses = System.Threading.Channels.Channel.CreateUnbounded<HotkeyPress>();
        factory = new FakeFactory();
        var orchestrator = new SessionOrchestrator(factory, () => _document, published.Enqueue, press => presses.Writer.TryWrite(press), store);
        return (orchestrator, published, presses);
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

        /// <summary>What each session's <c>Save</c> returns; none when null.</summary>
        public Func<long, IClipWrite?>? Save { get; init; }

        /// <summary>What each session's <c>Record</c> returns; none when null.</summary>
        public Func<long, IRecordingWrite?>? Record { get; init; }

        /// <summary>Where each session records its <c>SetBufferWindow</c> calls, as "window N".</summary>
        public ConcurrentQueue<string>? Log { get; init; }

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
                var session = new FakeSession(spec, events, _sessions.Count + 1) { SaveWith = Save, RecordWith = Record, Log = Log };
                _sessions.Add(session);
                return session;
            }
        }
    }

    /// <summary>
    /// A write that records its path and completes from another thread, at once or once
    /// <paramref name="gate"/> completes, leaving the reserved file, or failing with
    /// <paramref name="failure"/>; it counts its Resets.
    /// </summary>
    private sealed class FakeWrite(ConcurrentQueue<string> writes, Exception? failure = null, Task? gate = null, ConcurrentQueue<string>? log = null) : IClipWrite
    {
        private int _resets;

        public int Resets => Volatile.Read(ref _resets);

        public SessionSpec? SwitchesTo { get; init; }

        public void Write(string path, Action<Exception?> done)
        {
            writes.Enqueue(path);
            (gate ?? Task.CompletedTask).ContinueWith(_ => done(failure), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        public void Reset()
        {
            Interlocked.Increment(ref _resets);
            log?.Enqueue("reset");
        }
    }

    /// <summary>A Recording that records its path and stops, and finishes from another thread on its stop.</summary>
    private sealed class FakeRecording : IRecordingWrite
    {
        private readonly ConcurrentQueue<long> _stops = new();
        private Action<Exception?>? _done;

        public string? Path { get; private set; }

        public IEnumerable<long> Stops => _stops;

        public void Start(string path, Action<Exception?> done)
        {
            Path = path;
            _done = done;
        }

        public void Stop(long qpc)
        {
            _stops.Enqueue(qpc);
            Finish();
        }

        /// <summary>Finishes as the session's output ending would, from another thread, with <paramref name="error"/> as the writer's.</summary>
        public void Finish(Exception? error = null)
        {
            var done = _done!;
            Task.Run(() => done(error), CancellationToken.None);
        }
    }

    /// <summary>Records each Library call, and each open-Recording call, in order.</summary>
    private sealed class FakeLibraryWrites : ILibraryWrites, IOpenRecordings
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public IEnumerable<string> Calls => _calls;

        public void BeginWrite(string path, string? profileId) => _calls.Enqueue($"begin {path} {profileId}");

        public void EndWrite(string path, WriteOutcome outcome) => _calls.Enqueue($"end {path} {outcome}");

        public void Opened(string path) => _calls.Enqueue($"opened {path}");

        public void Closed(string path) => _calls.Enqueue($"closed {path}");
    }

    /// <summary>A store that starts with <paramref name="saved"/> and records each save.</summary>
    private sealed class FakeMicMuteStore(bool saved) : IMicMuteStore
    {
        private readonly ConcurrentQueue<bool> _saves = new();

        public int Loads { get; private set; }

        public IEnumerable<bool> Saves => _saves;

        public bool Load()
        {
            Loads++;
            return saved;
        }

        public void Save(bool muted) => _saves.Enqueue(muted);
    }

    private sealed class FakeSession(SessionSpec spec, ISessionEvents events, int number) : ICaptureSession
    {
        private readonly ConcurrentQueue<(bool, long)> _micMutes = new();
        private readonly ConcurrentQueue<nint> _retargets = new();
        private int _inside;
        private int _maxConcurrency;
        private int _drains;
        private int _releases;

        public SessionSpec Spec { get; } = spec;

        public ISessionEvents Events { get; } = events;

        public IEnumerable<nint> Retargets => _retargets;

        public ConcurrentQueue<int> ThreadIds { get; } = new();

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public string? EndReason { get; private set; }

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? RetargetFailure { get; set; }

        public Exception? EndFailure { get; set; }

        public IEnumerable<(bool Muted, long Qpc)> MicMutes => _micMutes;

        public Func<long, IClipWrite?>? SaveWith { get; init; }

        public IClipWrite? Save(long qpc) => SaveWith?.Invoke(qpc);

        public Func<long, IRecordingWrite?>? RecordWith { get; init; }

        public IRecordingWrite? Record(long qpc) => RecordWith?.Invoke(qpc);

        public ConcurrentQueue<string>? Log { get; init; }

        public void SetBufferWindow(int seconds) => Log?.Enqueue($"window {seconds}");

        /// <summary>Each <c>PrepareChain</c> call's spec, null for a drop, in order.</summary>
        public ConcurrentQueue<SessionSpec?> Prepares { get; } = new();

        public void PrepareChain(SessionSpec? next) => Prepares.Enqueue(next);

        /// <summary>What <c>Chains</c> reports.</summary>
        public int ChainCount { get; set; } = 1;

        public int Chains => ChainCount;

        /// <summary>Each <c>SwitchChain</c> call's spec, in order; each is taken.</summary>
        public ConcurrentQueue<SessionSpec> Switches { get; } = new();

        public bool SwitchChain(SessionSpec next)
        {
            Switches.Enqueue(next);
            return true;
        }

        private int _recovers;
        private volatile bool _failRecovery;

        /// <summary>How many recovery attempts were asked for.</summary>
        public int Recovers => Volatile.Read(ref _recovers);

        /// <summary>When set, each recovery attempt fails: it posts <c>DeviceLost</c> again, from another thread.</summary>
        public bool FailRecovery
        {
            get => _failRecovery;
            set => _failRecovery = value;
        }

        public bool RecoverDevice()
        {
            Interlocked.Increment(ref _recovers);
            if (FailRecovery)
            {
                Task.Run(() => Events.DeviceLost(new InvalidOperationException("the recovery attempt failed")), CancellationToken.None);
            }

            return true;
        }

        private int _suspends;

        /// <summary>How many times it was asked to suspend for sleep.</summary>
        public int Suspends => Volatile.Read(ref _suspends);

        /// <summary>What <see cref="Suspend"/> answers: false as a session that hasn't anchored would.</summary>
        public bool CanSuspend { get; set; } = true;

        public bool Suspend()
        {
            Interlocked.Increment(ref _suspends);
            return CanSuspend;
        }

        private volatile string? _monitorDevice;

        /// <summary>The monitor a Monitor session captures; null as a game session's.</summary>
        public string? MonitorDevice
        {
            get => _monitorDevice;
            set => _monitorDevice = value;
        }

        private volatile string[]? _monitorDevices;

        /// <summary>Story 11.3: the composited monitors a test set (<see cref="SetMonitorDevices"/>), less the dropped; else <see cref="MonitorDevice"/>.</summary>
        public IReadOnlyList<string> MonitorDevices => _monitorDevices ?? (MonitorDevice is { } device ? [device] : []);

        public ConcurrentQueue<string> Dropped { get; } = new();

        public void SetMonitorDevices(params string[] devices) => _monitorDevices = devices;

        public bool DropMonitor(string device)
        {
            Dropped.Enqueue(device);
            _monitorDevices = [.. MonitorDevices.Where(d => d != device)];
            return true;
        }

        public void SetMicMuted(bool muted, long qpc)
        {
            ThreadIds.Enqueue(Environment.CurrentManagedThreadId);
            _micMutes.Enqueue((muted, qpc));
        }

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

        public int Drains => Volatile.Read(ref _drains);

        public int Releases => Volatile.Read(ref _releases);

        /// <summary>When set, <see cref="Drain"/>'s outcome carries it, as a session that never anchored.</summary>
        public Exception? DrainFailure { get; set; }

        public SessionOutcome Drain(string reason)
        {
            Interlocked.Increment(ref _drains);
            var outcome = End(reason);
            return DrainFailure is { } failure ? outcome with { Failure = failure } : outcome;
        }

        public void Release() => Interlocked.Increment(ref _releases);

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
