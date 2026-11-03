using System.Collections.Concurrent;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Library;
using ReplayBuffer.Core.Output;
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
    public async Task ARunningRecordingHoldsASwitchUntilItClearsThenItCommitsAtOnce()
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
        using var orchestrator = new SessionOrchestrator(_factory, () => Volatile.Read(ref _document), _ => { }, targetChanged: c => changes.Writer.TryWrite(c));
        var a = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
        var b = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");
        var c = new TargetCandidate(Pid + 2, 5, @"c:\games\third.exe", "Third.exe");

        orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
        orchestrator.Post(new RecordingRunning(true));
        orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Focus));
        orchestrator.Post(new ProposeTarget(c, TargetChangeReason.Focus));

        // A proposal of the Target itself drops the held switch: clearing the flag then commits nothing.
        orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Focus));
        orchestrator.Post(new RecordingRunning(false));
        orchestrator.Post(new RecordingRunning(true));
        orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Focus));
        await HandledAsync(orchestrator);
        Assert.Equal(new TargetChanged(null, a.ToTargetGame(), TargetChangeReason.Detected), await NextAsync(changes));
        Assert.False(changes.Reader.TryRead(out _));

        orchestrator.Post(new RecordingRunning(false));

        Assert.Equal(new TargetChanged(a.ToTargetGame(), b.ToTargetGame(), TargetChangeReason.Focus), await NextAsync(changes));
    }

    [Theory]
    [InlineData(TargetChangeReason.Exit)]
    [InlineData(TargetChangeReason.Ignored)]
    public async Task AnExitOrIgnoredHandoverCommitsThroughARunningRecording(TargetChangeReason reason)
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
        using var orchestrator = new SessionOrchestrator(_factory, () => Volatile.Read(ref _document), _ => { }, targetChanged: c => changes.Writer.TryWrite(c));
        var a = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
        var b = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");

        orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
        orchestrator.Post(new RecordingRunning(true));
        orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Focus));
        orchestrator.Post(new ProposeTarget(b, reason));
        await HandledAsync(orchestrator);

        Assert.Equal(new TargetChanged(null, a.ToTargetGame(), TargetChangeReason.Detected), await NextAsync(changes));
        Assert.Equal(new TargetChanged(a.ToTargetGame(), b.ToTargetGame(), reason), await NextAsync(changes));
        Assert.False(changes.Reader.TryRead(out _));
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
            Assert.Equal([ReplayState.Buffering, ReplayState.Draining, ReplayState.Off], published.Select(p => p.Replay));
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
                var session = new FakeSession(spec, events, _sessions.Count + 1) { SaveWith = Save };
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
    private sealed class FakeWrite(ConcurrentQueue<string> writes, Exception? failure = null, Task? gate = null) : IClipWrite
    {
        private int _resets;

        public int Resets => Volatile.Read(ref _resets);

        public void Write(string path, Action<Exception?> done)
        {
            writes.Enqueue(path);
            (gate ?? Task.CompletedTask).ContinueWith(_ => done(failure), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        public void Reset() => Interlocked.Increment(ref _resets);
    }

    /// <summary>Records each Library call, in order.</summary>
    private sealed class FakeLibraryWrites : ILibraryWrites
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public IEnumerable<string> Calls => _calls;

        public void BeginWrite(string path, string? profileId) => _calls.Enqueue($"begin {path} {profileId}");

        public void EndWrite(string path, WriteOutcome outcome) => _calls.Enqueue($"end {path} {outcome}");
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
