using System.Collections.Concurrent;
using System.Diagnostics;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;

namespace Encore.Core.Tests;

/// <summary>Monitor changes (8.5, T-03): what a removed monitor ends and what a display change leaves alone.</summary>
public sealed partial class SessionOrchestratorTests
{
    private static readonly DisplayMonitor Display1 = new(@"\\.\DISPLAY1", "DELL", 2560, 1440, true);
    private static readonly DisplayMonitor Display2 = new(@"\\.\DISPLAY2", "LG", 1920, 1080, false);

    [Fact]
    public async Task ARemovedMonitorEndsItsMonitorRecordingWithMonitorRemovedAndGameCaptureContinues()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var warns = new ConcurrentQueue<string>();
            var displays = new FakeDisplays(Display1, Display2);
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { Warn = (code, _) => warns.Enqueue(code) },
                displays: displays);
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var game = factory.Sessions[0];
            var m = await StartMonitorRecording(orchestrator, events, presses, factory, Display2.DeviceName);
            var monitor = factory.Sessions[1];

            // The primary changes first: neither session notices.
            displays.Change(Display1 with { Primary = false }, Display2 with { Primary = true });
            await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
            Assert.Null(monitor.EndReason);

            // Then DISPLAY2 is unplugged: the Monitor Recording ends at its last frame, its file kept.
            displays.Change(Display1);
            await WaitUntil(() => monitor.EndReason is not null);
            Assert.Single(made).Finish();
            Assert.Equal(new RecordingStopped(m.Path, RecordingStopReasons.MonitorRemoved), await NextAsync(events));
            await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
            Assert.Equal("monitor removed", monitor.EndReason);
            Assert.Equal([ErrorCodes.MonitorRemoved], warns);
            Assert.Equal(RecordingState.Off, published.Last().MonitorRecording);

            // Game capture is of a window: the game session runs on, untouched.
            Assert.Null(game.EndReason);
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);
            Assert.Equal(2, factory.Sessions.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AnotherMonitorsRemovalChangesNothingAndACloseAfterItsOwnRemovalEndsAsRemoved()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var warns = new ConcurrentQueue<string>();
            var displays = new FakeDisplays(Display1, Display2);
            var (orchestrator, events, presses, made, _, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { Warn = (code, _) => warns.Enqueue(code) },
                displays: displays);
            using var owned = orchestrator;
            var m = await StartMonitorRecording(orchestrator, events, presses, factory, Display1.DeviceName);
            var monitor = factory.Sessions[0];

            displays.Change(Display1);
            await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
            Assert.Null(monitor.EndReason);
            Assert.Empty(warns);

            // WGC sees its monitor go before the display change arrives: the close ends it as removed.
            displays.Set(Display2 with { Primary = true });
            monitor.Events.Closed("window closed");
            await WaitUntil(() => monitor.EndReason is not null);
            Assert.Single(made).Finish();
            Assert.Equal(new RecordingStopped(m.Path, RecordingStopReasons.MonitorRemoved), await NextAsync(events));
            Assert.Equal([ErrorCodes.MonitorRemoved], warns);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A Monitor Record press, its session capturing <paramref name="device"/>, anchored: the Monitor Recording started.</summary>
    private static async Task<RecordingStarted> StartMonitorRecording(
        SessionOrchestrator orchestrator,
        System.Threading.Channels.Channel<object> events,
        System.Threading.Channels.Channel<HotkeyPress> presses,
        FakeFactory factory,
        string device)
    {
        var qpc = Stopwatch.GetTimestamp();
        await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc);
        var session = factory.Sessions[^1];
        Assert.Equal(SessionKind.Monitor, session.Spec.Kind);
        session.MonitorDevice = device;
        session.Events.Anchored(qpc + 1);
        return Assert.IsType<RecordingStarted>(await NextAsync(events));
    }

    /// <summary>A display source a test changes, raising the change from another thread as the window's would.</summary>
    private sealed class FakeDisplays(params DisplayMonitor[] monitors) : IDisplayMonitors
    {
        private volatile DisplayMonitor[] _monitors = monitors;
        private Action? _changed;

        public IReadOnlyList<DisplayMonitor> List() => _monitors;

        public IDisposable Subscribe(Action changed)
        {
            _changed = changed;
            return new Unsubscribe(this);
        }

        /// <summary>Attaches <paramref name="monitors"/> and raises the change.</summary>
        public void Change(params DisplayMonitor[] monitors)
        {
            Set(monitors);
            Task.Run(() => _changed?.Invoke()).Wait(Token);
        }

        /// <summary>Attaches <paramref name="monitors"/> without raising a change.</summary>
        public void Set(params DisplayMonitor[] monitors) => _monitors = monitors;

        private sealed class Unsubscribe(FakeDisplays owner) : IDisposable
        {
            public void Dispose() => owner._changed = null;
        }
    }
}
