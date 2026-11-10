using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Library;
using Encore.Core.Output;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Engine.Settings;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>Crash-safe Recordings, in process: a "kill" copies the files as they are on disk, as a killed engine leaves them.</summary>
public sealed class RecordingCrashTests : IDisposable
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
    private const int Fps = 30;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-crash-").FullName;
    private readonly List<Process> _processes = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Output => Path.Combine(_directory, "output");

    private string Kills => Path.Combine(_directory, "kills");

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_directory).Replace(".", "-"), Path.Combine(_directory, "roaming"), Path.Combine(_directory, "local"));

    private string RuntimeFile => Path.Combine(TestInstance.LocalDataFolder, RuntimeStateStore.FileName);

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

    /// <summary>The kill is taken as a fragment reaches the disk, not at a fixed time, which a loaded run sometimes missed.</summary>
    [Fact]
    public Task ARecordingKilledThreeSecondsInPlaysToItsLastFragmentAndIsReportedAtTheNextStart() =>
        RecordAndKillAsync([TimeSpan.FromSeconds(3)], TimeSpan.FromSeconds(4), onFragment: true);

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public Task TenKillsAtRandomPointsOfALongRecordingEachPlayToUnderASecondBeforeTheKill() =>
        RecordAndKillAsync(
            [.. Enumerable.Range(0, 10).Select(_ => TimeSpan.FromSeconds(20 + (Random.Shared.NextDouble() * 70))).Order()],
            TimeSpan.FromSeconds(92));

    private static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = File.Create(to);
        source.CopyTo(target);
    }

    private static void CopyPrefix(string from, string to, long length)
    {
        using var source = File.OpenRead(from);
        using var target = File.Create(to);
        var buffer = new byte[length];
        source.ReadExactly(buffer);
        target.Write(buffer);
    }

    /// <summary>Each top-level <c>moof</c>: where its fragment starts and ends, its track count, and the video's decode time.</summary>
    private static List<(long Start, long End, int Trafs, long VideoTime)> Fragments(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var fragments = new List<(long, long, int, long)>();
        var boxes = Boxes(bytes, 0, bytes.Length).ToList();
        for (var i = 0; i < boxes.Count; i++)
        {
            var (type, start, end) = boxes[i];
            if (type != "moof")
            {
                continue;
            }

            Assert.True(i + 1 < boxes.Count && boxes[i + 1].Type == "mdat", $"the moof at {start} has no mdat after it");
            var trafs = Boxes(bytes, start + 8, end).Where(b => b.Type == "traf").ToList();
            var videoTime = -1L;
            foreach (var traf in trafs)
            {
                var children = Boxes(bytes, traf.Start + 8, traf.End).ToList();
                var tfhd = children.Single(b => b.Type == "tfhd");
                var trackId = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)tfhd.Start + 12));
                var tfdt = children.Single(b => b.Type == "tfdt");
                var version = bytes[tfdt.Start + 8];
                var time = version == 1
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan((int)tfdt.Start + 12))
                    : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)tfdt.Start + 12));
                if (trackId == 1)
                {
                    videoTime = time;
                }
            }

            fragments.Add((start, boxes[i + 1].End, trafs.Count, videoTime));
        }

        return fragments;
    }

    private static IEnumerable<(string Type, long Start, long End)> Boxes(byte[] bytes, long from, long to)
    {
        var position = from;
        while (position + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)position));
            var type = System.Text.Encoding.ASCII.GetString(bytes, (int)position + 4, 4);
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan((int)position + 8));
            }
            else if (size == 0)
            {
                size = to - position;
            }

            yield return (type, position, position + size);
            position += size;
        }
    }

    /// <summary>The file plays: ffprobe reads h264 and three AAC streams from it.</summary>
    private static void AssertPlays(string file) =>
        Assert.Equal(
            ["h264", "aac", "aac", "aac"],
            SessionTestKit.Probe(file, "-show_streams").GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_name").GetString()));

    /// <summary>Each stream's end, in seconds: its last packet's time plus its duration.</summary>
    private static double[] StreamEnds(string file) =>
        [.. SessionTestKit.Probe(file, "-show_entries", "packet=stream_index,pts_time,duration_time", "-show_packets")
            .GetProperty("packets").EnumerateArray()
            .GroupBy(p => p.GetProperty("stream_index").GetInt32())
            .OrderBy(g => g.Key)
            .Select(g => g.Max(p =>
                double.Parse(p.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture)
                + double.Parse(p.GetProperty("duration_time").GetString()!, CultureInfo.InvariantCulture)))];

    /// <summary>The length of the Recording's whole boxes on disk now (it grows as each fragment lands).</summary>
    private static long PlayableLength(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Mp4File.PlayableLength(stream);
    }

    /// <summary>Records for a length and kills at each given time; with onFragment each kill waits for the next fragment.</summary>
    private async Task RecordAndKillAsync(IReadOnlyList<TimeSpan> kills, TimeSpan length, bool onFragment = false)
    {
        SessionTestKit.RequireFFmpeg();
        Directory.CreateDirectory(Output);
        Directory.CreateDirectory(Kills);
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
                .SetItem(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, 640, 480, Fps, 4_000, RateControl.Cbr)))
                .SetItem(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(Output)),
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
        var options = new CaptureSessionOptions { OutputFolder = Output, Replay = true, Anchored = qpc => anchored.TrySetResult(qpc) };
        var store = new RuntimeStateStore(TestInstance.LocalDataFolder, NullLogger.Instance);

        long start;
        var killedAt = new List<double>();
        RecordingStarted started;
        using (var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub),
            () => document,
            _ => { },
            micMute: store,
            targetCapture: new TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), (_, e) => events.Writer.TryComplete(e)),
            recordings: new Recordings(
                OutputPathAllocator.System,
                () => Output,
                s => events.Writer.TryWrite(s),
                s => events.Writer.TryWrite(s),
                e => events.Writer.TryWrite(e),
                Open: store)))
        {
            orchestrator.Post(new ProposeTarget(new TargetCandidate((int)window.ProcessId, window.Handle, ProfileId, "testgame.exe"), TargetChangeReason.Detected));
            await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(TimeSpan.FromSeconds(1), Token);

            start = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, start)));
            started = Assert.IsType<RecordingStarted>(await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));

            // Each kill: the Recording and runtime.json as they are on disk at that instant.
            for (var i = 0; i < kills.Count; i++)
            {
                var wait = Stopwatch.GetElapsedTime(start) - kills[i];
                if (wait < TimeSpan.Zero)
                {
                    await Task.Delay(-wait, Token);
                }

                if (onFragment)
                {
                    var landed = PlayableLength(started.Path);
                    var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
                    while (PlayableLength(started.Path) == landed)
                    {
                        Assert.True(Stopwatch.GetTimestamp() < deadline, "no fragment landed after the kill time");
                        await Task.Delay(10, Token);
                    }
                }

                var at = Stopwatch.GetTimestamp();
                CopyShared(started.Path, Path.Combine(Kills, $"kill-{i}.mp4"));
                CopyShared(RuntimeFile, Path.Combine(Kills, $"runtime-{i}.json"));
                killedAt.Add((double)(at - start) / Stopwatch.Frequency);
            }

            var rest = length - Stopwatch.GetElapsedTime(start);
            if (rest > TimeSpan.Zero)
            {
                await Task.Delay(rest, Token);
            }

            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.RecordToggle, Stopwatch.GetTimestamp())));
            Assert.Equal(new RecordingStopped(started.Path, RecordingStopReasons.Stopped), await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token));
        }

        // A normal end takes the path out of runtime.json.
        Assert.DoesNotContain("openRecordings", File.ReadAllText(RuntimeFile), StringComparison.Ordinal);

        // The finished file: a moof with every stream's traf at least every 0.5 s of its length.
        var finished = started.Path;
        AssertPlays(finished);
        // A fragment ends on the first frame 0.5 s or more into it, so its last frame starts under 0.5 s after its first.
        var fragments = Fragments(finished);
        Assert.All(fragments, f => Assert.Equal(4, f.Trafs));
        var (pts, num, den) = SessionTestKit.VideoPtsTicks(finished);
        var ticks = (double)den / num;
        Assert.InRange(fragments[0].VideoTime, 0, 0.001 * ticks);
        for (var i = 0; i < fragments.Count; i++)
        {
            var next = i + 1 < fragments.Count ? fragments[i + 1].VideoTime : long.MaxValue;
            var lastFrame = pts.Last(p => p < next);
            Assert.True(
                lastFrame - fragments[i].VideoTime <= (0.5 + 0.001) * ticks,
                $"fragment {i} of {fragments.Count} starts at {fragments[i].VideoTime / ticks:0.000} s and its last frame at {lastFrame / ticks:0.000} s");
        }

        // Cut after any fragment, it plays; cut mid-fragment, it is trimmed back to the one before.
        for (var i = 0; i < fragments.Count; i++)
        {
            var cut = Path.Combine(Kills, $"cut-{i}.mp4");
            CopyPrefix(finished, cut, fragments[i].End);
            AssertPlays(cut);
            File.Delete(cut);
        }

        var middle = fragments[^1];
        var torn = Path.Combine(Kills, "torn.mp4");
        CopyPrefix(finished, torn, (middle.Start + middle.End) / 2);
        Assert.Equal(fragments.Count - 1, Mp4File.TrimToLastFragment(torn));
        Assert.Equal(fragments[^2].End, new FileInfo(torn).Length);
        Assert.True(Mp4File.IsComplete(torn));
        AssertPlays(torn);

        // Each kill, trimmed as the next start trims it: it plays, and its video ends under 1 s before the kill.
        var sampleRate = SessionTestKit.Probe(finished, "-select_streams", "a:0", "-show_streams").GetProperty("streams")[0].GetProperty("sample_rate").GetString()!;
        var step = (1024.0 / double.Parse(sampleRate, CultureInfo.InvariantCulture)) + (1.0 / Fps);
        for (var i = 0; i < kills.Count; i++)
        {
            var trimmed = Path.Combine(Kills, $"trimmed-{i}.mp4");
            File.Copy(Path.Combine(Kills, $"kill-{i}.mp4"), trimmed);
            Assert.True(Mp4File.TrimToLastFragment(trimmed) > 0);
            AssertPlays(trimmed);
            var ends = StreamEnds(trimmed);
            Assert.True(ends[0] < killedAt[i], $"kill {i} at {killedAt[i]:0.000} s: the video ends at {ends[0]:0.000} s");

            // The under-1 s bound is a latency measurement, so only the Heavy run, on a quiet machine, holds to it.
            if (!onFragment)
            {
                Assert.True(killedAt[i] - ends[0] < 1.0, $"kill {i} at {killedAt[i]:0.000} s: the video ends at {ends[0]:0.000} s, a second or more before");
            }
            Assert.All(ends.Skip(1), end => Assert.InRange(end, ends[0] - step, ends[0] + step));
            File.Delete(trimmed);
        }

        // The last kill's files put back: the next start trims the Recording and reports it, once.
        var last = kills.Count - 1;
        Assert.Contains("openRecordings", File.ReadAllText(Path.Combine(Kills, $"runtime-{last}.json")), StringComparison.Ordinal);
        File.Copy(Path.Combine(Kills, $"kill-{last}.mp4"), started.Path, overwrite: true);
        File.Copy(Path.Combine(Kills, $"runtime-{last}.json"), RuntimeFile, overwrite: true);
        await using (var host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource() }))
        {
            await using var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions(), Token);
            var warning = Assert.Single(client.State.Warnings);
            Assert.Equal(ErrorCodes.RecordingInterrupted, warning.Code);
            Assert.Equal(WarningSeverity.Warning, warning.Severity);
            Assert.Equal("recordings", warning.Source);
            Assert.True(warning.Overlay);
            Assert.True(warning.Dismissable);
            Assert.Contains(started.Path, warning.Message, StringComparison.Ordinal);
            Assert.Equal(RecordingState.Off, client.State.Capture.Recording);
        }

        Assert.True(Mp4File.IsComplete(started.Path));
        Assert.DoesNotContain("openRecordings", File.ReadAllText(RuntimeFile), StringComparison.Ordinal);
    }
}
