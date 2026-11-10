using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts.Settings;
using Encore.Core.Audio;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Platform.Audio;
using Encore.Platform.Audio.Tests;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>
/// The device dropout AC (R5), in process: a capture of the animated test window over a real
/// <see cref="AudioDeviceHub"/> built with fake tone clients A (440 Hz) and B (1 kHz) and a fake
/// endpoint list, with Tracks <c>A</c>={A}, <c>B</c>={B} and <c>AB</c>={A, B}. A leaves the list (and
/// its client goes quiet) for 10 s mid-capture, then returns under its id.
/// </summary>
public sealed class DeviceDropoutCaptureTests : IDisposable
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

    private const string IdA = "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-0000000000a1}";
    private const string IdB = "{0.0.0.00000000}.{bbbbbbbb-0000-0000-0000-0000000000b1}";
    private const double AmplitudeA = 0.1;
    private const double AmplitudeB = 0.2;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Dropout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan After = TimeSpan.FromSeconds(4);

    /// <summary>−60 dBFS.</summary>
    private static readonly double SilenceRms = Math.Pow(10, -60 / 20.0);

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-dropout-").FullName;
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
    public async Task ADeviceOutForTenSecondsIsSilentInItsTracksOnlyAndResumesWithoutARestart()
    {
        SessionTestKit.RequireFFmpeg();
        var source = new FakeEndpointList();
        var bank = new FakeDeviceBank();
        bank.Define(IdA, "Game", "BEACN", Platform.Audio.AudioFlow.Output, AmplitudeA, 440);
        bank.Define(IdB, "Mic", "BEACN", Platform.Audio.AudioFlow.Input, AmplitudeB, 1000);
        source.Add(IdA, "Game", "BEACN", SettingsFlow.Output, raise: false);
        source.Add(IdB, "Mic", "BEACN", SettingsFlow.Input, raise: false);
        using var hub = new AudioDeviceHub(NullLoggerFactory.Instance, bank.Create, source);
        var dropped = new ConcurrentQueue<long>();
        var resumed = new ConcurrentQueue<long>();
        hub.Dropped += _ => dropped.Enqueue(Stopwatch.GetTimestamp());
        hub.Resumed += _ => resumed.Enqueue(Stopwatch.GetTimestamp());
        var counting = new CountingHub(hub);

        var audio = new SessionAudio(
            [new SessionAudioDevice(IdA, SettingsFlow.Output, "Game"), new SessionAudioDevice(IdB, SettingsFlow.Input, "Mic")],
            [new SessionTrack("A", [0]), new SessionTrack("B", [1]), new SessionTrack("AB", [0, 1])]);

        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var output = Path.Combine(_directory, "dropout.mp4");
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output, Anchored = qpc => anchored.TrySetResult(qpc) };
        var video = new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr);
        var document = SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) };

        long anchor, removed, returned;
        var sessions = 0;
        using (var orchestrator = new SessionOrchestrator(new CaptureSessionFactory(options, NullLoggerFactory.Instance, counting), () => document, _ => { }))
        {
            await orchestrator.StartAsync(SessionKind.Game, new CaptureTarget((int)window.ProcessId, window.Handle, null), audio).WaitAsync(Timeout, Token);
            sessions++;
            anchor = await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(Before, Token);

            removed = Stopwatch.GetTimestamp();
            bank.Unplug(IdA);
            source.Remove(IdA);
            await Task.Delay(Dropout, Token);

            bank.SetOpenable(IdA, true);
            returned = Stopwatch.GetTimestamp();
            source.Add(IdA, "Game", "BEACN", SettingsFlow.Output);
            await Task.Delay(After, Token);

            var outcome = await orchestrator.EndAsync("test over").WaitAsync(Timeout, Token);
            Assert.Null(outcome.Failure);
        }

        // The hub dropped A once and resumed it once, within 2 s of its return.
        Assert.Single(dropped);
        var resumedAt = Assert.Single(resumed);
        Assert.InRange(resumedAt - returned, 0, 2 * Stopwatch.Frequency);

        // One session, one file; each device subscribed once, and A's second client fed the old subscription.
        Assert.Equal(1, sessions);
        Assert.Single(Directory.GetFiles(_directory, "*.mp4"));
        Assert.Equal(1, counting.Subscriptions(IdA));
        Assert.Equal(1, counting.Subscriptions(IdB));
        Assert.Equal(2, bank.Opens(IdA));
        Assert.Equal(1, bank.Opens(IdB));

        var videoDuration = AssertDurationsMatchTheVideo(output, 3);

        // Spans in file seconds: the anchor is the file's zero.
        var outAt = Seconds(removed - anchor);
        var backAt = Seconds(returned - anchor);
        var beforeSpan = (0.5, outAt - 0.1);
        var silentSpan = (outAt + 1.0, backAt);
        var afterSpan = (backAt + 2.0, videoDuration - 0.5);

        var trackA = Decode(output, 0);
        var trackB = Decode(output, 1);
        var trackAb = Decode(output, 2);
        var rmsA = AmplitudeA / Math.Sqrt(2);
        var rmsB = AmplitudeB / Math.Sqrt(2);
        var rmsAb = Math.Sqrt((rmsA * rmsA) + (rmsB * rmsB));

        AssertRms(Rms(trackA, beforeSpan), rmsA, "A before");
        Assert.True(Rms(trackA, silentSpan) < SilenceRms, $"Track A wasn't silent over the dropout: RMS {Rms(trackA, silentSpan)}");
        AssertRms(Rms(trackA, afterSpan), rmsA, "A after");

        AssertRms(Rms(trackB, beforeSpan), rmsB, "B before");
        AssertRms(Rms(trackB, silentSpan), rmsB, "B during");
        AssertRms(Rms(trackB, afterSpan), rmsB, "B after");

        AssertRms(Rms(trackAb, beforeSpan), rmsAb, "AB before");
        AssertRms(Rms(trackAb, silentSpan), rmsB, "AB during");
        AssertRms(Rms(trackAb, afterSpan), rmsAb, "AB after");
    }

    private static double Seconds(long qpcTicks) => (double)qpcTicks / Stopwatch.Frequency;

    private static void AssertRms(double actual, double expected, string what) =>
        Assert.True(actual >= expected * 0.9 && actual <= expected * 1.1, $"{what}: RMS {actual}, expected {expected}");

    /// <summary>The file holds video plus <paramref name="audioStreams"/> AAC streams, each within 0.1 s of the video's duration; returns that duration.</summary>
    private static double AssertDurationsMatchTheVideo(string file, int audioStreams)
    {
        var streams = SessionTestKit.Probe(file, "-show_streams").GetProperty("streams").EnumerateArray()
            .Select(s => (Codec: s.GetProperty("codec_name").GetString(), Duration: double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(["h264", .. Enumerable.Repeat("aac", audioStreams)], streams.Select(s => s.Codec));
        var video = streams[0].Duration;
        Assert.InRange(video, 15, 20);
        Assert.All(streams.Skip(1), s => Assert.InRange(s.Duration, video - 0.1, video + 0.1));
        return video;
    }

    /// <summary>The RMS of <paramref name="samples"/> between two file times, in seconds.</summary>
    private static double Rms(float[] samples, (double From, double To) span)
    {
        var from = (int)(span.From * AudioFormat.SampleRate);
        var to = Math.Min(samples.Length, (int)(span.To * AudioFormat.SampleRate));
        Assert.True(to - from > AudioFormat.SampleRate / 2, $"the span {span} is shorter than 0.5 s");
        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += samples[i] * samples[i];
        }

        return Math.Sqrt(sum / (to - from));
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

    /// <summary>Passes subscriptions to the real hub and counts them per endpoint.</summary>
    private sealed class CountingHub(IAudioDeviceHub inner) : IAudioDeviceHub
    {
        private readonly ConcurrentDictionary<string, int> _counts = new();

        public int Subscriptions(string endpointId) => _counts.GetValueOrDefault(endpointId);

        public IAudioSubscription Subscribe(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber)
        {
            _counts.AddOrUpdate(endpointId, 1, (_, n) => n + 1);
            return inner.Subscribe(endpointId, flow, subscriber);
        }
    }
}
