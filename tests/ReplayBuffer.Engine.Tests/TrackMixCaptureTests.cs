using System.Diagnostics;
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
/// output device, <c>Both</c> the output and the input (the output shared), <c>In</c> the input.
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
        SessionTestKit.RequireFFmpeg();
        var window = SessionTestKit.OpenWindow(_directory, _processes, WindowScript);
        var output = Path.Combine(_directory, "mix.mp4");
        var anchored = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output, Anchored = qpc => anchored.TrySetResult(qpc) };
        var video = new VideoSettings(VideoCodec.H264, 640, 480, 30, 4_000, RateControl.Cbr);
        var document = SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) };
        var audio = new SessionAudio(
            [new SessionAudioDevice("out", SettingsFlow.Output, "Out device"), new SessionAudioDevice("in", SettingsFlow.Input, "In device")],
            [new SessionTrack("Out", [0]), new SessionTrack("Both", [0, 1]), new SessionTrack("In", [1])]);

        using var hub = new ToneHub(new Dictionary<string, (double, double)> { ["out"] = (OutAmplitude, 440), ["in"] = (InAmplitude, 1000) });
        using (var orchestrator = new SessionOrchestrator(new CaptureSessionFactory(options, NullLoggerFactory.Instance, hub), () => document, _ => { }))
        {
            await orchestrator.StartAsync(SessionKind.Game, new CaptureTarget((int)window.ProcessId, window.Handle, null), audio).WaitAsync(Timeout, Token);
            await anchored.Task.WaitAsync(Timeout, Token);
            await Task.Delay(TimeSpan.FromSeconds(4), Token);
            var outcome = await orchestrator.EndAsync("test over").WaitAsync(Timeout, Token);
            Assert.Null(outcome.Failure);
        }

        Assert.Equal(1, hub.Subscriptions("out"));
        Assert.Equal(1, hub.Subscriptions("in"));

        // A sine of amplitude a has RMS a/√2; two tones at different frequencies add in power.
        var outRms = OutAmplitude / Math.Sqrt(2);
        var inRms = InAmplitude / Math.Sqrt(2);
        AssertRms(Rms(output, 0), outRms);
        AssertRms(Rms(output, 1), Math.Sqrt((outRms * outRms) + (inRms * inRms)));
        AssertRms(Rms(output, 2), inRms);
    }

    private static void AssertRms(double actual, double expected) =>
        Assert.InRange(actual, expected * 0.9, expected * 1.1);

    /// <summary>The RMS of audio stream <paramref name="index"/>'s left channel, past its first and before its last 0.5 s.</summary>
    private double Rms(string file, int index)
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
        double sum = 0;
        for (var i = edge; i < frames - edge; i++)
        {
            var s = samples[i * AudioFormat.Channels];
            sum += s * s;
        }

        return Math.Sqrt(sum / (frames - (2 * edge)));
    }

    /// <summary>
    /// A hub whose every endpoint plays one sine, identical on both channels, in real time: 10 ms
    /// blocks stamped contiguously from the subscription's start QPC, as the real hub's are.
    /// </summary>
    private sealed class ToneHub(IReadOnlyDictionary<string, (double Amplitude, double Hz)> tones) : IAudioDeviceHub, IDisposable
    {
        private readonly List<Tone> _running = [];
        private readonly Dictionary<string, int> _subscriptions = [];

        public int Subscriptions(string endpointId)
        {
            lock (_running)
            {
                return _subscriptions.GetValueOrDefault(endpointId);
            }
        }

        public IAudioSubscription Subscribe(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber)
        {
            var (amplitude, hz) = tones[endpointId];
            var tone = new Tone(amplitude, hz, subscriber);
            lock (_running)
            {
                _subscriptions[endpointId] = _subscriptions.GetValueOrDefault(endpointId) + 1;
                _running.Add(tone);
            }

            return tone;
        }

        public void Dispose()
        {
            lock (_running)
            {
                foreach (var tone in _running)
                {
                    tone.Dispose();
                }
            }
        }

        private sealed class Tone : IAudioSubscription
        {
            private const int BlockFrames = AudioFormat.SampleRate / 100;

            private readonly double _amplitude;
            private readonly double _hz;
            private readonly IAudioPcmSubscriber _subscriber;
            private readonly Thread _thread;
            private volatile bool _stop;

            public Tone(double amplitude, double hz, IAudioPcmSubscriber subscriber)
            {
                _amplitude = amplitude;
                _hz = hz;
                _subscriber = subscriber;
                _thread = new Thread(Run) { IsBackground = true, Name = "ToneHub" };
                _thread.Start();
            }

            public Exception? DeviceFault => null;

            public Exception? SubscriberFault { get; private set; }

            public void Dispose()
            {
                _stop = true;
                if (Thread.CurrentThread != _thread)
                {
                    _thread.Join();
                }
            }

            private void Run()
            {
                var origin = Stopwatch.GetTimestamp();
                var block = new float[BlockFrames * AudioFormat.Channels];
                long frame = 0;
                while (!_stop)
                {
                    var startQpc = origin + (frame * Stopwatch.Frequency / AudioFormat.SampleRate);
                    var due = origin + ((frame + BlockFrames) * Stopwatch.Frequency / AudioFormat.SampleRate);
                    while (Stopwatch.GetTimestamp() < due && !_stop)
                    {
                        Thread.Sleep(1);
                    }

                    for (var i = 0; i < BlockFrames; i++)
                    {
                        var value = (float)(_amplitude * Math.Sin(2 * Math.PI * _hz * (frame + i) / AudioFormat.SampleRate));
                        block[2 * i] = value;
                        block[(2 * i) + 1] = value;
                    }

                    if (SubscriberFault is null)
                    {
                        try
                        {
                            _subscriber.OnPcm(new AudioBlock(startQpc, BlockFrames, block));
                        }
                        catch (Exception e)
                        {
                            SubscriberFault = e;
                        }
                    }

                    frame += BlockFrames;
                }
            }
        }
    }
}
