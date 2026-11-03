using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// The session's Track wiring, in process: a fake hub plays a known tone on each of two devices,
/// and each Track's decoded stream carries exactly its devices' tones. Track <c>Out</c> holds the
/// output device, <c>Both</c> the output and the input (the output shared), <c>In</c> the input,
/// and <c>Silent</c> none; a second case sums two full-scale tones in one Track, which the limiter
/// holds within full scale.
/// </summary>
public sealed class TrackMixCaptureTests : IDisposable
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

    private const double OutAmplitude = 0.1;
    private const double InAmplitude = 0.2;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-mix-").FullName;
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
    public async Task EachTrackCarriesExactlyItsDevicesTones()
    {
        var audio = new SessionAudio(
            [new SessionAudioDevice("out", SettingsFlow.Output, "Out device"), new SessionAudioDevice("in", SettingsFlow.Input, "In device")],
            [new SessionTrack("Out", [0]), new SessionTrack("Both", [0, 1]), new SessionTrack("In", [1]), new SessionTrack("Silent", [])]);
        using var hub = new ToneHub(new Dictionary<string, (double, double)> { ["out"] = (OutAmplitude, 440), ["in"] = (InAmplitude, 1000) });

        var output = await CaptureAsync(audio, hub);

        Assert.Equal(1, hub.Subscriptions("out"));
        Assert.Equal(1, hub.Subscriptions("in"));

        // A sine of amplitude a has RMS a/√2; two tones at different frequencies add in power.
        var outRms = OutAmplitude / Math.Sqrt(2);
        var inRms = InAmplitude / Math.Sqrt(2);
        AssertRms(Rms(Decode(output, 0)), outRms);
        AssertRms(Rms(Decode(output, 1)), Math.Sqrt((outRms * outRms) + (inRms * inRms)));
        AssertRms(Rms(Decode(output, 2)), inRms);

        // The Track with no devices is silence (below -90 dBFS), and every Track lasts as long as the video.
        Assert.InRange(Peak(Decode(output, 3)), 0, Math.Pow(10, -90 / 20.0));
        AssertDurationsMatchTheVideo(output, 4);
    }

    [Fact]
    public async Task TwoFullScaleTonesInOneTrackStayWithinFullScale()
    {
        var audio = new SessionAudio(
            [new SessionAudioDevice("a", SettingsFlow.Output, "A device"), new SessionAudioDevice("b", SettingsFlow.Input, "B device")],
            [new SessionTrack("Loud", [0, 1])]);
        using var hub = new ToneHub(new Dictionary<string, (double, double)> { ["a"] = (1.0, 440), ["b"] = (1.0, 660) });

        var output = await CaptureAsync(audio, hub);

        // The raw sum peaks near 2; the limiter holds it at full scale, give or take AAC's coding error.
        var samples = Decode(output, 0);
        Assert.InRange(Peak(samples), 0.5, 1.0 + 0.05);
        AssertDurationsMatchTheVideo(output, 1);
    }

    /// <summary>Runs a 4 s session of the test window with <paramref name="audio"/> from <paramref name="hub"/>; returns the file.</summary>
    private async Task<string> CaptureAsync(SessionAudio audio, ToneHub hub)
    {
        SessionTestKit.RequireFFmpeg();
        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var output = Path.Combine(_directory, "mix.mp4");
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output, Anchored = qpc => anchored.TrySetResult(qpc) };
        var video = new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr);
        var document = SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) };

        using (var orchestrator = new SessionOrchestrator(new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub), () => document, _ => { }))
        {
            await orchestrator.StartAsync(SessionKind.Game, new CaptureTarget((int)window.ProcessId, window.Handle, null), audio).WaitAsync(Timeout, Token);
            await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(TimeSpan.FromSeconds(4), Token);
            var outcome = await orchestrator.EndAsync("test over").WaitAsync(Timeout, Token);
            Assert.Null(outcome.Failure);
        }

        return output;
    }

    private static void AssertRms(double actual, double expected) =>
        Assert.InRange(actual, expected * 0.9, expected * 1.1);

    /// <summary>The file holds video plus <paramref name="audioStreams"/> AAC streams, each within 0.1 s of the video's duration.</summary>
    private static void AssertDurationsMatchTheVideo(string file, int audioStreams)
    {
        var streams = SessionTestKit.Probe(file, "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => (Codec: s.GetProperty("codec_name").GetString(), Duration: double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(["h264", .. Enumerable.Repeat("aac", audioStreams)], streams.Select(s => s.Codec));
        var video = streams[0].Duration;
        Assert.InRange(video, 3, 6);
        Assert.All(streams.Skip(1), s => Assert.InRange(s.Duration, video - 0.1, video + 0.1));
    }

    /// <summary>Audio stream <paramref name="index"/>'s left channel, decoded, without its first and last 0.5 s.</summary>
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

        // Interleaved stereo; the tones are identical on both channels, so the left one is read.
        var samples = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(raw));
        var edge = AudioFormat.SampleRate / 2;
        var frames = samples.Length / AudioFormat.Channels;
        Assert.True(frames > 4 * edge, $"stream {index} holds only {frames} frames");
        var left = new float[frames - (2 * edge)];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = samples[(edge + i) * AudioFormat.Channels];
        }

        return left;
    }

    private static double Rms(float[] samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private static double Peak(float[] samples) => samples.Max(Math.Abs);
}
