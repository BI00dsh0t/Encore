using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Monitors;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.Capture;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// Monitor Recording (story 6.9), in process on the real primary monitor: a <c>monitorRecordToggle</c>
/// pair with no game writes one file in <c>Monitor Recordings</c> with every Global Track; a window
/// marked <c>WDA_EXCLUDEFROMCAPTURE</c> never shows in it; and beside a game Recording on a synthetic
/// window, the two sessions run two encoder chains between them. With WGC forced to fail (6.10), the
/// Monitor session records through Desktop Duplication instead, across a lost duplication, while a
/// game session fails. A fake display port (8.5, T-03) that drops the captured monitor ends the Monitor
/// Recording playable with <c>monitor-removed</c>, and a game Recording runs on through display changes.
/// The displays themselves are never changed. On-screen, so each takes the bench lock.
/// </summary>
public sealed class MonitorRecordingCaptureTests : IDisposable
{
    private const string GameWindowScript =
        """
        param([string] $Title)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.Width = 641
        $form.Height = 481
        $start = [Diagnostics.Stopwatch]::StartNew()
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $form.BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
        })
        $timer.Start()
        $form.Show()
        $form.Hide()
        [void] $form.ShowDialog()
        """;

    // A full-screen, topmost magenta window excluded from capture before it first shows.
    private const string ExcludedWindowScript =
        """
        param([string] $Title)
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -Namespace Native -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);'
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.FormBorderStyle = 'None'
        $form.WindowState = 'Maximized'
        $form.TopMost = $true
        $form.BackColor = [Drawing.Color]::Magenta
        $form.Add_HandleCreated({ [void][Native.User32]::SetWindowDisplayAffinity($form.Handle, 0x11) })
        [void] $form.ShowDialog()
        """;

    // Story 12.2: a borderless, animated window at the primary screen's top-left, 1280x720, that
    // resizes itself to 2560x1440 after $After seconds.
    private const string ResizingWindowScript =
        """
        param([string] $Title, [string] $After)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.FormBorderStyle = 'None'
        $form.StartPosition = 'Manual'
        $form.Left = 0
        $form.Top = 0
        $form.Width = 1280
        $form.Height = 720
        $start = [Diagnostics.Stopwatch]::StartNew()
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $form.BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
            if ($form.Width -ne 2560 -and $start.Elapsed.TotalSeconds -ge [double] $After) {
                $form.Width = 2560
                $form.Height = 1440
            }
        })
        $timer.Start()
        [void] $form.ShowDialog()
        """;

    private const string ProfileId = @"c:\games\test game\testgame.exe";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-monitor-").FullName;
    private readonly List<Process> _processes = [];
    private SettingsDocument _document = SettingsDocument.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ATogglePairWithNoGameWritesAPlayableFileWithEveryGlobalTrack()
    {
        using var bench = BenchLock.Take();
        using var rig = NewRig();

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(3));

        Assert.Equal(Path.Combine(_directory, ProfileFolders.MonitorRecordings), Path.GetDirectoryName(file));
        Assert.Equal(["Game", "Mic"], AudioTitles(file));
        Assert.Equal((640, 360), (SessionTestKit.VideoStream(file).Width, SessionTestKit.VideoStream(file).Height));
        SessionTestKit.AssertDecodesCleanly(file);
        Assert.Contains(rig.Published, p => p.MonitorRecording == RecordingState.Running);
        Assert.Equal(RecordingState.Off, rig.Published.Last().MonitorRecording);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AWindowExcludedFromCaptureNeverShowsInTheMonitorRecording()
    {
        using var bench = BenchLock.Take();
        SessionTestKit.RequireFFmpeg();
        SessionTestKit.OpenWindow(_directory, _processes, ExcludedWindowScript);
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        using var rig = NewRig();

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(2));

