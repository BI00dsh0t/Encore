using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Library;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.FFmpeg;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// The Recordings tracer (story 6.1), in process: a <see cref="ToneHub"/> plays three devices, the
/// Game Profile "Test Game" has three Tracks and <c>recordingsEnabled</c> on, and its game is
/// proposed as the Target on a pwsh window. Two <c>recordToggle</c> presses some seconds apart make
/// one fragmented MP4 of the time between them beside the Replay Buffer, which keeps buffering.
/// Killing the game, or disposing the orchestrator, mid-Recording still finishes it (story 6.4).
/// A window resize keeps it one file, and a forced encoder restart splits it in two (story 6.6).
/// </summary>
public sealed class RecordingCaptureTests : IDisposable
{
    private const string WindowScript =
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

    // The tracer's window, grown to 1000x700 at 3.5 s after it opens and back at 5.5 s.
    private const string ResizingWindowScript =
        """
        param([string] $Title)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.Width = 641
        $form.Height = 481
        $start = [Diagnostics.Stopwatch]::StartNew()
        $script:resized = 0
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $form.BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
            if ($script:resized -eq 0 -and $start.ElapsedMilliseconds -ge 3500) {
                $form.Size = New-Object Drawing.Size(1000, 700)
                $script:resized = 1
            } elseif ($script:resized -eq 1 -and $start.ElapsedMilliseconds -ge 5500) {
                $form.Size = New-Object Drawing.Size(641, 481)
                $script:resized = 2
            }
        })
        $timer.Start()
        $form.Show()
        $form.Hide()
        [void] $form.ShowDialog()
        """;

