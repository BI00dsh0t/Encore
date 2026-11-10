using System.Collections.Concurrent;
using System.Diagnostics;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;

namespace Encore.Core.Tests;

/// <summary>A Monitor Recording of several screens, as the orchestrator resolves and follows them.</summary>
public sealed partial class SessionOrchestratorTests
{
    private static readonly DisplayMonitor Screen1 = new(@"\\.\DISPLAY1", "Odyssey", 2560, 1440, true, "path-1", 0, 0, 240);
    private static readonly DisplayMonitor Screen2 = new(@"\\.\DISPLAY2", "U32", 3840, 2160, false, "path-2", 2560, 0, 144);
    private static readonly DisplayMonitor Screen3 = new(@"\\.\DISPLAY3", "Portable", 1920, 1080, false, "path-3", 6400, 0, 60);

    [Fact]
    public async Task AllScreensStartsOneSessionWithEveryScreenAndAnArrivalOnlyRaisesItsNotice()
    {
        var folder = Directory.CreateTempSubdirectory("rb-screens-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0).SetItem(Keys.MonitorRecordingScreens.Name, Keys.MonitorRecordingScreens.ToJson(MonitorRecordingScreens.AllScreens)) };
            var warns = new ConcurrentQueue<string>();
            var displays = new FakeDisplays(Screen1, Screen2);
            var (orchestrator, events, presses, _, _, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { Warn = (code, _) => warns.Enqueue(code) },
                displays: displays);
            using var owned = orchestrator;
            var monitor = await StartScreens(orchestrator, presses, factory);

            Assert.Equal([Screen1, Screen2], monitor.Spec.Screens);
            Assert.Single(factory.Sessions);

            // A third screen arrives: one notice, and the session (and its canvas) is untouched.
            displays.Change(Screen1, Screen2, Screen3);
            displays.Change(Screen1, Screen2, Screen3);
            await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
            Assert.Equal([ErrorCodes.MonitorAddedNextRecording], warns);
            Assert.Null(monitor.EndReason);
            Assert.Empty(monitor.Dropped);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ARemovedScreenIsDroppedAndTheRecordingEndsOnlyWithTheLast()
    {
        var folder = Directory.CreateTempSubdirectory("rb-screens-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0).SetItem(Keys.MonitorRecordingScreens.Name, Keys.MonitorRecordingScreens.ToJson(MonitorRecordingScreens.Selected("path-1", "path-2"))) };
            var warns = new ConcurrentQueue<string>();
            var displays = new FakeDisplays(Screen1, Screen2);
            var (orchestrator, events, presses, made, _, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { Warn = (code, _) => warns.Enqueue(code) },
                displays: displays);
            using var owned = orchestrator;
            var monitor = await StartScreens(orchestrator, presses, factory);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));

            displays.Change(Screen1);
            await Press(orchestrator, presses, HotkeyAction.MicMute, Stopwatch.GetTimestamp());
            Assert.Equal([Screen2.DeviceName], monitor.Dropped);
            Assert.Null(monitor.EndReason);
            Assert.Equal([ErrorCodes.MonitorRemoved], warns);

            displays.Change(Screen2 with { Primary = true });
            await WaitUntil(() => monitor.EndReason is not null);
            Assert.Single(made).Finish();
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.MonitorRemoved), await NextAsync(events));
            Assert.Equal([ErrorCodes.MonitorRemoved, ErrorCodes.MonitorRemoved], warns);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingScreenIsSkippedWithANoticeAndNoneConnectedRefusesTheStart()
    {
        var folder = Directory.CreateTempSubdirectory("rb-screens-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0).SetItem(Keys.MonitorRecordingScreens.Name, Keys.MonitorRecordingScreens.ToJson(MonitorRecordingScreens.Selected("path-gone", "path-2"))) };
            var warns = new ConcurrentQueue<string>();
            var displays = new FakeDisplays(Screen1, Screen2 with { Width = 0, Height = 0 });
            var (orchestrator, events, presses, _, published, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { Warn = (code, _) => warns.Enqueue(code) },
                displays: displays);
            using var owned = orchestrator;

            // DISPLAY2's mode reads 0x0: as good as gone, so nothing chosen is connected.
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp());
            var refused = Assert.IsType<ErrorCodeException>(await NextAsync(events));
            Assert.Equal(ErrorCodes.MonitorRecordingNoScreens, refused.Code);
            Assert.Empty(factory.Sessions);
            Assert.Equal([ErrorCodes.MonitorMissing, ErrorCodes.MonitorMissing], warns);
            Assert.NotEqual(RecordingState.Running, published.LastOrDefault()?.MonitorRecording);

            // Once DISPLAY2 reads again, only the missing one is skipped.
            displays.Set(Screen1, Screen2);
            var monitor = await StartScreens(orchestrator, presses, factory);
            Assert.Equal([Screen2], monitor.Spec.Screens);
            Assert.Equal(3, warns.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A Monitor Record press: its session gets the resolved screens' devices and anchors.</summary>
    private static async Task<FakeSession> StartScreens(
        SessionOrchestrator orchestrator,
        System.Threading.Channels.Channel<HotkeyPress> presses,
        FakeFactory factory)
    {
        var qpc = Stopwatch.GetTimestamp();
        await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc);
        var session = factory.Sessions[^1];
        Assert.Equal(SessionKind.Monitor, session.Spec.Kind);
        session.SetMonitorDevices([.. session.Spec.Screens!.Select(s => s.DeviceName)]);
        session.Events.Anchored(qpc + 1);
        return session;
    }
}
