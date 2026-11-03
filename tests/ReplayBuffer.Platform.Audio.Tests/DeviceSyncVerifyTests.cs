using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Media;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Platform.Audio.Tests;

/// <summary>
/// Finds the onsets of <see cref="TonePlayer"/>'s 3 kHz bursts in a loopback stream: a 2 ms
/// quadrature detector at 3 kHz (one cycle is 16 frames at 48 kHz), with an onset where its
/// envelope first crosses the threshold, re-armed after 200 ms below a lower one. Allocation-free.
/// </summary>
internal sealed class BurstDetector : IAudioPcmSubscriber
{
    private const int Window = 96;
    private const double On = 0.004;
    private const double Off = 0.0015;
    private const int RearmFrames = AudioFormat.SampleRate / 5;

    private static readonly double[] Cos = [.. Enumerable.Range(0, 16).Select(k => Math.Cos(2 * Math.PI * k / 16))];
    private static readonly double[] Sin = [.. Enumerable.Range(0, 16).Select(k => Math.Sin(2 * Math.PI * k / 16))];

    private readonly double[] _i = new double[Window];
    private readonly double[] _q = new double[Window];
    private readonly long[] _onsets = new long[8192];
    private readonly long _qpf = Stopwatch.Frequency;
    private double _sumI;
    private double _sumQ;
    private long _frame;
    private bool _armed = true;
    private int _below;
    private int _onsetCount;

    public int OnsetCount => Volatile.Read(ref _onsetCount);

    public long Onset(int index) => _onsets[index];

    public void OnPcm(in AudioBlock block)
    {
        for (var n = 0; n < block.Frames; n++)
        {
            var x = block.IsSilent ? 0 : 0.5 * (block.Interleaved[2 * n] + block.Interleaved[(2 * n) + 1]);
            var slot = (int)(_frame % Window);
            var phase = (int)(_frame % 16);
            _sumI += (x * Cos[phase]) - _i[slot];
            _sumQ += (x * Sin[phase]) - _q[slot];
            _i[slot] = x * Cos[phase];
            _q[slot] = x * Sin[phase];
            _frame++;

            var envelope = 2 * Math.Sqrt((_sumI * _sumI) + (_sumQ * _sumQ)) / Window;
            if (_armed && envelope > On)
            {
                if (_onsetCount < _onsets.Length)
                {
                    _onsets[_onsetCount] = block.StartQpc + QpcClock.ToUnits(n, AudioFormat.SampleRate, _qpf);
                    Volatile.Write(ref _onsetCount, _onsetCount + 1);
                }

                _armed = false;
                _below = 0;
            }
            else if (!_armed)
            {
                _below = envelope < Off ? _below + 1 : 0;
                if (_below >= RearmFrames)
                {
                    _armed = true;
                }
            }
        }
    }
}

/// <summary>Counts what reaches it; the mic's own stream is read through the hub's diagnostics.</summary>
internal sealed class CountingSubscriber : IAudioPcmSubscriber
{
    private long _frames;

    public long Frames => Interlocked.Read(ref _frames);

    public void OnPcm(in AudioBlock block) => Interlocked.Add(ref _frames, block.Frames);
}

/// <summary>
/// Forwards to <paramref name="inner"/> and records each block's delivery age: QPC now minus the
/// QPC its last frame stands for, in samples. The smallest age in each window is the stream's lag
/// behind wall-clock QPC without the scheduling noise of the delivery itself.
/// </summary>
internal sealed class DeliveryAgeTap(IAudioPcmSubscriber inner) : IAudioPcmSubscriber
{
    private readonly long _qpf = Stopwatch.Frequency;
    private long _minAgeTicks = long.MaxValue;

    public void OnPcm(in AudioBlock block)
    {
        inner.OnPcm(in block);
        var age = Stopwatch.GetTimestamp() - (block.StartQpc + QpcClock.ToUnits(block.Frames, AudioFormat.SampleRate, _qpf));
        var seen = Interlocked.Read(ref _minAgeTicks);
        while (age < seen)
        {
            var was = Interlocked.CompareExchange(ref _minAgeTicks, age, seen);
            if (was == seen)
            {
                break;
            }

            seen = was;
        }
    }

    /// <summary>The smallest age since the last call, in samples, and starts a new window; null if no block came.</summary>
    public double? TakeMinAgeSamples()
    {
        var ticks = Interlocked.Exchange(ref _minAgeTicks, long.MaxValue);
        return ticks == long.MaxValue ? null : (double)ticks * AudioFormat.SampleRate / _qpf;
    }
}

