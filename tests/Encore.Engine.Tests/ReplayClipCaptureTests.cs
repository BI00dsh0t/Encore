using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Output;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Platform.FFmpeg;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>Rewind, in process: saves are refused when too short, saved, retried after a full disk, and cut during a drain.</summary>
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
        SessionTestKit.RequireCapture();
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
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory))
                .SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Test Game",
                Overrides = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
            }),
        };

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
        var refused = System.Threading.Channels.Channel.CreateUnbounded<ClipFailed>();
        var fault = new FaultSwitch();
        var options = new CaptureSessionOptions
        {
            OutputFolder = _directory,
            Replay = true,
            Anchored = qpc => anchored.TrySetResult(qpc),
        };
        var faults = new TestCaptureFaults
        {
            ClipWriteFault = path =>
            {
                if (!fault.TakeArmed())
                {
                    return null;
                }

                // A part file was begun before the volume filled up.
                File.WriteAllBytes(path + ClipWriter.PartExtension, [1, 2, 3]);
                return new ErrorCodeException(ErrorCodes.DiskFull, "test volume full");
            },
        };
        var published = new ConcurrentQueue<CaptureState>();

        long anchor, press, nextPress;
        ClipSaved clip, next, same;
        using (var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub, faults: faults),
            () => document,
            published.Enqueue,
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => saved.Writer.TryComplete(e)),
            clips: new ReplayClips(OutputPathAllocator.System, () => _directory, c => saved.Writer.TryWrite(c), _ => { }, f => refused.Writer.TryWrite(f))))
        {
            orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
            anchor = await anchored.Task.WaitAsync(Timeout, Token);
            Assert.Equal(ReplayState.Buffering, published.Last().Replay);

            // Right after the anchor there is too little to save.
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            Assert.Equal(new ClipFailed(ErrorCodes.ClipTooShort), await refused.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));

            await Task.Delay(Buffered, Token);
            press = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, press)));
            clip = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

            // About 1.5 s later a save fails with the disk full: it Resets nothing.
            await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
            fault.Arm();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            Assert.Equal(new ClipFailed(ErrorCodes.DiskFull), await refused.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));

            // The retry, 1.5 s after that, runs from the first one's end while capture goes on.
            await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
            nextPress = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, nextPress)));
            next = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

            // Two presses on one keyframe: one Clip, and the other press is too short.
            await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
            var samePress = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, samePress)));
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, samePress)));
            same = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
            Assert.Equal(new ClipFailed(ErrorCodes.ClipTooShort), await refused.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));
        }

        // Three Clips, none of them empty, and nothing left by the failed write or the refusal.
        var clips = Directory.GetFiles(Path.Combine(_directory, "Test Game"), "*.mp4");
        Assert.Equal(3, clips.Length);
        Assert.Contains(same.Path, clips);
        Assert.All(clips, c => Assert.True(new FileInfo(c).Length > 0, c));

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

    /// <summary>A one-shot switch the test arms and the writer thread takes.</summary>
    private sealed class FaultSwitch
    {
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public bool TakeArmed() => Interlocked.Exchange(ref _armed, 0) == 1;
    }

    [Fact]
    public async Task AKilledTargetIsDrainedSoASaveStillCutsItsLastMomentsUntilTheDrainExpires()
    {
        SessionTestKit.RequireCapture();
        using var hub = new ToneHub(new Dictionary<string, (double, double)> { ["game"] = (0.1, 440) });
        var game = new LayoutDevice(Guid.NewGuid(), "game", "Game device", "", SettingsFlow.Output, IsMic: false);
        var layout = new TrackLayout([game], [new Track(Guid.NewGuid(), "Game", [game.Key], MuteAffected: false)]);
        var document = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr)))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(_directory))
                .SetItem(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(0)),
            Profiles = SettingsMaps.EmptyProfiles.Add(ProfileId, new GameProfile
            {
                Name = "Test Game",
                Overrides = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
            }),
        };

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = System.Threading.Channels.Channel.CreateUnbounded<ClipSaved>();
        var options = new CaptureSessionOptions { OutputFolder = _directory, Replay = true, Anchored = qpc => anchored.TrySetResult(qpc) };
        var published = new ConcurrentQueue<CaptureState>();

        long anchor, killed;
        ClipSaved clip;
        using (var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub),
            () => document,
            published.Enqueue,
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => saved.Writer.TryComplete(e)),
            clips: new ReplayClips(OutputPathAllocator.System, () => _directory, c => saved.Writer.TryWrite(c), e => saved.Writer.TryComplete(e),
                NotSaved: f => saved.Writer.TryComplete(new InvalidOperationException($"the save made no Clip: {f.Reason}")),
                ExitDrain: TimeSpan.FromSeconds(3))))
        {
            orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
            anchor = await anchored.Task.WaitAsync(Timeout, Token);

            // About 3 s in, the game dies; its window closing drains the session.
            await Task.Delay(TimeSpan.FromSeconds(3), Token);
            using (var owner = Process.GetProcessById((int)window.ProcessId))
            {
                killed = Stopwatch.GetTimestamp();
                owner.Kill();
                owner.WaitForExit(TimeSpan.FromSeconds(10));
            }

            // The press goes in as soon as the drain is seen, so all of the 3 s drain is slack for a loaded machine.
            await WaitForReplay(published, ReplayState.Draining);
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            clip = await saved.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);

            // Once the drain expires the ring is gone: a press makes nothing.
            await WaitForReplay(published, ReplayState.Off);
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.Save, Stopwatch.GetTimestamp())));
            await Task.Delay(TimeSpan.FromSeconds(1.5), Token);
            Assert.False(saved.Reader.TryRead(out _));
        }

        // The Clip runs from the anchor's keyframe to the game's last moments.
        var video = SessionTestKit.Probe(clip.Path, "-show_streams").GetProperty("streams").EnumerateArray()
            .First(s => s.GetProperty("codec_type").GetString() == "video");
        var killSeconds = (double)(killed - anchor) / Stopwatch.Frequency;
        Assert.InRange(double.Parse(video.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture), killSeconds - 0.5, killSeconds + 0.5);
        Assert.Single(Directory.GetFiles(_directory, "*.mp4", SearchOption.AllDirectories));
    }

    private static async Task WaitForReplay(ConcurrentQueue<CaptureState> published, ReplayState state)
    {
        var deadline = Stopwatch.StartNew();
        while (published.LastOrDefault()?.Replay != state)
        {
            Assert.True(deadline.Elapsed < Timeout, $"The state never became {state}.");
            await Task.Delay(20, Token);
        }
    }

    /// <summary>The types of the file's top-level MP4 boxes, in order.</summary>
    internal static List<string> TopLevelBoxes(string file)
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
