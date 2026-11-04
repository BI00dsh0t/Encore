using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
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
    private Rig NewRig(
        bool recordingsEnabled = false,
        bool clips = false,
        Action<string>? resetGap = null,
        Func<Exception?>? wgcFault = null,
        Action<string>? fallback = null,
        IDisplayMonitors? displays = null,
        Action<string>? warn = null)
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
            new CaptureSessionOptions { OutputFolder = _directory, Replay = true, ResetGap = resetGap, WgcFault = wgcFault, CaptureFallback = fallback },
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
                Warn: warn is null ? null : (code, _) => warn(code)),
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

    /// <summary>
    /// <c>.bench-running</c> in the main folder: on-screen tests wait while another session holds it,
    /// then hold it themselves.
    /// </summary>
    private sealed class BenchLock(string path) : IDisposable
    {
        public static BenchLock Take()
        {
            var path = Path.Combine(MainFolder(), ".bench-running");
            while (true)
            {
                try
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    file.Write("Engine Monitor Recording capture test"u8);
                    return new BenchLock(path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
            }
        }

        public void Dispose() => File.Delete(path);

        // The topmost folder with ReplayBuffer.slnx: the main checkout, not a worktree under it.
        private static string MainFolder()
        {
            string? found = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ReplayBuffer.slnx")))
                {
                    found = dir.FullName;
                }
            }

            return found ?? throw new InvalidOperationException("ReplayBuffer.slnx wasn't found above the test.");
        }
    }
}