    private const string ProfileId = @"c:\games\test game\testgame.exe";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-recording-").FullName;
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public Task TwoRecordPressesWriteOneFragmentedRecordingOfTheTimeBetween() => RecordAsync(TimeSpan.FromSeconds(4));

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public Task ASixtySecondRecordingKeepsEveryTrackInStep() => RecordAsync(TimeSpan.FromSeconds(60));

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task KillingTheTargetMidRecordingFinishesItInTheGamesFolderAsGameExit()
    {
        using var rig = await StartRecordingAsync();
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        // The game dies; Game Detection then sees the exit.
        var game = _processes[^1];
        game.Kill(entireProcessTree: true);
        Assert.True(game.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.True(rig.Orchestrator.Post(new ProposeTarget(null, TargetChangeReason.Exit)));
        var stopped = Assert.IsType<RecordingStopped>(await rig.NextAsync());
        rig.Orchestrator.Dispose();

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "Test Game"), "*.mp4"));
        Assert.Equal(new RecordingStopped(file, RecordingStopReasons.GameExit), stopped);
        Assert.Equal([$"begin {file} {ProfileId}", $"end {file} Written"], rig.Library.Calls);
        AssertFinished(file, atLeast: 1.5);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task DisposingTheOrchestratorMidRecordingFinishesItAsShutdownWithinFiveSeconds()
    {
        using var rig = await StartRecordingAsync();
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        var quit = Stopwatch.StartNew();
        rig.Orchestrator.Dispose();
        quit.Stop();

        // Handled before Dispose returned: the channel had closed, so nothing else could post it.
        Assert.True(quit.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {quit.Elapsed}");
        Assert.True(rig.Events.Reader.TryRead(out var stopped));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "Test Game"), "*.mp4"));
        Assert.Equal(new RecordingStopped(file, RecordingStopReasons.Shutdown), stopped);
        Assert.Equal([$"begin {file} {ProfileId}", $"end {file} Written"], rig.Library.Calls);
        AssertFinished(file, atLeast: 1.5);
    }

    /// <summary>
    /// Story 6.5: a 1280×720 profile video committed before the Record press waits for the
    /// Recording's stop; ten saves at seeded random times during a 2-minute Recording leave it
    /// unbroken, and the save after the switching one has the new size.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ClipsDuringARecordingLeaveItUnbrokenAndAChainChangeWaitsForItsStop()
    {
        const int Saves = 10;
        var length = TimeSpan.FromMinutes(2);
        var changed = new VideoSettings(VideoCodec.H264, 1280, 720, 30, 6_000, RateControl.Cbr);
        using var rig = await StartRecordingAsync(
            clips: true,
            pending: document => document with
            {
                Profiles = document.Profiles.SetItem(ProfileId, document.Profiles[ProfileId] with
                {
                    Overrides = document.Profiles[ProfileId].Overrides.SetItem(Keys.Video.Name, Keys.Video.ToJson(changed)),
                }),
            });

        // One save in each 12 s slot, 1.5 s to 10.5 s into it: at least 3 s apart, past the 1 s after each Reset.
        var random = new Random(65);
        var slot = length / Saves;
        var at = Enumerable.Range(0, Saves).Select(i => (slot * i) + TimeSpan.FromSeconds(1.5 + (random.NextDouble() * 9))).ToList();
        var clips = new List<string>();
        var outstanding = 0;
        var clock = Stopwatch.StartNew();

        async Task NextClipAsync()
        {
            switch (await rig.NextAsync())
            {
                case ClipSaved saved:
                    clips.Add(saved.Path);
                    outstanding--;
                    break;
                case var other:
                    Assert.Fail($"Expected a saved Clip, got {other}");
                    break;
            }
        }

        foreach (var time in at)
        {
            if (time > clock.Elapsed)
            {
                await Task.Delay(time - clock.Elapsed, Token);
            }

            // R6: never a third writer.
            while (outstanding >= ReplayClips.MaxWriters)
            {
                await NextClipAsync();
            }

            Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            outstanding++;
        }

        while (outstanding > 0)
        {
            await NextClipAsync();
        }

        if (length > clock.Elapsed)
        {
            await Task.Delay(length - clock.Elapsed, Token);
        }

        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        var stopped = Assert.IsType<RecordingStopped>(await rig.NextAsync());
        Assert.Equal(new RecordingStopped(rig.Started.Path, RecordingStopReasons.Stopped), stopped);

        // The stop prepares the profile's chain; the first save switches to it, the second has it.
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var switching = Assert.IsType<ClipSaved>(await rig.NextAsync());
        await Task.Delay(TimeSpan.FromSeconds(3), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
        var after = Assert.IsType<ClipSaved>(await rig.NextAsync());
        rig.Orchestrator.Dispose();

        // Ten Clips during it, each whole at the session's size with all its Tracks.
        Assert.Equal(Saves, clips.Count);
        foreach (var clip in clips.Append(switching.Path))
        {
            var (_, width, height, _) = SessionTestKit.VideoStream(clip);
            Assert.Equal((640, 480), (width, height));
            Assert.Equal(["Game", "Voice", "Mic"], AudioTitles(clip));
            SessionTestKit.AssertDecodesCleanly(clip);
        }

        var (_, afterWidth, afterHeight, _) = SessionTestKit.VideoStream(after.Path);
        Assert.Equal((1280, 720), (afterWidth, afterHeight));

        // The Recording: one size, one frame step throughout, every Track's audio contiguous.
        var file = rig.Started.Path;
        var (_, recordingWidth, recordingHeight, _) = SessionTestKit.VideoStream(file);
        Assert.Equal((640, 480), (recordingWidth, recordingHeight));
        var (pts, num, den) = SessionTestKit.VideoPtsTicks(file);
        Assert.Equal(0, den % (30 * num));
        var step = den / (30 * num);
        for (var i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] - pts[i - 1] == step, $"video PTS step {pts[i] - pts[i - 1]} at packet {i}, not {step}");
        }

        for (var track = 0; track < 3; track++)
        {
            var packets = SessionTestKit.Probe(file, "-select_streams", $"a:{track}", "-show_entries", "packet=pts,duration", "-show_packets")
                .GetProperty("packets").EnumerateArray()
                .Select(p => (Pts: p.GetProperty("pts").GetInt64(), Duration: p.GetProperty("duration").GetInt64()))
                .OrderBy(p => p.Pts)
                .ToList();
            for (var i = 1; i < packets.Count; i++)
            {
                Assert.True(
                    packets[i].Pts == packets[i - 1].Pts + packets[i - 1].Duration,
                    $"audio Track {track}: packet {i} at {packets[i].Pts}, expected {packets[i - 1].Pts + packets[i - 1].Duration}");
            }
        }

        SessionTestKit.AssertDecodesCleanly(file);
    }

    [Fact]
    public void ARestartIsRefusedBeforeTheSessionHasAnchored()
    {
        using var hub = new ToneHub(new Dictionary<string, (double, double)>());
        var spec = new SessionSpec(SessionKind.Game, new CaptureTarget(1, 0, null), Core.Settings.SettingsResolver.Resolve(SettingsDocument.Empty, null), SessionAudio.None, MicMuted: true);
        var session = new CaptureSession(spec, new NoEvents(), new CaptureSessionOptions { OutputFolder = _directory, Replay = true }, hub, new NoEncoders(), NullLoggerFactory.Instance);
        try
        {
            Assert.False(session.RestartChain("test"));
        }
        finally
        {
            session.Abandon();
        }
    }

    /// <summary>
    /// Story 6.6: a Recording runs through a window resize (one file, still 640x480), then a forced
    /// encoder restart ends it as <c>encoder-restart</c> and starts a second one at once; the two
    /// play, and they meet with no gap longer than the restart took.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AWindowResizeKeepsOneFileAndAnEncoderRestartStartsASecond()
    {
        var logs = new ListLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var rig = await StartRecordingAsync(loggers: loggers, windowScript: ResizingWindowScript);

        // Five seconds at least, and past the window's resize and its return.
        await Task.Delay(TimeSpan.FromSeconds(5), Token);
        var open = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - rig.OpenedAt) / Stopwatch.Frequency);
        if (open < TimeSpan.FromSeconds(7))
        {
            await Task.Delay(TimeSpan.FromSeconds(7) - open, Token);
        }

        var restart = Stopwatch.StartNew();
        Assert.True(rig.Factory.Session!.RestartChain("simulated"));
        var a = rig.Started.Path;
        Assert.Equal(new RecordingStopped(a, RecordingStopReasons.EncoderRestart), await rig.NextAsync());
        var measured = restart.Elapsed.TotalSeconds;
        var b = Assert.IsType<RecordingStarted>(await rig.NextAsync()).Path;
        Assert.Equal(new RecordingRestart(a, b), await rig.NextAsync());

        await Task.Delay(TimeSpan.FromSeconds(4), Token);
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
        Assert.Equal(new RecordingStopped(b, RecordingStopReasons.Stopped), await rig.NextAsync());
        rig.Orchestrator.Dispose();

        Assert.Equal([a, b], Directory.GetFiles(Path.Combine(_directory, "Test Game"), "*.mp4").Order(StringComparer.Ordinal));
        Assert.Equal([$"begin {a} {ProfileId}", $"begin {b} {ProfileId}", $"end {a} Written", $"end {b} Written"], rig.Library.Calls);
        var (_, width, height, _) = SessionTestKit.VideoStream(a);
        Assert.Equal((640, 480), (width, height));
        SessionTestKit.AssertDecodesCleanly(a);
        SessionTestKit.AssertDecodesCleanly(b);

        // Each Recording's start in anchor time, from its writer's log: A's end meets B's start.
        var starts = logs.Entries
            .Select(e => Regex.Match(e.Message, @"The Recording starts on the keyframe (\d+\.\d+) s after the anchor"))
            .Where(m => m.Success)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(2, starts.Count);
        var video = SessionTestKit.Probe(a, "-select_streams", "v:0", "-show_streams").GetProperty("streams")[0];
        var aEnd = starts[0] + double.Parse(video.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var gap = starts[1] - aEnd;
        Assert.InRange(gap, -2.0 / 30, measured + (2.0 / 30));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AFailedRestartRebuildFailsTheSessionWithEncodeFailed()
    {
        using var rig = await StartRecordingAsync(restartFault: () => new InvalidOperationException("simulated rebuild failure"));
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        Assert.True(rig.Factory.Session!.RestartChain("simulated"));
        var failure = Assert.IsType<ErrorCodeException>(await rig.Factory.Failed.WaitAsync(Timeout, Token));
        Assert.Equal(ErrorCodes.EncodeFailed, failure.Code);

        // The Recording ends with its session, its file kept.
        Assert.IsType<RecordingStopped>(await rig.NextAsync());
        rig.Orchestrator.Dispose();
        AssertFinished(rig.Started.Path, atLeast: 1);
    }

    private static List<string?> AudioTitles(string file) =>
        [.. SessionTestKit.Probe(file, "-select_streams", "a", "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var title) ? title.GetString() : null)];

    private async Task RecordAsync(TimeSpan seconds)
    {
        using var rig = await StartRecordingAsync();
        var (start, started, library, published) = (rig.Start, rig.Started, rig.Library, rig.Published);

        await Task.Delay(seconds, Token);
        var stop = Stopwatch.GetTimestamp();
        Assert.True(rig.Orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, stop)));
        var stopped = Assert.IsType<RecordingStopped>(await rig.NextAsync());
        rig.Orchestrator.Dispose();

        // One Recording, in the game's folder beside its Clips, and nothing else.
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "Test Game"), "*.mp4"));
        Assert.Equal(file, started.Path);
        Assert.Equal(new RecordingStopped(file, RecordingStopReasons.Stopped), stopped);
        Assert.Empty(Directory.GetFiles(_directory, "*" + ClipWriter.PartExtension, SearchOption.AllDirectories));
        Assert.Equal([$"begin {file} {ProfileId}", $"end {file} Written"], library.Calls);

        // Running from the accepted start to the writer's post-back; the Replay Buffer buffered throughout.
        var states = published.ToList();
        var recording = states.Select(s => s.Recording).Where((r, i) => i == 0 || r != states[i - 1].Recording).ToList();
        Assert.Equal([RecordingState.Off, RecordingState.Running, RecordingState.Off], recording);
        var on = states.FindIndex(s => s.Recording == RecordingState.Running);
        var off = states.FindIndex(on, s => s.Recording == RecordingState.Off);
        Assert.All(states[on..(off + 1)], s => Assert.Equal(ReplayState.Buffering, s.Replay));

        // Fragmented: an empty index first, then a fragment at a time.
        var boxes = ReplayClipCaptureTests.TopLevelBoxes(file);
        Assert.Contains("moov", boxes);
        Assert.Contains("moof", boxes);
        Assert.True(boxes.IndexOf("moov") < boxes.IndexOf("moof"), $"boxes: {string.Join(", ", boxes)}");

        var streams = SessionTestKit.Probe(file, "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => (
                Codec: s.GetProperty("codec_name").GetString(),
                Title: s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var title) ? title.GetString() : null,
                Default: s.GetProperty("disposition").GetProperty("default").GetInt32() == 1,
                Duration: double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(["h264", "aac", "aac", "aac"], streams.Select(s => s.Codec));
        Assert.Equal(["Game", "Voice", "Mic"], streams.Skip(1).Select(s => s.Title));
        Assert.Equal([true, false, false], streams.Skip(1).Select(s => s.Default));

        // The time between the presses, every Track in step with the video.
        var between = (double)(stop - start) / Stopwatch.Frequency;
        var video = streams[0].Duration;
        Assert.InRange(video, between - 0.5, between + 0.5);
        Assert.All(streams.Skip(1), s => Assert.InRange(s.Duration, video - 0.1, video + 0.1));

        // It starts on a keyframe, and decodes cleanly.
        var first = SessionTestKit.Probe(file, "-select_streams", "v:0", "-show_entries", "packet=pts,flags", "-show_packets")
            .GetProperty("packets").EnumerateArray()
            .MinBy(p => p.GetProperty("pts").GetInt64());
        Assert.StartsWith("K", first.GetProperty("flags").GetString(), StringComparison.Ordinal);
        SessionTestKit.AssertDecodesCleanly(file);
    }

    /// <summary>A complete file: ffmpeg decodes it cleanly to its end, and its video runs at least <paramref name="atLeast"/> seconds.</summary>
    private static void AssertFinished(string file, double atLeast)
    {
        var video = SessionTestKit.Probe(file, "-select_streams", "v:0", "-show_streams").GetProperty("streams")[0];
        Assert.True(double.Parse(video.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) >= atLeast);
        SessionTestKit.AssertDecodesCleanly(file);
    }

    /// <summary>
    /// Proposes "Test Game" as the Target on a pwsh window, with a <see cref="ToneHub"/> playing
    /// three devices into its three Tracks, and once the session anchors, presses
    /// <c>recordToggle</c>: returns with the Recording running. With <paramref name="clips"/>, the
    /// orchestrator has Replay Clips (Save Cooldown 0) whose saves and refusals land in the events;
    /// with <paramref name="pending"/>, that document is committed and <see cref="SettingsUpdated"/>
    /// posted before the press.
    /// </summary>
    private async Task<Rig> StartRecordingAsync(
        bool clips = false,
        Func<SettingsDocument, SettingsDocument>? pending = null,
        Func<Exception?>? restartFault = null,
        ILoggerFactory? loggers = null,
        string windowScript = WindowScript)
    {
        SessionTestKit.RequireFFmpeg();
        var hub = new ToneHub(new Dictionary<string, (double, double)>
        {
            ["game"] = (0.1, 440),
            ["voice"] = (0.1, 660),
            ["mic"] = (0.2, 1000),
        });
        var game = new LayoutDevice(Guid.NewGuid(), "game", "Game device", "", SettingsFlow.Output, IsMic: false);
        var voice = new LayoutDevice(Guid.NewGuid(), "voice", "Voice device", "", SettingsFlow.Output, IsMic: false);
        var mic = new LayoutDevice(Guid.NewGuid(), "mic", "Mic device", "", SettingsFlow.Input, IsMic: true);
        var layout = new TrackLayout(
            [game, voice, mic],
            [
                new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false),
                new Track(Guid.NewGuid(), "Voice", [voice.Key], MuteAffected: false),
                new Track(Guid.NewGuid(), "Mic", [mic.Key], MuteAffected: true),
            ]);
        var document = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr)))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory))
                .SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Test Game",
                RecordingsEnabled = true,
                Overrides = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
            }),
        };

        SessionOrchestrator? orchestrator = null;
        try
        {
            var window = SessionTestKit.OpenWindow(_directory, _processes, windowScript);
            var openedAt = Stopwatch.GetTimestamp();
            var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
            var options = new CaptureSessionOptions
            {
                OutputFolder = _directory,
                Replay = true,
                Anchored = qpc => anchored.TrySetResult(qpc),
                RestartFault = restartFault,
            };
            var published = new ConcurrentQueue<CaptureState>();
            var library = new LibraryCalls();
            var factory = new CapturingFactory(new CaptureSessionFactory(options, loggers ?? NullLoggerFactory.Instance, hub));
            orchestrator = new SessionOrchestrator(
                factory,
                () => Volatile.Read(ref document),
                published.Enqueue,
                targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => events.Writer.TryComplete(e)),
                clips: clips
                    ? new ReplayClips(OutputPathAllocator.System, () => _directory, c => events.Writer.TryWrite(c), e => events.Writer.TryWrite(e), NotSaved: f => events.Writer.TryWrite(f))
                    : null,
                recordings: new Recordings(
                    OutputPathAllocator.System,
                    () => _directory,
                    s => events.Writer.TryWrite(s),
                    s => events.Writer.TryWrite(s),
                    e => events.Writer.TryWrite(e),
                    library,
                    Restarted: r => events.Writer.TryWrite(r)));

            orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
            await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
            if (pending is not null)
            {
                // Long enough for the session to build the chain it is asked to prepare.
                Volatile.Write(ref document, pending(document));
                Assert.True(orchestrator.Post(new SettingsUpdated()));
                await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
            }

            var start = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, start)));
            var started = Assert.IsType<RecordingStarted>(await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));
            return new Rig(orchestrator, hub, events, published, library, start, started) { Factory = factory, OpenedAt = openedAt };
        }
        catch
        {
            orchestrator?.Dispose();
            hub.Dispose();
            throw;
        }
    }

    /// <summary>A running Recording's orchestrator, what it raised, and the hub its Tracks hear; disposing it disposes both.</summary>
    private sealed class Rig(
        SessionOrchestrator orchestrator,
        ToneHub hub,
        System.Threading.Channels.Channel<object> events,
        ConcurrentQueue<CaptureState> published,
        LibraryCalls library,
        long start,
        RecordingStarted started) : IDisposable
    {
        public SessionOrchestrator Orchestrator { get; } = orchestrator;

        /// <summary>Each <c>recording.started</c>, <c>recording.stopped</c> and failure, in order.</summary>
        public System.Threading.Channels.Channel<object> Events { get; } = events;

        public ConcurrentQueue<CaptureState> Published { get; } = published;

        public LibraryCalls Library { get; } = library;

        /// <summary>The QPC of the press that started the Recording.</summary>
        public long Start { get; } = start;

        public RecordingStarted Started { get; } = started;

        /// <summary>The session factory, which keeps the session it started and its first failure.</summary>
        public required CapturingFactory Factory { get; init; }

        /// <summary>The QPC just after the game's window appeared.</summary>
        public long OpenedAt { get; init; }

        public async Task<object> NextAsync() => await Events.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

        public void Dispose()
        {
            Orchestrator.Dispose();
            hub.Dispose();
        }
    }

    /// <summary>Starts sessions with <paramref name="inner"/>, keeping the last one and the first failure any of them posted.</summary>
    private sealed class CapturingFactory(ICaptureSessionFactory inner) : ICaptureSessionFactory
    {
        private readonly TaskCompletionSource<Exception> _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CaptureSession? _session;

        public CaptureSession? Session => Volatile.Read(ref _session);

        public Task<Exception> Failed => _failed.Task;

        public ICaptureSession Start(SessionSpec spec, ISessionEvents events)
        {
            var session = inner.Start(spec, new Events(events, _failed));
            Volatile.Write(ref _session, (CaptureSession)session);
            return session;
        }

        private sealed class Events(ISessionEvents inner, TaskCompletionSource<Exception> failed) : ISessionEvents
        {
            public void Closed(string reason) => inner.Closed(reason);

            public void Failed(Exception failure)
            {
                failed.TrySetResult(failure);
                inner.Failed(failure);
            }

            public void ChainRestarted(long qpc) => inner.ChainRestarted(qpc);
        }
    }

    private sealed class NoEvents : ISessionEvents
    {
        public void Closed(string reason)
        {
        }

        public void Failed(Exception failure)
        {
        }
    }

    /// <summary>A catalog a session that never opens must not consult.</summary>
    private sealed class NoEncoders : IVideoEncoderCatalog
    {
        public string? EncoderFor(VideoCodec codec) => throw new InvalidOperationException("The catalog was consulted.");
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Records each Library call, in order.</summary>
    private sealed class LibraryCalls : ILibraryWrites
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public IEnumerable<string> Calls => _calls;

        public void BeginWrite(string path, string? profileId) => _calls.Enqueue($"begin {path} {profileId}");

        public void EndWrite(string path, WriteOutcome outcome) => _calls.Enqueue($"end {path} {outcome}");
    }
}
