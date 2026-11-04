using System.Collections.Concurrent;
using System.Diagnostics;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

/// <summary>The Monitor session's slot (6.9): Monitor Recording by its own Hotkey.</summary>
public sealed partial class SessionOrchestratorTests
{
    [Fact]
    public async Task AMonitorToggleWithNoGameRecordsTheGlobalTracksInMonitorRecordingsUntilTheNextToggle()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            var mic = new LayoutDevice(Guid.NewGuid(), "global-mic", "Global mic", "", AudioFlow.Input, IsMic: true);
            var layout = new TrackLayout([mic], [new Track(Guid.NewGuid(), "Mic", [mic.Key], MuteAffected: true)]);
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0).SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)) };
            var library = new FakeLibraryWrites();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(library: library);
            using var owned = orchestrator;
            var qpc = Stopwatch.GetTimestamp();

            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc);
            var session = Assert.Single(factory.Sessions);
            Assert.Equal(SessionKind.Monitor, session.Spec.Kind);
            Assert.Null(session.Spec.Target.ProfileId);
            Assert.Equal([("Mic", "global-mic")], session.Spec.Audio.GatedMicInputs().Select(i => (i.Track.Name, i.Device.EndpointId)));
            Assert.Equal(RecordingState.Running, published.Last().MonitorRecording);

            // The Recording starts at the session's anchor, in Monitor Recordings.
            session.Events.Anchored(qpc + 1);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));
            Assert.Equal(Path.Combine(folder, ProfileFolders.MonitorRecordings), Path.GetDirectoryName(started.Path));
            Assert.StartsWith("Monitor_", Path.GetFileName(started.Path), StringComparison.Ordinal);
            Assert.Equal(started.Path, made.Single().Path);

            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc + 2);
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            await WaitUntil(() => session.EndReason is not null);
            Assert.Equal(RecordingState.Off, published.Last().MonitorRecording);
            Assert.All(published, p => Assert.Equal(RecordingState.Off, p.Recording));

            // Opened and closed as a game Recording is, so a killed engine's file is found and trimmed.
            Assert.Equal(
                [$"begin {started.Path} ", $"opened {started.Path}", $"end {started.Path} Written", $"closed {started.Path}"],
                library.Calls);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AMonitorRecordingBesideAGameRecordingNeitherStopsNorHoldsATargetSwitch()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            SetRecordingsEnabled(true);
            var changes = System.Threading.Channels.Channel.CreateUnbounded<TargetChanged>();
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(c => changes.Writer.TryWrite(c));
            using var owned = orchestrator;
            var a = new TargetCandidate(Pid, 1, ProfileId, "Game.exe");
            var b = new TargetCandidate(Pid + 1, 3, @"c:\games\other.exe", "Other.exe");
            var qpc = Stopwatch.GetTimestamp();

            orchestrator.Post(new ProposeTarget(a, TargetChangeReason.Detected));
            await NextAsync(changes);
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc);
            var game = Assert.IsType<RecordingStarted>(await NextAsync(events));
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc + 1);
            var monitor = factory.Sessions.Single(s => s.Spec.Kind == SessionKind.Monitor);
            monitor.Events.Anchored(qpc + 2);
            var monitorStarted = Assert.IsType<RecordingStarted>(await NextAsync(events));

            // The game Recording holds the switch as before; its stop commits it at once.
            orchestrator.Post(new ProposeTarget(b, TargetChangeReason.Focus));
            await Press(orchestrator, presses, HotkeyAction.MicMute, qpc + 3);
            Assert.False(changes.Reader.TryRead(out _));
            await Press(orchestrator, presses, HotkeyAction.RecordToggle, qpc + 4);
            Assert.Equal(new RecordingStopped(game.Path, RecordingStopReasons.Stopped), await NextAsync(events));
            Assert.Equal(new TargetChanged(a.ToTargetGame(), b.ToTargetGame(), TargetChangeReason.Focus), await NextAsync(changes));

            Assert.Empty(made.Single(r => r.Path == monitorStarted.Path).Stops);
            Assert.Null(monitor.EndReason);
            Assert.Equal(RecordingState.Running, published.Last().MonitorRecording);

            // Stopped before Dispose, which would otherwise wait for its writer.
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc + 5);
            Assert.Equal(new RecordingStopped(monitorStarted.Path, RecordingStopReasons.Stopped), await NextAsync(events));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task InSeparateModeMonitorStartAndStopActAndAStopBeforeTheAnchorLeavesNothing()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with
            {
                Global = InFolder(folder, cooldown: 0).SetItem(Keys.RecordHotkeyMode.Name, Keys.RecordHotkeyMode.ToJson(RecordHotkeyMode.Separate)),
            };
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator();
            using var owned = orchestrator;
            var qpc = Stopwatch.GetTimestamp();

            // The toggle isn't used in separate mode, and a stop with nothing running does nothing.
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc);
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordStop, qpc + 1);
            Assert.Empty(factory.Sessions);

            // A stop before the first frame ends the session with no Recording and no file.
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordStart, qpc + 2);
            var first = Assert.Single(factory.Sessions);
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordStop, qpc + 3);
            Assert.NotNull(first.EndReason);
            Assert.Equal(RecordingState.Off, published.Last().MonitorRecording);
            Assert.Empty(made);
            Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));

            // A start while one runs is ignored.
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordStart, qpc + 4);
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordStart, qpc + 5);
            Assert.Equal(2, factory.Sessions.Count);
            factory.Sessions[1].Events.Anchored(qpc + 6);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordStop, qpc + 7);
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await NextAsync(events));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AMonitorPressIsRefusedForTheEncoderBudgetWhenTheGameSessionStillRunsTwoChains()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var warnings = new ConcurrentQueue<string>();
            var (orchestrator, _, presses, _, published, factory) = NewRecordingOrchestrator(
                drains: true,
                recordingsWith: r => r with { Warn = (code, _) => warnings.Enqueue(code) });
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var game = Assert.Single(factory.Sessions);

            // A chain prepared for the profile's change is dropped first; the session still reports two.
            ChangeProfileVideo(new VideoSettings(VideoCodec.H264, 1280, 720, 60, 20_000, RateControl.Vbr));
            orchestrator.Post(new SettingsUpdated());
            await WaitUntil(() => game.Prepares.Count == 1);
            game.ChainCount = 2;
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp());

            Assert.Null(game.Prepares.Last());
            Assert.Equal([ErrorCodes.EncoderBudget], warnings);
            Assert.Single(factory.Sessions);
            Assert.All(published, p => Assert.Equal(RecordingState.Off, p.MonitorRecording));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AChainChangeDuringAMonitorRecordingIsSwitchedAtTheNextResetNotPrepared()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            using var rig = NewChainRecordingRig(folder);
            var (orchestrator, game) = (rig.Orchestrator, await rig.StartAsync());
            var qpc = Stopwatch.GetTimestamp();
            var second = Stopwatch.Frequency;

            await Press(orchestrator, rig.Presses, HotkeyAction.MonitorRecordToggle, qpc);
            rig.Factory.Sessions[1].Events.Anchored(qpc + 1);
            Assert.IsType<RecordingStarted>(await NextAsync(rig.Events));

            rig.ChangeVideo();
            orchestrator.Post(new SettingsUpdated());
            await Press(orchestrator, rig.Presses, HotkeyAction.MicMute, qpc + 2);
            Assert.Empty(game.Prepares);
            Assert.Empty(game.Switches);

            // The next Clip's Reset switches, tearing down first; the spec takes it, so the next Reset switches nothing.
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, qpc + (2 * second)));
            await NextAsync(rig.Saved);
            Assert.Equal(ChainRecordingRig.Changed, Assert.Single(game.Switches).Snapshot.Video);
            orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, qpc + (4 * second)));
            await NextAsync(rig.Saved);
            Assert.Single(game.Switches);
            Assert.Empty(game.Prepares);
            await Press(orchestrator, rig.Presses, HotkeyAction.MonitorRecordToggle, qpc + (5 * second));
            Assert.IsType<RecordingStopped>(await NextAsync(rig.Events));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AFullDiskStopsTheMonitorRecordingAndEndsItsSessionAndTheGameSessionGoesOn()
    {
        var folder = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
        try
        {
            _document = Revision(3) with { Global = InFolder(folder, cooldown: 0) };
            var diskFulls = 0;
            var (orchestrator, events, presses, made, published, factory) = NewRecordingOrchestrator(
                recordingsWith: r => r with { DiskFull = () => Interlocked.Increment(ref diskFulls) });
            using var owned = orchestrator;
            await orchestrator.StartAsync(SessionKind.Game, Target(window: 1), NoAudio).WaitAsync(Timeout, Token);
            var qpc = Stopwatch.GetTimestamp();
            await Press(orchestrator, presses, HotkeyAction.MonitorRecordToggle, qpc);
            var (game, monitor) = (factory.Sessions[0], factory.Sessions[1]);
            monitor.Events.Anchored(qpc + 1);
            var started = Assert.IsType<RecordingStarted>(await NextAsync(events));

            made.Single().Finish(DiskFullError());

            Assert.IsType<IOException>(await NextAsync(events));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.DiskFull), await NextAsync(events));
            await WaitUntil(() => Volatile.Read(ref diskFulls) == 1 && monitor.EndReason is not null);
            Assert.Null(game.EndReason);
            Assert.Equal(RecordingState.Off, published.Last().MonitorRecording);
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Commits the profile's video as <paramref name="video"/>.</summary>
    private void ChangeProfileVideo(VideoSettings video)
    {
        var document = Volatile.Read(ref _document);
        var profile = document.Profiles[ProfileId];
        Volatile.Write(ref _document, document with { Profiles = document.Profiles.SetItem(ProfileId, profile with { Overrides = profile.Overrides.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) }) });
    }
}
