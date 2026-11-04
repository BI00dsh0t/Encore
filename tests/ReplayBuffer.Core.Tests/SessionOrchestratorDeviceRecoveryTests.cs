using System.Collections.Concurrent;
using System.Diagnostics;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

/// <summary>GPU driver reset recovery (8.3): the orchestrator's retries, give-up and Recording handovers.</summary>
public sealed partial class SessionOrchestratorTests
{
    [Fact]
    public void DeviceRecoveryRetriesEveryTwoSecondsAndGivesUpAfterSixtyByDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DeviceRecovery.DefaultRetryEvery);
        Assert.Equal(TimeSpan.FromSeconds(60), DeviceRecovery.DefaultGiveUpAfter);
    }

    [Fact]
    public async Task AGpuResetRecoversAtOnceRetriesAFailedAttemptAndHandsTheRecordingOver()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var resets = new ConcurrentQueue<string>();
            var recovered = new ConcurrentQueue<TimeSpan>();
            var retryEvery = TimeSpan.FromMilliseconds(200);
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                deviceRecovery: new DeviceRecovery(Reset: resets.Enqueue, Recovered: recovered.Enqueue, RetryEvery: retryEvery, GiveUpAfter: TimeSpan.FromMinutes(1)));
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var session = factory.Sessions[0];
            var qpc = Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            var a = Assert.IsType<RecordingStarted>(await NextAsync(events));

            // The first loss is reported once and recovered at once.
            session.Events.DeviceLost(new InvalidOperationException("device reset"));
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 1);
            Assert.Single(resets);
            Assert.Equal(1, session.Recovers);

            // A failed attempt is retried after RetryEvery, and not reported again.
            var failed = Stopwatch.StartNew();
            session.Events.DeviceLost(new InvalidOperationException("the recovery attempt failed"));
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 2);
            Assert.Equal(1, session.Recovers);
            await WaitUntil(() => session.Recovers == 2);
            Assert.True(failed.Elapsed >= retryEvery * 0.75, $"retried after {failed.Elapsed}");
            Assert.Single(resets);

            // The attempt succeeds: Recovered, then the restart's handover, the Recording on throughout.
            session.Events.ChainRestarted(qpc + 3);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 4);
            Assert.Single(recovered);
            Assert.Equal(2, made.Count);
            made.First().Finish();
            Assert.Equal(new RecordingStopped(a.Path, RecordingStopReasons.EncoderRestart), await NextAsync(events));
            var b = Assert.IsType<RecordingStarted>(await NextAsync(events));
            Assert.Equal(new RecordingRestart(a.Path, b.Path), await NextAsync(events));
            Assert.Equal([RecordingState.Off, RecordingState.Running], RecordingStates(published));
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);

            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 5);
            Assert.Equal(new RecordingStopped(b.Path, RecordingStopReasons.Stopped), await NextAsync(events));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ARecoveryThatKeepsFailingGivesUpAndTheTargetStaysOffUntilAnotherOne()
    {
        var failures = new ConcurrentQueue<string>();
        var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
        var (orchestrator, _, presses, _, published, factory) = NewRecordingOrchestrator(
            c => changes.Writer.TryWrite(c),
            deviceRecovery: new DeviceRecovery(Failed: failures.Enqueue, RetryEvery: TimeSpan.FromMilliseconds(50), GiveUpAfter: TimeSpan.FromMilliseconds(400)));
        using var owned = orchestrator;
        var game = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
        orchestrator.Post(new ProposeTarget(game, TargetChangeReason.Detected));
        await NextAsync(changes);
        await WaitUntil(() => factory.Sessions.Count == 1);
        var session = factory.Sessions[0];
        session.FailRecovery = true;

        session.Events.DeviceLost(new InvalidOperationException("device reset"));
        await WaitUntil(() => !failures.IsEmpty);
        Assert.Equal("gpu reset failed", session.EndReason);
        Assert.True(session.Recovers >= 2, $"{session.Recovers} attempts");
        Assert.Equal(ReplayState.Off, published.Last().Replay);

        // A new window of the same Target doesn't restart the Replay Buffer.
        orchestrator.Post(new ProposeTarget(game with { Window = 2 }, TargetChangeReason.Focus));
        await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
        Assert.Single(factory.Sessions);

        // Another Target does, and the engine went on throughout.
        var other = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");
        orchestrator.Post(new ProposeTarget(other, TargetChangeReason.Focus));
        await NextAsync(changes);
        await WaitUntil(() => factory.Sessions.Count == 2);
        Assert.Equal(ReplayState.Buffering, published.Last().Replay);
        Assert.Single(failures);
    }

    [Fact]
    public async Task ATargetExitDuringTheOutageDrainsTheSessionAndStopsItsRetries()
    {
        var failures = new ConcurrentQueue<string>();
        var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
        var (orchestrator, _, presses, _, published, factory) = NewRecordingOrchestrator(
            c => changes.Writer.TryWrite(c),
            drains: true,
            deviceRecovery: new DeviceRecovery(Failed: failures.Enqueue, RetryEvery: TimeSpan.FromMilliseconds(50), GiveUpAfter: TimeSpan.FromMilliseconds(300)));
        using var owned = orchestrator;
        var game = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
        orchestrator.Post(new ProposeTarget(game, TargetChangeReason.Detected));
        await NextAsync(changes);
        await WaitUntil(() => factory.Sessions.Count == 1);
        var session = factory.Sessions[0];
        session.FailRecovery = true;

        session.Events.DeviceLost(new InvalidOperationException("device reset"));
        await WaitUntil(() => session.Recovers >= 1);
        orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit));
        await NextAsync(changes);
        await WaitUntil(() => session.EndReason is not null);
        var attempts = session.Recovers;

        // Past the give-up time: no further attempt, no gpu-reset-failed, and the drain holds the ring.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Token);
        await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
        Assert.Equal(attempts, session.Recovers);
        Assert.Empty(failures);
        Assert.Equal(ReplayState.Draining, published.Last().Replay);
    }

    [Fact]
    public async Task AMonitorSessionsGpuResetHandsItsMonitorRecordingOverWithTheStateRunningThroughout()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var resets = new ConcurrentQueue<string>();
            var recovered = new ConcurrentQueue<TimeSpan>();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                deviceRecovery: new DeviceRecovery(Reset: resets.Enqueue, Recovered: recovered.Enqueue));
            using var owned = orchestrator;
            var qpc = Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc);
            var session = Assert.Single(factory.Sessions);
            session.Events.Anchored(qpc + 1);
            var a = Assert.IsType<RecordingStarted>(await NextAsync(events));

            session.Events.DeviceLost(new InvalidOperationException("device reset"));
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 2);
            Assert.Single(resets);
            Assert.Equal(1, session.Recovers);

            // B starts at once in Monitor Recordings; nothing is published until A's writer posts back.
            session.Events.ChainRestarted(qpc + 3);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 4);
            Assert.Single(recovered);
            Assert.Equal(2, made.Count);
            Assert.False(events.Reader.TryRead(out _));
            made.First().Finish();
            Assert.Equal(new RecordingStopped(a.Path, RecordingStopReasons.EncoderRestart), await NextAsync(events));
            var b = Assert.IsType<RecordingStarted>(await NextAsync(events));
            Assert.Equal(Path.GetDirectoryName(a.Path), Path.GetDirectoryName(b.Path));
            Assert.Equal(new RecordingRestart(a.Path, b.Path), await NextAsync(events));
            Assert.Null(session.EndReason);
            List<RecordingState> monitorStates = [RecordingState.Off, .. published.Select(s => s.MonitorRecording)];
            Assert.Equal(
                [RecordingState.Off, RecordingState.Running],
                monitorStates.Where((r, i) => i == 0 || r != monitorStates[i - 1]));

            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc + 5);
            Assert.Equal(new RecordingStopped(b.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            await WaitUntil(() => session.EndReason is not null);
            Assert.Equal(RecordingState.Off, published.Last().MonitorRecording);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