        // Magenta has both chroma planes near their top; the desktop under it doesn't.
        var frames = SessionTestKit.FrameStats(file, "crop=iw/2:ih/2:iw/4:ih/4");
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.False(f.U > 170 && f.V > 170, $"magenta at {f.Time:0.000} s (U {f.U:0}, V {f.V:0})"));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AMonitorRecordingBesideAGameRecordingRunsTwoChainsBetweenThem()
    {
        using var bench = BenchLock.Take();
        using var rig = NewRig(recordingsEnabled: true);
        var window = SessionTestKit.OpenWindow(_directory, _processes, GameWindowScript);
        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => rig.Factory.Sessions.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        var game = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        var monitor = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        Assert.Equal(2, rig.Factory.Sessions.Sum(s => s.Chains));

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(monitor.Path, RecordingStopReasons.Stopped), await rig.NextAsync());
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(game.Path, RecordingStopReasons.Stopped), await rig.NextAsync());
        rig.Orchestrator.Dispose();

        Assert.Equal((640, 480), (SessionTestKit.VideoStream(game.Path).Width, SessionTestKit.VideoStream(game.Path).Height));
        Assert.Equal((640, 360), (SessionTestKit.VideoStream(monitor.Path).Width, SessionTestKit.VideoStream(monitor.Path).Height));
        SessionTestKit.AssertDecodesCleanly(game.Path);
        SessionTestKit.AssertDecodesCleanly(monitor.Path);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AChainChangeDuringAMonitorRecordingSwitchesAtTheResetTearingDownFirst()
    {
        using var bench = BenchLock.Take();
        var gaps = new ConcurrentQueue<string>();
        using var rig = NewRig(clips: true, resetGap: gaps.Enqueue);
        var window = SessionTestKit.OpenWindow(_directory, _processes, GameWindowScript);
        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => rig.Factory.Sessions.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        var monitor = Assert.IsType<RecordingStarted>(await rig.NextAsync());

        // AD-7's budget, sampled from here to the end: the switch never runs a third chain.
        var most = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(
            async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    most = Math.Max(most, rig.Factory.Sessions.Sum(s => s.Chains));
                    await Task.Delay(5, CancellationToken.None);
                }
            },
            CancellationToken.None);

        // A profile video change that needs another chain, then the save whose Reset switches to it.
        var changed = new VideoSettings(VideoCodec.H264, 1280, 720, 30, 4_000, RateControl.Cbr);
        var document = Volatile.Read(ref _document);
        var profile = document.Profiles[ProfileId];
        Volatile.Write(ref _document, document with { Profiles = document.Profiles.SetItem(ProfileId, profile with { Overrides = profile.Overrides.SetItem(Keys.Video.Name, Keys.Video.ToJson(changed)) }) });
        Assert.True(rig.Orchestrator.Post(new SettingsUpdated()));
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var before = Assert.IsType<ClipSaved>(await rig.NextAsync());
        await WaitUntil(() => !gaps.IsEmpty);

        // The next save comes from the new chain.
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var after = Assert.IsType<ClipSaved>(await rig.NextAsync());
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(monitor.Path, RecordingStopReasons.Stopped), await rig.NextAsync());
        await sampling.CancelAsync();
        await sampler;
        rig.Orchestrator.Dispose();

        Assert.InRange(most, 1, 2);
        Assert.Single(gaps);
        Assert.Equal((640, 480), (SessionTestKit.VideoStream(before.Path).Width, SessionTestKit.VideoStream(before.Path).Height));
        Assert.Equal((1280, 720), (SessionTestKit.VideoStream(after.Path).Width, SessionTestKit.VideoStream(after.Path).Height));
        SessionTestKit.AssertDecodesCleanly(before.Path);
        SessionTestKit.AssertDecodesCleanly(after.Path);
        SessionTestKit.AssertDecodesCleanly(monitor.Path);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task WithWgcFailingAMonitorRecordingFallsBackToDesktopDuplication()
    {
        using var bench = BenchLock.Take();
        var fallbacks = new ConcurrentQueue<string>();
        using var rig = NewRig(wgcFault: WgcOff, fallback: fallbacks.Enqueue);

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(3));

        Assert.Equal("desktop-duplication", MonitorSession(rig).CaptureApi);
        Assert.Single(fallbacks);
        Assert.Equal(Path.Combine(_directory, ProfileFolders.MonitorRecordings), Path.GetDirectoryName(file));
        Assert.Equal(["Game", "Mic"], AudioTitles(file));
        AssertInStepAt30Fps(file, atLeast: 2.5);
        SessionTestKit.AssertDecodesCleanly(file);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ALostDuplicationIsRecreatedIntoTheSameRecording()
    {
        using var bench = BenchLock.Take();
        var fallbacks = new ConcurrentQueue<string>();
        using var rig = NewRig(wgcFault: WgcOff, fallback: fallbacks.Enqueue);

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(4), async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
            MonitorSession(rig).SimulateCaptureAccessLost();
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
            MonitorSession(rig).SimulateCaptureAccessLost();
        });

        Assert.Single(fallbacks);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, ProfileFolders.MonitorRecordings)));
        AssertInStepAt30Fps(file, atLeast: 3.5);
        SessionTestKit.AssertDecodesCleanly(file);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AWindowExcludedFromCaptureNeverShowsInADesktopDuplicationRecording()
    {
        using var bench = BenchLock.Take();
        SessionTestKit.RequireFFmpeg();
        SessionTestKit.OpenWindow(_directory, _processes, ExcludedWindowScript);
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        using var rig = NewRig(wgcFault: WgcOff);

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(2));

        Assert.Equal("desktop-duplication", MonitorSession(rig).CaptureApi);
        var frames = SessionTestKit.FrameStats(file, "crop=iw/2:ih/2:iw/4:ih/4");
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.False(f.U > 170 && f.V > 170, $"magenta at {f.Time:0.000} s (U {f.U:0}, V {f.V:0})"));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ATwoScreenMonitorRecordingOf60SecondsPlaysToTheEndWithEveryTrackAndBothScreensPlaced()
    {
        // Story 11.3 on the builder's PC: All screens, composited on one chain at the canvas rule's size.
        using var bench = BenchLock.Take();
        var displays = new DisplayMonitors(NullLogger.Instance);
        var screens = RequireTwoScreens(displays);
        var warns = new ConcurrentQueue<string>();
        using var rig = NewRig(displays: displays, warn: warns.Enqueue);
        ChooseAllScreens();
        var chains = 0;

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(60), async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), Token);
            chains = rig.Factory.Sessions.Sum(s => s.Chains);
        });

        Assert.Equal("composite", MonitorSession(rig).CaptureApi);
        Assert.Equal(1, chains);
        Assert.Empty(warns);
        var plan = MonitorCanvasRule.Plan(screens, VideoCodec.H264, _ => true);
        var video = SessionTestKit.VideoStream(file);
        Assert.Equal((plan.Codec.ToString().ToLowerInvariant(), plan.Width, plan.Height), (video.Codec, video.Width, video.Height));
        Assert.Equal(["Game", "Mic"], AudioTitles(file));
        AssertInStepAt30Fps(file, atLeast: 59);
        SessionTestKit.AssertDecodesCleanly(file);
        AssertGapsAreBlack(file, plan);

        // Story 14.3: Media Foundation reads its length from the mehd and plays it to the end.
        await MediaPlayerCheck.AssertPlaysToTheEndAsync(file, 30, Token);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AScreenOnDesktopDuplicationStillComposites()
    {
        using var bench = BenchLock.Take();
        var displays = new DisplayMonitors(NullLogger.Instance);
        var screens = RequireTwoScreens(displays);
        var fallbacks = new ConcurrentQueue<string>();
        using var rig = NewRig(displays: displays, fallback: fallbacks.Enqueue, duplicationScreens: [screens[1].DeviceName]);
        ChooseAllScreens();

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(4));

        Assert.Contains(screens[1].DeviceName, Assert.Single(fallbacks), StringComparison.Ordinal);
        var plan = MonitorCanvasRule.Plan(screens, VideoCodec.H264, _ => true);
        Assert.Equal((plan.Width, plan.Height), (SessionTestKit.VideoStream(file).Width, SessionTestKit.VideoStream(file).Height));
        AssertInStepAt30Fps(file, atLeast: 3.5);
        SessionTestKit.AssertDecodesCleanly(file);
        AssertGapsAreBlack(file, plan);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AGpuResetRebuildsTheCompositeAndTheMonitorRecordingContinuesInASecondFile()
    {
        using var bench = BenchLock.Take();
        var displays = new DisplayMonitors(NullLogger.Instance);
        var screens = RequireTwoScreens(displays);
        using var rig = NewRig(displays: displays, recovery: true);
        ChooseAllScreens();
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        var first = Assert.IsType<RecordingStarted>(await rig.NextAsync()).Path;
        await Task.Delay(TimeSpan.FromSeconds(3), Token);

        MonitorSession(rig).SimulateDeviceLoss();
        RecordingRestart? restart = null;
        while (restart is null)
        {
            restart = await rig.NextAsync() as RecordingRestart;
        }

        Assert.Equal(first, restart.StoppedPath);
        await Task.Delay(TimeSpan.FromSeconds(3), Token);
        Assert.Equal(1, rig.Factory.Sessions.Sum(s => s.Chains));
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        while (await rig.NextAsync() is not RecordingStopped { Reason: RecordingStopReasons.Stopped } stopped || stopped.Path != restart.StartedPath)
        {
        }

        rig.Orchestrator.Dispose();
        var plan = MonitorCanvasRule.Plan(screens, VideoCodec.H264, _ => true);
        foreach (var file in new[] { first, restart.StartedPath })
        {
            Assert.Equal((plan.Width, plan.Height), (SessionTestKit.VideoStream(file).Width, SessionTestKit.VideoStream(file).Height));
            SessionTestKit.AssertDecodesCleanly(file);
        }
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ARealWindowResizedTo2560By1440MovesTheReplayBufferAndTheNextClipHasIt()
    {
        // Story 12.2 (AD-26): matchSource on; the window resizes itself from 1280x720 after 3 s.
        using var bench = BenchLock.Take("Engine 12.2 size move test");
        var gaps = new ConcurrentQueue<string>();
        using var rig = NewRig(clips: true, resetGap: gaps.Enqueue);
        MatchSource();
        var window = SessionTestKit.OpenWindow(_directory, _processes, ResizingWindowScript, "3");
        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => rig.Factory.Sessions.Count == 1);

        // 3 s at 1280x720, the resize, 3 s held, then the build and the switch.
        await Task.Delay(TimeSpan.FromSeconds(11), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var clip = Assert.IsType<ClipSaved>(await rig.NextAsync()).Path;
        rig.Orchestrator.Dispose();

        Assert.Equal((2560, 1440), (SessionTestKit.VideoStream(clip).Width, SessionTestKit.VideoStream(clip).Height));
        SessionTestKit.AssertDecodesCleanly(clip);
        Assert.True(gaps.Count <= 1, string.Join(" | ", gaps));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AResizeDuringAGameRecordingKeepsItsSizeAndMovesAfterItStops()
    {
        // Story 12.2 (R12): the Recording stays one 1280x720 file; the move waits for its end.
        using var bench = BenchLock.Take("Engine 12.2 size move test");
        using var rig = NewRig(recordingsEnabled: true, clips: true);
        MatchSource();
        var window = SessionTestKit.OpenWindow(_directory, _processes, ResizingWindowScript, "4");
        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => rig.Factory.Sessions.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        var recording = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        await Task.Delay(TimeSpan.FromSeconds(9), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(recording.Path, RecordingStopReasons.Stopped), await rig.NextAsync());

        // The size held all along: the move runs at the first frame after the stop.
        await Task.Delay(TimeSpan.FromSeconds(6), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var clip = Assert.IsType<ClipSaved>(await rig.NextAsync()).Path;
        rig.Orchestrator.Dispose();

        Assert.Equal((1280, 720), (SessionTestKit.VideoStream(recording.Path).Width, SessionTestKit.VideoStream(recording.Path).Height));
        SessionTestKit.AssertDecodesCleanly(recording.Path);
        Assert.Equal((2560, 1440), (SessionTestKit.VideoStream(clip).Width, SessionTestKit.VideoStream(clip).Height));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ARecordPressWhileAMoveBuildsCancelsItAndTheRecordingKeepsItsChain()
    {
        // Story 12.2 (QA 1, 2): the window resizes at 2 s, the size holds at 5 s and the move's chain
        // builds; Record lands then. The Recording keeps the 1280x720 chain in one file, and the move
        // is found again after the stop.
        using var bench = BenchLock.Take("Engine 12.2 size move test");
        using var rig = NewRig(recordingsEnabled: true, clips: true);
        MatchSource();
        var window = SessionTestKit.OpenWindow(_directory, _processes, ResizingWindowScript, "2");
        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => rig.Factory.Sessions.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(5.1), Token);

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        var recording = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        await Task.Delay(TimeSpan.FromSeconds(4), Token);
        Assert.True(rig.Factory.Sessions.Sum(s => s.Chains) <= 1, "no move chain parked beside the Recording");
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(recording.Path, RecordingStopReasons.Stopped), await rig.NextAsync());

        await Task.Delay(TimeSpan.FromSeconds(6), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var clip = Assert.IsType<ClipSaved>(await rig.NextAsync()).Path;
        rig.Orchestrator.Dispose();

        Assert.Equal((1280, 720), (SessionTestKit.VideoStream(recording.Path).Width, SessionTestKit.VideoStream(recording.Path).Height));
        SessionTestKit.AssertDecodesCleanly(recording.Path);
        Assert.Equal((2560, 1440), (SessionTestKit.VideoStream(clip).Width, SessionTestKit.VideoStream(clip).Height));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AMonitorRecordingStartsBesideAGameRecordingAfterAResize()
    {
        // Story 12.2 (QA 2): a resize during a game Recording parks no chain, so a Monitor Recording
        // still fits AD-7's budget, and two chains run between the sessions.
        using var bench = BenchLock.Take("Engine 12.2 size move test");
        using var rig = NewRig(recordingsEnabled: true);
        MatchSource();
        var window = SessionTestKit.OpenWindow(_directory, _processes, ResizingWindowScript, "3");
        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => rig.Factory.Sessions.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        var game = Assert.IsType<RecordingStarted>(await rig.NextAsync());

        // The resize at 3 s, held past 6 s.
        await Task.Delay(TimeSpan.FromSeconds(6), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        var monitor = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        Assert.Equal(2, rig.Factory.Sessions.Sum(s => s.Chains));

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(monitor.Path, RecordingStopReasons.Stopped), await rig.NextAsync());
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(game.Path, RecordingStopReasons.Stopped), await rig.NextAsync());
        rig.Orchestrator.Dispose();
        SessionTestKit.AssertDecodesCleanly(game.Path);
        SessionTestKit.AssertDecodesCleanly(monitor.Path);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AOneScreenMonitorRecordingOfThe3840By2160ScreenRecordsAtThatSize()
    {
        // Story 12.2 (AD-26): one screen under matchSource records at its physical mode size.
        using var bench = BenchLock.Take("Engine 12.2 one-screen source size test");
        var displays = new DisplayMonitors(NullLogger.Instance);
        var uhd = displays.List().FirstOrDefault(m => m is { Width: 3840, Height: 2160 });
        Assert.SkipWhen(uhd is null, "Needs a 3840x2160 screen attached.");
        using var rig = NewRig(displays: displays);
        MatchSource();
        var document = Volatile.Read(ref _document);
        Volatile.Write(ref _document, document with { Global = document.Global.SetItem(Keys.MonitorRecordingScreens.Name, Keys.MonitorRecordingScreens.ToJson(MonitorRecordingScreens.Selected(uhd!.MonitorId ?? uhd.DeviceName))) });

        var file = await RecordMonitorAsync(rig, TimeSpan.FromSeconds(4));

        var video = SessionTestKit.VideoStream(file);
        Assert.Equal(("h264", 3840, 2160), (video.Codec, video.Width, video.Height));
        SessionTestKit.AssertDecodesCleanly(file);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AGameSessionWhoseWgcFailsNeverFallsBack()
    {
        using var bench = BenchLock.Take();
        var fallbacks = new ConcurrentQueue<string>();
        using var rig = NewRig(wgcFault: WgcOff, fallback: fallbacks.Enqueue);
        var window = SessionTestKit.OpenWindow(_directory, _processes, GameWindowScript);

        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => !rig.Factory.Failures.IsEmpty);

        Assert.Contains(rig.Factory.Failures, e => e.Message == WgcOffMessage);
        Assert.Empty(rig.Factory.Sessions);
        Assert.Empty(fallbacks);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ARemovedMonitorEndsItsMonitorRecordingPlayableWithMonitorRemoved()
    {
        using var bench = BenchLock.Take();
        var displays = new FakeDisplays(RealDisplays());
        var warns = new ConcurrentQueue<string>();
        using var rig = NewRig(displays: displays, warn: warns.Enqueue);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        var started = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        await Task.Delay(TimeSpan.FromSeconds(3), Token);

        // The port reports the captured monitor gone (the real one stays): another becomes the only one.
        var captured = MonitorSession(rig).MonitorDevice;
        Assert.False(string.IsNullOrEmpty(captured));
        displays.Change([new DisplayMonitor(@"\\.\DISPLAY99", "Other", 1920, 1080, true)]);

        Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.MonitorRemoved), await rig.NextAsync());
        rig.Orchestrator.Dispose();
        Assert.Equal([Contracts.ErrorCodes.MonitorRemoved], warns);
        SessionTestKit.AssertDecodesCleanly(started.Path);
        AssertInStepAt30Fps(started.Path, 2.5);
        Assert.Equal(RecordingState.Off, rig.Published.Last().MonitorRecording);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AGameRecordingRunsOnThroughEachDisplayChange()
    {
        using var bench = BenchLock.Take();
        var real = RealDisplays();
        var displays = new FakeDisplays(real);
        using var rig = NewRig(recordingsEnabled: true, displays: displays);
        var window = SessionTestKit.OpenWindow(_directory, _processes, GameWindowScript);
        rig.Orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
        await WaitUntil(() => rig.Factory.Sessions.Count == 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        var game = Assert.IsType<RecordingStarted>(await rig.NextAsync());

        // A monitor is connected, the primary moves to it, then it is removed again.
        var added = new DisplayMonitor(@"\\.\DISPLAY99", "Added", 1920, 1080, false);
        displays.Change([.. real, added]);
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        displays.Change([.. real.Select(m => m with { Primary = false }), added with { Primary = true }]);
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        displays.Change(real);

        // Past the 3.5 s the file is checked for: the three changes took about 3 s.
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(game.Path, RecordingStopReasons.Stopped), await rig.NextAsync());
        Assert.Single(rig.Factory.Sessions);
        Assert.Equal(ReplayState.Buffering, rig.Published.Last().Replay);
        rig.Orchestrator.Dispose();
        SessionTestKit.AssertDecodesCleanly(game.Path);
        AssertInStepAt30Fps(game.Path, 3.5);
    }

    /// <summary>The real attached monitors, as the engine's port lists them; read only.</summary>
    private static IReadOnlyList<DisplayMonitor> RealDisplays() =>
        new DisplayMonitors(NullLogger.Instance).List() is { Count: > 0 } list ? list : throw new InvalidOperationException("No monitor is attached.");

    private const string WgcOffMessage = "WGC is off (test)";

    private static Exception? WgcOff() => new InvalidOperationException(WgcOffMessage);

    /// <summary>The attached screens, skipping the test unless there are at least two (story 11.3 needs them).</summary>
    private static IReadOnlyList<DisplayMonitor> RequireTwoScreens(DisplayMonitors displays)
    {
        var screens = displays.List();
        Assert.SkipWhen(screens.Count < 2, "A composited Monitor Recording needs two screens attached.");
        return screens;
    }

    /// <summary>Story 12.2: <c>video.matchSource</c> on, in Global and in the test game's profile override.</summary>
    private void MatchSource()
    {
        var document = Volatile.Read(ref _document);
        var global = Keys.Video.TryRead(document.Global[Keys.Video.Name], out var g) ? g : Keys.Video.Default;
        var profile = document.Profiles[ProfileId];
        var overridden = Keys.Video.TryRead(profile.Overrides[Keys.Video.Name], out var o) ? o : global;
        Volatile.Write(ref _document, document with
        {
            Global = document.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(global with { MatchSource = true })),
            Profiles = document.Profiles.SetItem(ProfileId, profile with { Overrides = profile.Overrides.SetItem(Keys.Video.Name, Keys.Video.ToJson(overridden with { MatchSource = true })) }),
        });
    }

    /// <summary>Global <c>monitorRecordingScreens</c> = All screens (story 11.3).</summary>
    private void ChooseAllScreens()
    {
        var document = Volatile.Read(ref _document);
        Volatile.Write(ref _document, document with { Global = document.Global.SetItem(Keys.MonitorRecordingScreens.Name, Keys.MonitorRecordingScreens.ToJson(MonitorRecordingScreens.AllScreens)) });
    }

    /// <summary>
    /// The canvas's gaps, where no screen sits (below a shorter screen), are black in every frame: the
    /// screens are placed as Windows arranges them, not stretched.
    /// </summary>
    private static void AssertGapsAreBlack(string file, CanvasPlan plan)
    {
        var scale = (double)plan.Width / plan.NativeWidth;
        foreach (var area in plan.Areas.Where(a => a.Y + a.Height < plan.NativeHeight))
        {
            var x = (int)(area.X * scale) + 8;
            var y = (int)((area.Y + area.Height) * scale) + 8;
            var w = (int)(area.Width * scale) - 16;
            var h = (int)((plan.NativeHeight - area.Y - area.Height) * scale) - 16;
            var frames = SessionTestKit.FrameStats(file, FormattableString.Invariant($"crop={w}:{h}:{x}:{y}"));
            Assert.NotEmpty(frames);
            Assert.All(frames, f => Assert.True(f.Y < 24, $"the gap under {area.Screen.DeviceName} isn't black at {f.Time:0.000} s (Y {f.Y:0})"));
        }
    }

    private static CaptureSession MonitorSession(Rig rig) => Assert.Single(rig.Factory.Sessions.OfType<CaptureSession>());

    /// <summary>
    /// The file's video runs at least <paramref name="atLeast"/> seconds at one 30 fps step
    /// throughout, and both Global Tracks' durations are within 0.2 s of the video's.
    /// </summary>
    private static void AssertInStepAt30Fps(string file, double atLeast)
    {
        var (pts, num, den) = SessionTestKit.VideoPtsTicks(file);
        Assert.Equal(0, den % (30 * num));
        var step = den / (30 * num);
        for (var i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] - pts[i - 1] == step, $"video PTS step {pts[i] - pts[i - 1]} at packet {i}, not {step}");
        }

        var durations = SessionTestKit.Probe(file, "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => (Type: s.GetProperty("codec_type").GetString(), Duration: double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        var video = Assert.Single(durations, d => d.Type == "video").Duration;
        Assert.True(video >= atLeast, $"video runs {video:0.000} s, under {atLeast} s");
        var audio = durations.Where(d => d.Type == "audio").ToList();
        Assert.Equal(2, audio.Count);
        Assert.All(audio, a => Assert.InRange(a.Duration, video - 0.2, video + 0.2));
    }

    private static List<string?> AudioTitles(string file) =>
        [.. SessionTestKit.Probe(file, "-select_streams", "a", "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var title) ? title.GetString() : null)];

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(50, Token);
        }
    }

    /// <summary>
    /// Two Monitor Record toggles <paramref name="length"/> apart, <paramref name="during"/> run
    /// meanwhile; returns the file once its writer has finished.
    /// </summary>
    private static async Task<string> RecordMonitorAsync(Rig rig, TimeSpan length, Func<Task>? during = null)
    {
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        var started = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        var elapsed = Stopwatch.StartNew();
        if (during is not null)
        {
            await during();
        }

        var rest = length - elapsed.Elapsed;
        if (rest > TimeSpan.Zero)
        {
            await Task.Delay(rest, Token);
        }

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await rig.NextAsync());
        rig.Orchestrator.Dispose();
        return started.Path;
    }

    /// <summary>
    /// An orchestrator with Recordings and the Target's game session in Replay mode; the Global
    /// video is 640x360 at 30 fps and the Global Track Layout two Tracks a <see cref="ToneHub"/>
    /// plays. The profile, 640x480, has <c>recordingsEnabled</c> as given.
    /// </summary>
    /// <param name="clips">Whether the orchestrator has Replay Clips, whose saves land in the events.</param>
    /// <param name="resetGap">Receives each <c>reset-gap</c> message.</param>
    /// <param name="wgcFault">Thrown before any WGC capture is created, when it returns non-null.</param>
    /// <param name="fallback">Receives each <c>monitor-capture-fallback</c> message.</param>
    /// <param name="displays">The display port the orchestrator watches; none when null.</param>
    /// <param name="warn">Receives each Recordings warning's code.</param>
    /// <param name="duplicationScreens">Composited screens captured through Desktop Duplication (story 11.3).</param>
    /// <param name="recovery">Whether the orchestrator recovers a lost GPU device (8.3), its restarts landing in the events.</param>
    private Rig NewRig(
        bool recordingsEnabled = false,
        bool clips = false,
        Action<string>? resetGap = null,
        Func<Exception?>? wgcFault = null,
        Action<string>? fallback = null,
        IDisplayMonitors? displays = null,
        Action<string>? warn = null,
        IReadOnlyCollection<string>? duplicationScreens = null,
        bool recovery = false)
    {
        SessionTestKit.RequireFFmpeg();
        var hub = new ToneHub(new Dictionary<string, (double, double)> { ["game"] = (0.1, 440), ["mic"] = (0.2, 1000) });
        var game = new LayoutDevice(Guid.NewGuid(), "game", "Game device", "", SettingsFlow.Output, IsMic: false);
        var mic = new LayoutDevice(Guid.NewGuid(), "mic", "Mic device", "", SettingsFlow.Input, IsMic: true);
        var layout = new TrackLayout(
            [game, mic],
            [new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false), new Track(Guid.NewGuid(), "Mic", [mic.Key], MuteAffected: true)]);
        _document = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 360, 30, 4_000, RateControl.Cbr)))
                .SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory))
                .SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Test Game",
                RecordingsEnabled = recordingsEnabled,
                Overrides = SettingsMaps.EmptyValues.Add(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr))),
            }),
        };

        var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
        var published = new ConcurrentQueue<CaptureState>();
        var factory = new KeepingFactory(new CaptureSessionFactory(
            new CaptureSessionOptions { OutputFolder = _directory, Replay = true, ResetGap = resetGap, WgcFault = wgcFault, CaptureFallback = fallback, DuplicationScreens = duplicationScreens },
            NullLoggerFactory.Instance,
            hub));
        var orchestrator = new SessionOrchestrator(
            factory,
            () => Volatile.Read(ref _document),
            published.Enqueue,
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => events.Writer.TryWrite(e)),
            clips: clips
                ? new ReplayClips(OutputPathAllocator.System, () => _directory, c => events.Writer.TryWrite(c), e => events.Writer.TryWrite(e), NotSaved: f => events.Writer.TryWrite(f))
                : null,
            recordings: new Recordings(
                OutputPathAllocator.System,
                () => _directory,
                s => events.Writer.TryWrite(s),
                s => events.Writer.TryWrite(s),
                e => events.Writer.TryWrite(e),
                Warn: warn is null ? null : (code, _) => warn(code),
                Restarted: r => events.Writer.TryWrite(r)),
            deviceRecovery: recovery ? new DeviceRecovery(RetryEvery: TimeSpan.FromSeconds(1)) : null,
            displays: displays);
        return new Rig(orchestrator, hub, events, published, factory);
    }

    private sealed class Rig(
        SessionOrchestrator orchestrator,
        ToneHub hub,
        System.Threading.Channels.Channel<object> events,
        ConcurrentQueue<CaptureState> published,
        KeepingFactory factory) : IDisposable
    {
        public SessionOrchestrator Orchestrator { get; } = orchestrator;

        public ConcurrentQueue<CaptureState> Published { get; } = published;

        public KeepingFactory Factory { get; } = factory;

        public async Task<object> NextAsync() => await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

        public void Dispose()
        {
            Orchestrator.Dispose();
            hub.Dispose();
        }
    }

    /// <summary>Starts sessions with <paramref name="inner"/>, keeping every one it started and every start's failure.</summary>
    private sealed class KeepingFactory(ICaptureSessionFactory inner) : ICaptureSessionFactory
    {
        private readonly ConcurrentQueue<ICaptureSession> _sessions = new();

        public IReadOnlyList<ICaptureSession> Sessions => [.. _sessions];

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public ICaptureSession Start(SessionSpec spec, ISessionEvents events)
        {
            ICaptureSession session;
            try
            {
                session = inner.Start(spec, events);
            }
            catch (Exception e)
            {
                Failures.Enqueue(e);
                throw;
            }

            _sessions.Enqueue(session);
            return session;
        }
    }
}