/// <summary>
/// Story 1.12's verify, on the builder's PC: a quiet tone with a 3 kHz burst each second plays into
/// the BEACN Game output while the hub captures its loopback and the BEACN Mic. Once a second, each
/// stream's error (where its latest packet's content comes out minus where its stamp puts it, in
/// samples) is read; the loopback's burst onsets are matched to the player's QPC for each burst.
/// Independently of the stamps, each stream's lag behind wall-clock QPC is read as the smallest
/// delivery age of its blocks in each second; that lag holds the capture latency but must not drift. Both errors, their difference,
/// the spread of each lag and of the out − mic lag, and the spread of the burst offsets must stay within one AAC frame
/// (1024 samples). 60 s normally, 10 minutes with <c>REPLAYBUFFER_LONG_TESTS=1</c>. The devices are
/// found by name, or by <c>REPLAYBUFFER_TEST_OUTPUT</c> / <c>REPLAYBUFFER_TEST_INPUT</c>.
/// </summary>
public sealed class DeviceSyncVerifyTests
{
    private const int AacFrame = 1024;
    private const int SettleSeconds = 5;

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void BothStreamsStayLockedToQpcAndToEachOther()
    {
        var seconds = Environment.GetEnvironmentVariable("REPLAYBUFFER_LONG_TESTS") == "1" ? 600 : 60;
        var output = Resolve(AudioFlow.Output, Environment.GetEnvironmentVariable("REPLAYBUFFER_TEST_OUTPUT") ?? "Game (BEACN Mix Create)");
        var input = Resolve(AudioFlow.Input, Environment.GetEnvironmentVariable("REPLAYBUFFER_TEST_INPUT") ?? "BEACN Mic");
        var qpf = Stopwatch.Frequency;
        var token = TestContext.Current.CancellationToken;

        using var hub = new AudioDeviceHub(NullLoggerFactory.Instance);
        var bursts = new BurstDetector();
        var mic = new CountingSubscriber();
        var outAge = new DeliveryAgeTap(bursts);
        var micAge = new DeliveryAgeTap(mic);
        using var loopback = hub.SubscribeDevice(output.Id, SettingsFlow.Output, outAge);
        using var capture = hub.SubscribeDevice(input.Id, SettingsFlow.Input, micAge);
        using var player = new TonePlayer(output.Id);
        player.Start();

        var outErrors = new List<double>();
        var micErrors = new List<double>();
        var outLags = new List<double>();
        var micLags = new List<double>();
        var start = Stopwatch.StartNew();
        for (var second = 1; second <= seconds; second++)
        {
            var wait = TimeSpan.FromSeconds(second) - start.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                token.WaitHandle.WaitOne(wait);
            }

            token.ThrowIfCancellationRequested();

            // Independent of the device stamps: each stream's frames against wall-clock QPC, the
            // smallest delivery age over the last second.
            var outLag = outAge.TakeMinAgeSamples();
            var micLag = micAge.TakeMinAgeSamples();
            if (second > SettleSeconds)
            {
                outErrors.Add(loopback.Diagnostics.Stream.LastErrorSamples);
                micErrors.Add(capture.Diagnostics.Stream.LastErrorSamples);
                Assert.True(outLag is not null && micLag is not null, $"a stream delivered nothing in second {second}");
                outLags.Add(outLag.Value);
                micLags.Add(micLag.Value);
            }
        }

        player.Stop();
        Assert.Null(player.Fault);
        Assert.Null(loopback.DeviceFault);
        Assert.Null(capture.DeviceFault);
        Assert.Null(loopback.SubscriberFault);
        Assert.Null(capture.SubscriberFault);

        // Match each played burst to the detected onset nearest it.
        var offsets = new List<double>();
        var onsets = Enumerable.Range(0, bursts.OnsetCount).Select(bursts.Onset).ToList();
        for (var i = 0; i < player.BurstCount; i++)
        {
            var played = player.BurstQpc(i);
            var nearest = onsets.Count == 0 ? long.MaxValue : onsets.MinBy(o => Math.Abs(o - played));
            if (Math.Abs(nearest - played) < qpf / 2)
            {
                offsets.Add((double)(nearest - played) * AudioFormat.SampleRate / qpf);
            }
        }

