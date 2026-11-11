using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Audio;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>Mic mute in a capture, in process: a press mutes the mic in the Tracks it affects, and a second one unmutes it.</summary>
public sealed class MicMuteCaptureTests : IDisposable
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

    private const double GameAmplitude = 0.1;
    private const double MicAmplitude = 0.2;
    private const double GameHz = 440;
    private const double MicHz = 1000;

    /// <summary>How far from each press the spans keep: the press's handling, and AAC's transform window around an edge.</summary>
    private const double EdgeSeconds = 0.05;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Muted = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan After = TimeSpan.FromSeconds(1.5);

    /// <summary>−60 dBFS, as an RMS and as a tone's amplitude.</summary>
    private static readonly double Silence = Math.Pow(10, -60 / 20.0);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-micmute-").FullName;
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

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AMuteSilencesTheMicInItsAffectedTracksOnlyFromThePressUntilTheUnmute()
    {
        SessionTestKit.RequireCapture();
        var audio = new SessionAudio(
            [new SessionAudioDevice("game", SettingsFlow.Output, "Game device"), new SessionAudioDevice("mic", SettingsFlow.Input, "Mic device", IsMic: true)],
            [
                new SessionTrack("Everything", [0, 1], MuteAffected: true),
                new SessionTrack("Game", [0]),
                new SessionTrack("Mic", [1], MuteAffected: true),
                new SessionTrack("MicRaw", [1]),
            ]);
        using var hub = new ToneHub(new Dictionary<string, (double, double)> { ["game"] = (GameAmplitude, GameHz), ["mic"] = (MicAmplitude, MicHz) });

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var output = Path.Combine(_directory, "micmute.mp4");
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output, Anchored = qpc => anchored.TrySetResult(qpc) };
        var video = new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr);
        var document = SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) };
        var published = new ConcurrentQueue<CaptureState>();
        var store = new UnmutedStore();

        long anchor, mute, unmute;
        using (var orchestrator = new SessionOrchestrator(
            new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub), () => document, published.Enqueue, micMute: store))
        {
            var running = await orchestrator.StartAsync(SessionKind.Game, new CaptureTarget((int)window.ProcessId, window.Handle, null), audio).WaitAsync(Timeout, Token);
            Assert.False(running.Spec.MicMuted);
            anchor = await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(Before, Token);

            mute = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.MicMute, mute)));
            await Task.Delay(Muted, Token);

            unmute = Stopwatch.GetTimestamp();
            Assert.True(orchestrator.Post(new HotkeyPressed(HotkeyAction.MicMute, unmute)));
            await Task.Delay(After, Token);

            var outcome = await orchestrator.EndAsync("test over").WaitAsync(Timeout, Token);
            Assert.Null(outcome.Failure);
        }

        // Loaded unmuted (published first, as it differs from the initial state), then muted, then unmuted.
        Assert.Equal([false, true, false], Changes(published.Select(s => s.MicMuted)));
        Assert.Equal([true, false], store.Saves);

        var videoDuration = AssertDurationsMatchTheVideo(output, 4);
        var mutedAt = Seconds(mute - anchor);
        var unmutedAt = Seconds(unmute - anchor);
        var beforeSpan = (0.5, mutedAt - EdgeSeconds);
        var mutedSpan = (mutedAt + EdgeSeconds, unmutedAt - EdgeSeconds);
        var afterSpan = (unmutedAt + EdgeSeconds, videoDuration - 0.5);
        var wholeSpan = (0.5, videoDuration - 0.5);

        var everything = Decode(output, 0);
        var game = Decode(output, 1);
        var mic = Decode(output, 2);
        var micRaw = Decode(output, 3);
        var gameRms = GameAmplitude / Math.Sqrt(2);
        var micRms = MicAmplitude / Math.Sqrt(2);

        // Mic: the tone, silence over the mute, the tone again.
        AssertNear(Rms(mic, beforeSpan), micRms, "Mic before");
        Assert.True(Rms(mic, mutedSpan) < Silence, $"Mic wasn't silent while muted: RMS {Rms(mic, mutedSpan)}");
        AssertNear(Rms(mic, afterSpan), micRms, "Mic after");

        // MicRaw and Game: untouched throughout.
        AssertNear(Rms(micRaw, wholeSpan), micRms, "MicRaw");
        AssertNear(Rms(micRaw, mutedSpan), micRms, "MicRaw while muted");
        AssertNear(Rms(game, wholeSpan), gameRms, "Game");
        AssertNear(Tone(game, mutedSpan, MicHz), 0, Silence, "Game's 1 kHz while muted");

        // Everything: G throughout, M's 1 kHz only outside the mute.
        foreach (var (span, name) in new[] { (beforeSpan, "before"), (mutedSpan, "while muted"), (afterSpan, "after") })
        {
            AssertNear(Tone(everything, span, GameHz), GameAmplitude, $"Everything's 440 Hz {name}");
        }

        AssertNear(Tone(everything, beforeSpan, MicHz), MicAmplitude, "Everything's 1 kHz before");
        AssertNear(Tone(everything, mutedSpan, MicHz), 0, Silence, "Everything's 1 kHz while muted");
        AssertNear(Tone(everything, afterSpan, MicHz), MicAmplitude, "Everything's 1 kHz after");
    }

    /// <summary>The values where they change, the first included.</summary>
    private static List<bool> Changes(IEnumerable<bool> values)
    {
        var changes = new List<bool>();
        foreach (var value in values)
        {
            if (changes.Count == 0 || changes[^1] != value)
            {
                changes.Add(value);
            }
        }

        return changes;
    }

    private static double Seconds(long qpcTicks) => (double)qpcTicks / Stopwatch.Frequency;

    private static void AssertNear(double actual, double expected, string what) =>
        Assert.True(actual >= expected * 0.9 && actual <= expected * 1.1, $"{what}: {actual}, expected {expected}");

    private static void AssertNear(double actual, double expected, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{what}: {actual}, expected {expected} ± {tolerance}");

    /// <summary>The file holds the video and the AAC streams, each within 0.1 s of the video's duration, which is returned.</summary>
    private static double AssertDurationsMatchTheVideo(string file, int audioStreams)
    {
        var streams = SessionTestKit.Probe(file, "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => (Codec: s.GetProperty("codec_name").GetString(), Duration: double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(["h264", .. Enumerable.Repeat("aac", audioStreams)], streams.Select(s => s.Codec));
        var video = streams[0].Duration;
        Assert.InRange(video, 4, 9);
        Assert.All(streams.Skip(1), s => Assert.InRange(s.Duration, video - 0.1, video + 0.1));
        return video;
    }

    private static (int From, int To) Frames(float[] samples, (double From, double To) span)
    {
        var from = (int)(span.From * AudioFormat.SampleRate);
        var to = Math.Min(samples.Length, (int)(span.To * AudioFormat.SampleRate));
        Assert.True(to - from > AudioFormat.SampleRate / 2, $"the span {span} is shorter than 0.5 s");
        return (from, to);
    }

    /// <summary>The RMS of <paramref name="samples"/> between two file times, in seconds.</summary>
    private static double Rms(float[] samples, (double From, double To) span)
    {
        var (from, to) = Frames(samples, span);
        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += samples[i] * samples[i];
        }

        return Math.Sqrt(sum / (to - from));
    }

    /// <summary>The amplitude of the <paramref name="hz"/> component between two file times (Goertzel).</summary>
    private static double Tone(float[] samples, (double From, double To) span, double hz)
    {
        var (from, to) = Frames(samples, span);
        var n = to - from;
        var coefficient = 2 * Math.Cos(2 * Math.PI * hz / AudioFormat.SampleRate);
        double s1 = 0, s2 = 0;
        for (var i = from; i < to; i++)
        {
            var s0 = samples[i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return 2 * Math.Sqrt(Math.Max(0, power)) / n;
    }

    /// <summary>Audio stream <paramref name="index"/>'s left channel, decoded whole.</summary>
    private float[] Decode(string file, int index)
    {
        var raw = Path.Combine(_directory, $"a{index}.f32");
        var start = new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, "ffmpeg.exe"))
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-y", "-i", file, "-map", $"0:a:{index}", "-f", "f32le", raw })
        {
            start.ArgumentList.Add(arg);
        }

        using (var ffmpeg = Process.Start(start)!)
        {
            var stderr = ffmpeg.StandardError.ReadToEnd();
            ffmpeg.WaitForExit();
            Assert.True(ffmpeg.ExitCode == 0, stderr);
        }

        var samples = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(raw));
        var left = new float[samples.Length / AudioFormat.Channels];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = samples[i * AudioFormat.Channels];
        }

        return left;
    }

    /// <summary>A store whose saved state is unmuted, recording each save.</summary>
    private sealed class UnmutedStore : IMicMuteStore
    {
        private readonly ConcurrentQueue<bool> _saves = new();

        public IEnumerable<bool> Saves => _saves;

        public bool Load() => false;

        public void Save(bool muted) => _saves.Enqueue(muted);
    }
}
