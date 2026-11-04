using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// Monitor Recording (story 6.9), in process on the real primary monitor: a <c>monitorRecordToggle</c>
/// pair with no game writes one file in <c>Monitor Recordings</c> with every Global Track; a window
/// marked <c>WDA_EXCLUDEFROMCAPTURE</c> never shows in it; and beside a game Recording on a synthetic
/// window, the two sessions run two encoder chains between them. On-screen, so each takes the bench lock.
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

    /// <summary>Two Monitor Record toggles <paramref name="length"/> apart; returns the file once its writer has finished.</summary>
    private static async Task<string> RecordMonitorAsync(Rig rig, TimeSpan length)
    {
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.MonitorRecordToggle, Stopwatch.GetTimestamp())));
        var started = Assert.IsType<RecordingStarted>(await rig.NextAsync());
        await Task.Delay(length, Token);
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
    private Rig NewRig(bool recordingsEnabled = false)
    {
        SessionTestKit.RequireFFmpeg();
        var hub = new ToneHub(new Dictionary<string, (double, double)> { ["game"] = (0.1, 440), ["mic"] = (0.2, 1000) });
        var game = new LayoutDevice(Guid.NewGuid(), "game", "Game device", "", SettingsFlow.Output, IsMic: false);
        var mic = new LayoutDevice(Guid.NewGuid(), "mic", "Mic device", "", SettingsFlow.Input, IsMic: true);
        var layout = new TrackLayout(
            [game, mic],
            [new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false), new Track(Guid.NewGuid(), "Mic", [mic.Key], MuteAffected: true)]);
        var document = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 360, 30, 4_000, RateControl.Cbr)))
                .SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Test Game",
                RecordingsEnabled = recordingsEnabled,
                Overrides = SettingsMaps.EmptyValues.Add(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr))),
            }),
        };

        var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
        var published = new ConcurrentQueue<CaptureState>();
        var factory = new KeepingFactory(new CaptureSessionFactory(new CaptureSessionOptions { OutputFolder = _directory, Replay = true }, NullLoggerFactory.Instance, hub));
        var orchestrator = new SessionOrchestrator(
            factory,
            () => document,
            published.Enqueue,
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => events.Writer.TryWrite(e)),
            recordings: new Recordings(
                OutputPathAllocator.System,
                () => _directory,
                s => events.Writer.TryWrite(s),
                s => events.Writer.TryWrite(s),
                e => events.Writer.TryWrite(e)));
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

    /// <summary>Starts sessions with <paramref name="inner"/> and keeps every one it started.</summary>
    private sealed class KeepingFactory(ICaptureSessionFactory inner) : ICaptureSessionFactory
    {
        private readonly ConcurrentQueue<ICaptureSession> _sessions = new();

        public IReadOnlyList<ICaptureSession> Sessions => [.. _sessions];

        public ICaptureSession Start(SessionSpec spec, ISessionEvents events)
        {
            var session = inner.Start(spec, events);
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
