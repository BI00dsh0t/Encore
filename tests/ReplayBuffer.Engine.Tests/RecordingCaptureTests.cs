using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
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

    private async Task RecordAsync(TimeSpan seconds)
    {
        SessionTestKit.RequireFFmpeg();
        using var hub = new ToneHub(new Dictionary<string, (double, double)>
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
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Test Game",
                RecordingsEnabled = true,
                Overrides = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
            }),
        };

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = System.Threading.Channels.Channel.CreateUnbounded<object>();
        var options = new CaptureSessionOptions { OutputFolder = _directory, Replay = true, Anchored = qpc => anchored.TrySetResult(qpc) };
        var published = new ConcurrentQueue<CaptureState>();
        var library = new LibraryCalls();

        long start, stop;
        RecordingStarted started;
        RecordingStopped stopped;
        using (var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub),
            () => document,
            published.Enqueue,
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => events.Writer.TryComplete(e)),
            recordings: new Recordings(
                OutputPathAllocator.System,
                () => _directory,
                s => events.Writer.TryWrite(s),
                s => events.Writer.TryWrite(s),
                e => events.Writer.TryWrite(e),
                library)))
        {
            orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
            await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(TimeSpan.FromSeconds(1), Token);

            start = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, start)));
            started = Assert.IsType<RecordingStarted>(await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));

            await Task.Delay(seconds, Token);
            stop = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, stop)));
            stopped = Assert.IsType<RecordingStopped>(await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));
        }

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

    /// <summary>Records each Library call, in order.</summary>
    private sealed class LibraryCalls : ILibraryWrites
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public IEnumerable<string> Calls => _calls;

        public void BeginWrite(string path, string? profileId) => _calls.Enqueue($"begin {path} {profileId}");

        public void EndWrite(string path, WriteOutcome outcome) => _calls.Enqueue($"end {path} {outcome}");
    }
}
