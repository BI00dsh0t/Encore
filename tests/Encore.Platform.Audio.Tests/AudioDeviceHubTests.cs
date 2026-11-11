using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Core.Audio;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Platform.Audio.Tests;

/// <summary>Records each block's stamp, length and a checksum of its samples, without allocating.</summary>
internal sealed class RecordingSubscriber(bool throwOnFirst = false) : IAudioPcmSubscriber
{
    private const int Capacity = 20_000;
    private readonly long[] _starts = new long[Capacity];
    private readonly int[] _frames = new int[Capacity];
    private readonly double[] _sums = new double[Capacity];
    private int _count;
    private int _calls;

    public int Count => Volatile.Read(ref _count);

    public int Calls => Volatile.Read(ref _calls);

    public void OnPcm(in AudioBlock block)
    {
        Interlocked.Increment(ref _calls);
        if (throwOnFirst)
        {
            throw new InvalidOperationException("test subscriber");
        }

        if (_count >= Capacity)
        {
            return;
        }

        double sum = 0;
        foreach (var sample in block.Interleaved)
        {
            sum += sample;
        }

        _starts[_count] = block.StartQpc;
        _frames[_count] = block.Frames;
        _sums[_count] = block.IsSilent ? double.NaN : sum;
        Volatile.Write(ref _count, _count + 1);
    }

    public (long Start, int Frames, double Sum) this[int index] => (_starts[index], _frames[index], _sums[index]);

    public bool WaitFor(int blocks, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (Count < blocks)
        {
            if (clock.Elapsed > timeout)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }
}

/// <summary>The hub on this PC's default devices: sharing, closing, faults and contiguity.</summary>
public sealed class AudioDeviceHubTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly AudioDeviceHub _hub = new(NullLoggerFactory.Instance);

    public void Dispose() => _hub.Dispose();

    private static string DefaultInput => AudioEndpoints.Default(AudioFlow.Input).Id;

    private static string DefaultOutput => AudioEndpoints.Default(AudioFlow.Output).Id;

    [Fact]
    public void TwoSubscribersShareOneClientAndGetIdenticalBlocks()
    {
        AudioDevices.Require();
        var first = new RecordingSubscriber();
        var second = new RecordingSubscriber();
        using (var a = _hub.Subscribe(DefaultInput, SettingsFlow.Input, first))
        {
            Assert.True(first.WaitFor(10, Timeout), "no blocks from the default input");
            using (var b = _hub.Subscribe(DefaultInput, SettingsFlow.Input, second))
            {
                Assert.Equal(1, _hub.OpenDeviceCount);
                Assert.True(second.WaitFor(50, Timeout), "the second subscriber got no blocks");
            }

            Assert.Equal(1, _hub.OpenDeviceCount);
        }

        Assert.Equal(0, _hub.OpenDeviceCount);

        // Every block the second saw is the first's block with the same stamp, sample for sample.
        var index = Enumerable.Range(0, first.Count).ToDictionary(i => first[i].Start);
        for (var i = 0; i < second.Count; i++)
        {
            Assert.True(index.TryGetValue(second[i].Start, out var j), $"block at {second[i].Start} reached only the second subscriber");
            Assert.Equal(first[j].Frames, second[i].Frames);
            Assert.Equal(first[j].Sum, second[i].Sum);
        }

        AssertContiguous(first);
    }

    [Fact]
    public void TheLastUnsubscribeClosesTheDeviceAndTheNextReopensIt()
    {
        AudioDevices.Require();
        var first = new RecordingSubscriber();
        var subscription = _hub.Subscribe(DefaultOutput, SettingsFlow.Output, first);
        Assert.True(first.WaitFor(5, Timeout), "no blocks from the default output's loopback");
        subscription.Dispose();
        Assert.Equal(0, _hub.OpenDeviceCount);

        var calls = first.Calls;
        Thread.Sleep(200);
        Assert.Equal(calls, first.Calls);

        var again = new RecordingSubscriber();
        using (_hub.Subscribe(DefaultOutput, SettingsFlow.Output, again))
        {
            Assert.Equal(1, _hub.OpenDeviceCount);
            Assert.True(again.WaitFor(5, Timeout), "the reopened device sent no blocks");
        }

        Assert.Equal(0, _hub.OpenDeviceCount);
        AssertContiguous(again);
    }

    [Fact]
    public void AThrowingSubscriberIsCutOffAndTheOtherContinues()
    {
        AudioDevices.Require();
        var thrower = new RecordingSubscriber(throwOnFirst: true);
        var steady = new RecordingSubscriber();
        using var a = _hub.Subscribe(DefaultInput, SettingsFlow.Input, thrower);
        using var b = _hub.Subscribe(DefaultInput, SettingsFlow.Input, steady);
        Assert.True(steady.WaitFor(50, Timeout), "the other subscriber stopped");

        Assert.Equal(1, thrower.Calls);
        Assert.IsType<InvalidOperationException>(a.SubscriberFault);
        Assert.Null(b.SubscriberFault);
        Assert.Null(a.DeviceFault);
        AssertContiguous(steady);
    }

    [Fact]
    public void ABadEndpointFailsTheSubscribe()
    {
        var failure = Assert.Throws<ErrorCodeException>(() => _hub.Subscribe("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", SettingsFlow.Output, new RecordingSubscriber()));
        Assert.Equal(ErrorCodes.AudioDeviceFailed, failure.Code);
        Assert.Equal(0, _hub.OpenDeviceCount);
    }

    [Fact]
    public void TheDiagnosticsShowTheStreamTrackingQpc()
    {
        AudioDevices.Require();
        var recorder = new RecordingSubscriber();
        using var subscription = _hub.SubscribeDevice(DefaultInput, SettingsFlow.Input, recorder);
        Assert.True(recorder.WaitFor(100, Timeout));
        var stream = subscription.Diagnostics.Stream;
        Assert.True(stream.Started);
        Assert.True(subscription.Diagnostics.Stamps.Packets > 0);

        // The output trails now by about the packet latency, nowhere near a second.
        var lag = stream.OutputIndexAt(Stopwatch.GetTimestamp()) - stream.OutputFrames;
        Assert.InRange(lag, 0, AudioFormat.SampleRate / 5);
    }

    private static void AssertContiguous(RecordingSubscriber recorder)
    {
        var qpf = Stopwatch.Frequency;
        var origin = recorder[0].Start;
        long frames = 0;
        for (var i = 0; i < recorder.Count; i++)
        {
            // Every block starts on the grid where the previous ended; the first block may start past the origin.
            var expected = origin + Media.QpcClock.ToUnits(frames, AudioFormat.SampleRate, qpf);
            Assert.InRange(recorder[i].Start - expected, -1, 1);
            frames += recorder[i].Frames;
        }
    }
}