        var differences = outErrors.Zip(micErrors, (o, m) => o - m).ToList();
        var lagDifferences = outLags.Zip(micLags, (o, m) => o - m).ToList();
        var report = new
        {
            created = DateTimeOffset.Now,
            seconds,
            output = DeviceReport(loopback.Diagnostics, outErrors),
            input = DeviceReport(capture.Diagnostics, micErrors),
            maxAbsOutMinusMicSamples = differences.Count == 0 ? 0 : differences.Max(Math.Abs),
            wallClock = new
            {
                outputLagMinSamples = outLags.Min(),
                outputLagSpreadSamples = Spread(outLags),
                inputLagMinSamples = micLags.Min(),
                inputLagSpreadSamples = Spread(micLags),
                outMinusMicLagSpreadSamples = Spread(lagDifferences),
            },
            bursts = new
            {
                played = player.BurstCount,
                detected = onsets.Count,
                matched = offsets.Count,
                meanOffsetMs = offsets.Count == 0 ? 0 : offsets.Average() / 48,
                spreadSamples = offsets.Count == 0 ? 0 : offsets.Max() - offsets.Min(),
                firstOffsetSamples = offsets.FirstOrDefault(),
                lastOffsetSamples = offsets.LastOrDefault(),
            },
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var folder = Path.Combine(Path.GetTempPath(), "ReplayBuffer");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"device-sync-{seconds}s.json");
        File.WriteAllText(path, json);
        TestContext.Current.TestOutputHelper?.WriteLine($"Report: {path}{Environment.NewLine}{json}");

        Assert.True(player.BurstCount >= seconds - 3, $"only {player.BurstCount} bursts played");
        Assert.True(offsets.Count >= player.BurstCount - 2, $"only {offsets.Count} of {player.BurstCount} bursts were found in the loopback; see {path}");
        Assert.True(offsets.Max() - offsets.Min() < AacFrame, $"burst offsets spread {offsets.Max() - offsets.Min():0} samples; see {path}");
        Assert.All(outErrors, e => Assert.InRange(Math.Abs(e), 0, AacFrame - 1));
        Assert.All(micErrors, e => Assert.InRange(Math.Abs(e), 0, AacFrame - 1));
        Assert.All(differences, d => Assert.InRange(Math.Abs(d), 0, AacFrame - 1));
        Assert.True(Spread(outLags) < AacFrame, $"the loopback's lag behind QPC spread {Spread(outLags):0} samples; see {path}");
        Assert.True(Spread(micLags) < AacFrame, $"the mic's lag behind QPC spread {Spread(micLags):0} samples; see {path}");
        Assert.True(Spread(lagDifferences) < AacFrame, $"the out − mic lag spread {Spread(lagDifferences):0} samples; see {path}");
        Assert.True(mic.Frames > (seconds - 1) * AudioFormat.SampleRate, "the mic stream fell behind");
    }

    private static double Spread(List<double> values) => values.Max() - values.Min();

    private static object DeviceReport(AudioDeviceDiagnostics diagnostics, List<double> perSecond)
    {
        var stream = diagnostics.Stream;
        var stamps = diagnostics.Stamps;
        return new
        {
            device = diagnostics.Endpoint.FriendlyName,
            rawDriftPpm = stamps.DriftPpm,
            driftSegmentSeconds = stamps.DriftSegmentSeconds,
            jitterP99Ms = stamps.Jitter.Percentile(99),
            ratioPpm = stream.RatioPpm,
            meanRatioPpm = stream.MeanRatioPpm,
            errorP99Samples = stream.SteeringError.Percentile(99) * 48,
            errorMaxSamples = stream.MaxSettledErrorSamples,
            perSecondMaxAbsErrorSamples = perSecond.Count == 0 ? 0 : perSecond.Max(Math.Abs),
            stepCorrections = stream.StepCorrections,
            gapFills = stream.GapFills,
            trims = stream.Trims,
            idleFillFrames = stream.IdleFillFrames,
            resolvedStamps = stream.ResolvedStamps,
            packets = stamps.Packets,
            outputFrames = stream.OutputFrames,
        };
    }

    private static AudioEndpoint Resolve(AudioFlow flow, string query)
    {
        var endpoints = AudioEndpoints.List(flow);
        var match = AudioEndpoints.Match(endpoints, query);
        Assert.True(
            match.Outcome == EndpointMatchOutcome.Found,
            $"{match.Outcome}: no single {flow} device matches '{query}'. Endpoints:{Environment.NewLine}"
                + string.Join(Environment.NewLine, match.Candidates.Select(e => $"  {e.FriendlyName}  [{e.Id}]")));
        return match.Endpoint!;
    }
}
