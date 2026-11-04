using System.Collections.Concurrent;
using System.Diagnostics;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

/// <summary>Sleep and resume (8.4, T-02): what a suspend stops and what a resume rebuilds.</summary>
public sealed partial class SessionOrchestratorTests
{
    [Fact]
    public async Task ASuspendEndsTheRecordingOnceAndTheResumeRebuildsTheReplayBufferAlone()
    {
        var folder = Directory.CreateTempSubdirectory("rb-recordings-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var warns = new ConcurrentQueue<string>();
            var resets = new ConcurrentQueue<string>();
            var recovered = new ConcurrentQueue<TimeSpan>();
            var power = new FakePower();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { Warn = (code, _) => warns.Enqueue(code) },
                deviceRecovery: new DeviceRecovery(Reset: resets.Enqueue, Recovered: recovered.Enqueue),
                power: power);
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var session = factory.Sessions[0];
            var qpc = Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            var a = Assert.IsType<RecordingStarted>(await NextAsync(events));

            power.Raise(PowerChange.Suspending);
            Assert.Equal(new RecordingStopped(a.Path, RecordingStopReasons.Suspend), await NextAsync(events));
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 1);
            Assert.Equal([ErrorCodes.RecordingSuspended], warns);
            Assert.Equal(1, session.Suspends);
            Assert.Equal(RecordingState.Off, published.Last().Recording);
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);

            // The resume rebuilds; its ChainRestarted starts no Recording and isn't a GPU reset's recovery.
            power.Raise(PowerChange.Resumed);
            power.Raise(PowerChange.Resumed);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 2);
            Assert.Equal(1, session.Recovers);
            session.Events.ChainRestarted(qpc + 3);
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 4);
            Assert.Single(made);
            Assert.Empty(resets);
            Assert.Empty(recovered);
            Assert.False(events.Reader.TryRead(out _));
            Assert.Null(session.EndReason);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AResumeWhoseRebuildKeepsFailingGivesUpAndEndsTheSession()
    {
        var failures = new ConcurrentQueue<string>();
        var power = new FakePower();
        var (orchestrator, _, presses, _, published, factory) = NewRecordingOrchestrator(
            deviceRecovery: new DeviceRecovery(Failed: failures.Enqueue, RetryEvery: TimeSpan.FromMilliseconds(50), GiveUpAfter: TimeSpan.FromMilliseconds(400)),
            power: power);
        using var owned = orchestrator;
        await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
        var session = factory.Sessions[0];
        session.FailRecovery = true;

        power.Raise(PowerChange.Suspending);
        power.Raise(PowerChange.Resumed);
        await WaitUntil(() => !failures.IsEmpty);
        Assert.Contains("after the PC woke", Assert.Single(failures), StringComparison.Ordinal);
        Assert.Equal("gpu reset failed", session.EndReason);
        Assert.True(session.Recovers >= 2, $"{session.Recovers} attempts");
        await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
        Assert.Equal(ReplayState.Off, published.Last().Replay);
    }

    [Fact]
    public async Task ASuspendEndsTheMonitorRecordingWithReasonSuspend()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var warns = new ConcurrentQueue<string>();
            var power = new FakePower();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { Warn = (code, _) => warns.Enqueue(code) },
                power: power);
            using var owned = orchestrator;
            var qpc = Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc);
            var session = Assert.Single(factory.Sessions);
            session.Events.Anchored(qpc + 1);
            var m = Assert.IsType<RecordingStarted>(await NextAsync(events));

            // Ending the Monitor session finishes its output, which ends the writer.
            power.Raise(PowerChange.Suspending);
            await WaitUntil(() => session.EndReason is not null);
            Assert.Single(made).Finish();
            Assert.Equal(new RecordingStopped(m.Path, RecordingStopReasons.Suspend), await NextAsync(events));
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 2);
            Assert.Equal("suspend", session.EndReason);
            Assert.Equal([ErrorCodes.RecordingSuspended], warns);
            Assert.Equal(RecordingState.Off, published.Last().MonitorRecording);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ASessionThatCantSuspendIsLeftAsItIsAndTheResumeAsksNothing()
    {
        var power = new FakePower();
        var (orchestrator, _, presses, _, _, factory) = NewRecordingOrchestrator(power: power);
        using var owned = orchestrator;
        await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
        var session = factory.Sessions[0];
        session.CanSuspend = false;

        power.Raise(PowerChange.Suspending);
        power.Raise(PowerChange.Resumed);
        await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
        Assert.Equal(1, session.Suspends);
        Assert.Equal(0, session.Recovers);
        Assert.Null(session.EndReason);
    }

    [Fact]
    public async Task ASuspendDuringAGpuResetOutageStopsItsRetriesAndTheResumeRecovers()
    {
        var failures = new ConcurrentQueue<string>();
        var resets = new ConcurrentQueue<string>();
        var recovered = new ConcurrentQueue<TimeSpan>();
        var power = new FakePower();
        var (orchestrator, _, presses, _, _, factory) = NewRecordingOrchestrator(
            deviceRecovery: new DeviceRecovery(Reset: resets.Enqueue, Failed: failures.Enqueue, Recovered: recovered.Enqueue, RetryEvery: TimeSpan.FromMilliseconds(50), GiveUpAfter: TimeSpan.FromMilliseconds(300)),
            power: power);
        using var owned = orchestrator;
        await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
        var session = factory.Sessions[0];
        session.Events.DeviceLost(new InvalidOperationException("reset"));
        await WaitUntil(() => session.Recovers >= 1);

        power.Raise(PowerChange.Suspending);
        await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
        var asked = session.Recovers;

        // Past the give-up: asleep, nothing retries and nothing gives up.
        await Task.Delay(500, Token);
        Assert.Equal(asked, session.Recovers);
        Assert.Empty(failures);
        Assert.Null(session.EndReason);

        power.Raise(PowerChange.Resumed);
        await WaitUntil(() => session.Recovers > asked);
        session.Events.ChainRestarted(Stopwatch.GetTimestamp());
        await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
        Assert.Empty(failures);
        Assert.Null(session.EndReason);

        // The resume's rebuild answers the reset raised before the sleep, so its warning is withdrawn.
        Assert.Single(resets);
        Assert.Single(recovered);
    }

    /// <summary>A power source a test raises changes on, from another thread, as the system's callback would.</summary>
    private sealed class FakePower : IPowerNotifications
    {
        private Action<PowerChange>? _changed;

        public IDisposable Subscribe(Action<PowerChange> changed)
        {
            _changed = changed;
            return new Unsubscribe(this);
        }

        public void Raise(PowerChange change) => Task.Run(() => _changed?.Invoke(change)).Wait(Token);

        private sealed class Unsubscribe(FakePower owner) : IDisposable
        {
            public void Dispose() => owner._changed = null;
        }
    }
}
