using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Output;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.FFmpeg;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// The Replay Buffer tracer (story 4.1), in process: a <see cref="ToneHub"/> plays three devices,
/// the Game Profile "Test Game" overrides the Track Layout with three Tracks, and its game is
/// proposed as the Target on a pwsh window. A <c>save</c> press right after the anchor is refused
/// as too short (story 4.2); about 4 s after the anchor one saves a Clip, read back with ffprobe and
/// as MP4 boxes, and about 2 s later another saves the time between the two presses.
/// </summary>
public sealed partial class ReplayClipCaptureTests : IDisposable
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
    private static readonly TimeSpan Buffered = TimeSpan.FromSeconds(4);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-replay-").FullName;
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
    public async Task ASavePressWritesAFaststartClipOfTheBufferWithOneTitledStreamPerTrack()
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
        var video = new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr);

        // The layout is the profile's own: three Tracks prove the session took its snapshot.
        var document = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(video))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Test Game",
                Overrides = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
            }),
        };

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
        var refused = new TaskCompletionSource<ClipFailed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CaptureSessionOptions { OutputFolder = _directory, Replay = true, Anchored = qpc => anchored.TrySetResult(qpc) };
        var published = new ConcurrentQueue<CaptureState>();

        long anchor, press, nextPress;
        ClipSaved clip, next;
        using (var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub),
            () => document,
            published.Enqueue,
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => saved.Writer.TryComplete(e)),
            clips: new ReplayClips(OutputPathAllocator.System, () => _directory, c => saved.Writer.TryWrite(c), e => saved.Writer.TryComplete(e), f => refused.TrySetResult(f))))
        {
            orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
            anchor = await anchored.Task.WaitAsync(Timeout, Token);
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);

            // Right after the anchor there is too little to save.
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            Assert.Equal(new ClipFailed(ErrorCodes.ClipTooShort), await refused.Task.WaitAsync(Timeout, Token));

            await Task.Delay(Buffered, Token);
            press = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press)));
            clip = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

            // A second save, about 2 s later, runs from the first one's end while capture goes on.
            await Task.Delay(TimeSpan.FromSeconds(2), Token);
            nextPress = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, nextPress)));
            next = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
        }

        // <outputFolder>\<profile folder>\<Name>_<time>.mp4, and nothing else beside it.
        Assert.Equal(Path.Combine(_directory, "Test Game"), Path.GetDirectoryName(clip.Path));
        Assert.Matches(ClipName(), Path.GetFileName(clip.Path));
        Assert.Empty(Directory.GetFiles(_directory, "*" + ClipWriter.PartExtension, SearchOption.AllDirectories));

        var streams = SessionTestKit.Probe(clip.Path, "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => (
                Codec: s.GetProperty("codec_name").GetString(),
                Title: s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var title) ? title.GetString() : null,
                Default: s.GetProperty("disposition").GetProperty("default").GetInt32() == 1,
                Duration: double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(["h264", "aac", "aac", "aac"], streams.Select(s => s.Codec));
        Assert.Equal(["Game", "Voice", "Mic"], streams.Skip(1).Select(s => s.Title));
        Assert.Equal([true, false, false], streams.Skip(1).Select(s => s.Default));

        // The whole buffer is inside the window: the Clip starts at the anchor's keyframe and ends at the press.
        var videoDuration = streams[0].Duration;
        var pressSeconds = (double)(press - anchor) / Stopwatch.Frequency;
        Assert.InRange(videoDuration, 2.5, 5);
        Assert.InRange(videoDuration, pressSeconds - 0.5, pressSeconds + 0.5);
        Assert.All(streams.Skip(1), s => Assert.InRange(s.Duration, videoDuration - 0.1, videoDuration + 0.1));
        SessionTestKit.AssertDecodesCleanly(clip.Path);

        // Faststart: the index before the media.
        var boxes = TopLevelBoxes(clip.Path);
        Assert.Contains("moov", boxes);
        Assert.Contains("mdat", boxes);
        Assert.True(boxes.IndexOf("moov") < boxes.IndexOf("mdat"), $"boxes: {string.Join(", ", boxes)}");

        // The second Clip covers the time between the presses.
        var between = (double)(nextPress - press) / Stopwatch.Frequency;
        var nextVideo = SessionTestKit.Probe(next.Path, "-show_streams").GetProperty("streams").EnumerateArray()
            .First(s => s.GetProperty("codec_type").GetString() == "video");
        Assert.InRange(double.Parse(nextVideo.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture), between - 0.1, between + 0.1);
    }

    /// <summary>The types of the file's top-level MP4 boxes, in order.</summary>
    private static List<string> TopLevelBoxes(string file)
    {
        var types = new List<string>();
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            var start = stream.Position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            types.Add(System.Text.Encoding.ASCII.GetString(header[4..8]));
            if (size == 1)
            {
                stream.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            }
            else if (size == 0)
            {
                break;
            }

            stream.Position = start + size;
        }

        return types;
    }

    [GeneratedRegex(@"^Test Game_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(_\d+)?\.mp4$")]
    private static partial Regex ClipName();
}
