using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Core.Audio;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Platform.Audio.Tests;

/// <summary>Records each block's stamp, length and energy (sum of squares), without allocating.</summary>
internal sealed class EnergySubscriber : IAudioPcmSubscriber
{
    private const int Capacity = 50_000;
    private readonly long[] _starts = new long[Capacity];
    private readonly int[] _frames = new int[Capacity];
    private readonly double[] _energy = new double[Capacity];
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void OnPcm(in AudioBlock block)
    {
        if (_count >= Capacity)
        {
            return;
        }

        double energy = 0;
        foreach (var sample in block.Interleaved)
        {
            energy += sample * sample;
        }

        _starts[_count] = block.StartQpc;
        _frames[_count] = block.Frames;
        _energy[_count] = energy;
        Volatile.Write(ref _count, _count + 1);
    }

    public (long Start, int Frames, double Energy) this[int index] => (_starts[index], _frames[index], _energy[index]);

    /// <summary>The RMS of each block starting at or after <paramref name="fromQpc"/> and before <paramref name="toQpc"/>.</summary>
    public IEnumerable<double> RmsBetween(long fromQpc, long toQpc)
    {
        var count = Count;
        for (var i = 0; i < count; i++)
        {
            if (_starts[i] >= fromQpc && _starts[i] < toQpc)
            {
                yield return Math.Sqrt(_energy[i] / (_frames[i] * AudioFormat.Channels));
            }
        }
    }

    /// <summary>The RMS over every block starting in [<paramref name="fromQpc"/>, <paramref name="toQpc"/>).</summary>
    public double Rms(long fromQpc, long toQpc)
    {
        double energy = 0;
        long samples = 0;
        var count = Count;
        for (var i = 0; i < count; i++)
        {
            if (_starts[i] >= fromQpc && _starts[i] < toQpc)
            {
                energy += _energy[i];
                samples += _frames[i] * AudioFormat.Channels;
            }
        }

        Assert.True(samples > 0, "no blocks in the span");
        return Math.Sqrt(energy / samples);
    }

    public bool WaitFor(int blocks, TimeSpan timeout) => Wait.Until(() => Count >= blocks, timeout);
}

internal static class Wait
{
    public static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
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

/// <summary>
/// The hub's dropout and rebind (R5) over fake clients and a fake endpoint list: nothing here touches
/// a Windows device. Device A plays 440 Hz, B 1 kHz.
/// </summary>
public sealed class DeviceDropoutTests : IDisposable
{
    private const string A = "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-000000000001}";
    private const string B = "{0.0.0.00000000}.{bbbbbbbb-0000-0000-0000-000000000002}";
    private const string ANew = "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-000000000003}";
    private const string AOther = "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-000000000004}";
    private const double Amplitude = 0.2;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DropWithin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResumeWithin = TimeSpan.FromSeconds(2);
    private static readonly long Qpf = Stopwatch.Frequency;

    private readonly FakeEndpointList _source = new();
    private readonly FakeDeviceBank _bank = new();
    private readonly AudioDeviceHub _hub;
    private readonly ConcurrentQueue<(AudioDeviceDropout Dropout, long Qpc)> _dropped = new();
    private readonly ConcurrentQueue<(AudioDeviceDropout Dropout, long Qpc)> _resumed = new();

    public DeviceDropoutTests()
    {
        _bank.Define(A, "Game", "BEACN", AudioFlow.Output, Amplitude, 440);
        _bank.Define(B, "Chat", "BEACN", AudioFlow.Output, Amplitude, 1000);
        _bank.Define(ANew, "Game", "BEACN", AudioFlow.Output, Amplitude, 440);
        _bank.Define(AOther, "Game", "BEACN", AudioFlow.Output, Amplitude, 440);
        _source.Add(A, "Game", "BEACN", SettingsFlow.Output, raise: false);
        _source.Add(B, "Chat", "BEACN", SettingsFlow.Output, raise: false);
        _hub = new AudioDeviceHub(NullLoggerFactory.Instance, _bank.Create, _source);
        _hub.Dropped += d => _dropped.Enqueue((d, Stopwatch.GetTimestamp()));
        _hub.Resumed += d => _resumed.Enqueue((d, Stopwatch.GetTimestamp()));
    }

    public void Dispose() => _hub.Dispose();

    [Fact]
    public void ARemovedDeviceDropsWithinASecondAndItsStreamContinuesAsSilence()
    {
        var recorder = new EnergySubscriber();
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, recorder);
        Assert.True(recorder.WaitFor(20, Timeout), "no blocks from A");

        var removed = Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin), "A wasn't dropped within 1 s");
        var (dropout, _) = Assert.Single(_dropped);
        Assert.Equal(new AudioDeviceDropout(A, SettingsFlow.Output, "Game", "BEACN"), dropout);
        Assert.Equal(1, _hub.DroppedDeviceCount);
        Assert.Null(subscription.DeviceFault);

        // The hold keeps blocks coming, and they are silent.
        var count = recorder.Count;
        Thread.Sleep(400);
        Assert.True(recorder.Count > count, "the stream stopped while A was out");
        Assert.All(recorder.RmsBetween(removed + Ms(50), Stopwatch.GetTimestamp() - Ms(100)), rms => Assert.InRange(rms, 0, 1e-6));
        Assert.Empty(_resumed);
    }

    [Fact]
    public void AnInvalidatedDeviceDropsWithoutAFaultAndResumesWhenItOpensAgain()
    {
        var recorder = new EnergySubscriber();
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, recorder);
        Assert.True(recorder.WaitFor(20, Timeout), "no blocks from A");

        // Still listed, but the device is gone for a moment: it can't reopen yet.
        _bank.SetOpenable(A, false);
        _bank.Latest(A).Invalidate();
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin), "the invalidated device wasn't dropped");
        Assert.Null(subscription.DeviceFault);
        Assert.True(_bank.Latest(A).Disposed);

        _bank.SetOpenable(A, true);
        Assert.True(Wait.Until(() => _resumed.Count == 1, ResumeWithin), "A didn't resume once it opened");
        Assert.Equal(2, _bank.Opens(A));
        Assert.Equal(0, _hub.DroppedDeviceCount);
        Assert.Single(_dropped);
    }

    [Fact]
    public void ADeviceReturningWithItsIdResumesOnTheSameStreamWithinTwoSeconds()
    {
        var recorder = new EnergySubscriber();
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, recorder);
        Assert.True(recorder.WaitFor(20, Timeout), "no blocks from A");
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));

        var returned = Replug(A, "Game");
        Assert.True(Wait.Until(() => _resumed.Count == 1, ResumeWithin), "A didn't resume within 2 s");
        var (resumed, at) = Assert.Single(_resumed);
        Assert.Equal(new AudioDeviceDropout(A, SettingsFlow.Output, "Game", "BEACN"), resumed);
        Assert.InRange(at - returned, 0, 2 * Qpf);
        Assert.Equal(2, _bank.Opens(A));
        Assert.Equal(1, _hub.OpenDeviceCount);
        Assert.Null(subscription.DeviceFault);

        // The old subscription carries the new client's tone.
        Thread.Sleep(300);
        AssertTone(recorder.Rms(at + Ms(50), Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void ADeviceReturningWithANewIdIsReboundByName()
    {
        var recorder = new EnergySubscriber();
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, recorder);
        Assert.True(recorder.WaitFor(20, Timeout));
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));

        Replug(ANew, "Game");
        Assert.True(Wait.Until(() => _resumed.Count == 1, ResumeWithin), "A wasn't rebound to its new id");
        Assert.Equal(new AudioDeviceDropout(ANew, SettingsFlow.Output, "Game", "BEACN"), Assert.Single(_resumed).Dropout);
        Assert.Equal(ANew, subscription.Diagnostics.Endpoint.Id);

        // A subscriber to the new id shares the rebound client (AD-20).
        using (_hub.SubscribeDevice(ANew, SettingsFlow.Output, new EnergySubscriber()))
        {
            Assert.Equal(1, _hub.OpenDeviceCount);
            Assert.Equal(1, _bank.Opens(ANew));
        }
    }

    [Fact]
    public void AnAmbiguousReturnStaysDropped()
    {
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, new EnergySubscriber());
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));

        _source.Add(ANew, "Game", "BEACN", SettingsFlow.Output, raise: false);
        _source.Add(AOther, "Game", "BEACN", SettingsFlow.Output);
        Thread.Sleep(1200);

        Assert.Empty(_resumed);
        Assert.Equal(1, _hub.DroppedDeviceCount);
        Assert.Equal(0, _bank.Attempts(ANew) + _bank.Attempts(AOther));
    }

    [Fact]
    public void ANameMatchHeldByAnotherOpenDeviceIsNotTaken()
    {
        // B is renamed to A's names, so it is A's only name match, but B is open.
        RenameB("Game", "BEACN");
        using var a = _hub.SubscribeDevice(A, SettingsFlow.Output, new EnergySubscriber());
        using var b = _hub.SubscribeDevice(B, SettingsFlow.Output, new EnergySubscriber());
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));
        Thread.Sleep(1200);

        Assert.Empty(_resumed);
        Assert.Equal(1, _hub.DroppedDeviceCount);
        Assert.Equal(1, _bank.Attempts(B));
    }

    [Fact]
    public void AReturnThatCantOpenIsRetriedUntilItOpens()
    {
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, new EnergySubscriber());
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));

        // Listed again, but still refusing to open.
        _source.Add(A, "Game", "BEACN", SettingsFlow.Output);
        var attempts = _bank.Attempts(A);
        Assert.True(Wait.Until(() => _bank.Attempts(A) >= attempts + 3, TimeSpan.FromSeconds(3)), "the open wasn't retried every 500 ms");
        Assert.Empty(_resumed);

        _bank.SetOpenable(A, true);
        Assert.True(Wait.Until(() => _resumed.Count == 1, TimeSpan.FromSeconds(1)), "A didn't resume once it opened");
    }

    [Fact]
    public void AFailedListingDropsNothingAndIsRetried()
    {
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, new EnergySubscriber());
        _source.Failing = true;
        Unplug(A);
        Thread.Sleep(800);
        Assert.Empty(_dropped);

        // The next round, 500 ms on, lists again with no further change raised.
        _source.Failing = false;
        Assert.True(Wait.Until(() => _dropped.Count == 1, TimeSpan.FromSeconds(1.5)), "A wasn't dropped after the listing recovered");
    }

    [Fact]
    public void TheLastSubscriberLeavingWhileDroppedClosesTheDeviceAndStopsRetries()
    {
        var recorder = new EnergySubscriber();
        var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, recorder);
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));
        _source.Add(A, "Game", "BEACN", SettingsFlow.Output);
        Thread.Sleep(600);

        subscription.Dispose();
        Assert.Equal(0, _hub.OpenDeviceCount);
        var attempts = _bank.Attempts(A);
        var count = recorder.Count;
        Thread.Sleep(1200);
        Assert.Equal(attempts, _bank.Attempts(A));
        Assert.Equal(count, recorder.Count);
    }

    [Fact]
    public void ADropoutSilencesOnlyItsOwnDevice()
    {
        var a = new EnergySubscriber();
        var b = new EnergySubscriber();
        using var subA = _hub.SubscribeDevice(A, SettingsFlow.Output, a);
        using var subB = _hub.SubscribeDevice(B, SettingsFlow.Output, b);
        Assert.True(b.WaitFor(20, Timeout));
        var removed = Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));
        Thread.Sleep(1000);
        var returned = Replug(A, "Game");
        Assert.True(Wait.Until(() => _resumed.Count == 1, ResumeWithin));
        Thread.Sleep(300);

        // B's own client never changed, and every block it sent (after its first) carries its tone.
        Assert.Equal(1, _bank.Opens(B));
        var first = b[0].Start;
        AssertTone(b.Rms(first + Ms(20), removed));
        AssertTone(b.Rms(removed, returned));
        AssertTone(b.Rms(returned, Stopwatch.GetTimestamp()));
        AssertContiguous(b);
        Assert.All(a.RmsBetween(removed + Ms(50), returned - Ms(50)), rms => Assert.InRange(rms, 0, 1e-6));
    }

    [Fact]
    public void ARepeatedDropoutIsReportedEachTime()
    {
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, new EnergySubscriber());
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));
        Replug(A, "Game");
        Assert.True(Wait.Until(() => _resumed.Count == 1, ResumeWithin));

        // Past the window in which a re-drop counts as the same dropout.
        Thread.Sleep(AudioDeviceHub.SameDropoutWindow + TimeSpan.FromMilliseconds(300));
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 2, DropWithin), "the second dropout wasn't reported");
        Replug(A, "Game");
        Assert.True(Wait.Until(() => _resumed.Count == 2, ResumeWithin));
        Assert.Equal(2, _dropped.Count);
        Assert.Equal(3, _bank.Opens(A));
    }

    [Fact]
    public void ARedropJustAfterAResumeIsTheSameDropout()
    {
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, new EnergySubscriber());
        Unplug(A);
        Assert.True(Wait.Until(() => _dropped.Count == 1, DropWithin));

        // The first client after the return is invalidated as it starts; the next one holds.
        _bank.InvalidateNextOnStart(A);
        Replug(A, "Game");
        Assert.True(Wait.Until(() => _bank.Opens(A) == 3 && _hub.DroppedDeviceCount == 0, TimeSpan.FromSeconds(3)), "A didn't end bound");
        Thread.Sleep(600);

        Assert.Single(_dropped);
        Assert.Single(_resumed);
        Assert.Equal(0, _hub.DroppedDeviceCount);
        Assert.Equal(3, _bank.Opens(A));
    }

    [Fact]
    public void AFaultStaysReadableAfterTheLastSubscriberLeaves()
    {
        var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, new EnergySubscriber());
        var fault = new InvalidOperationException("test device fault");
        _bank.Latest(A).Fault = fault;

        subscription.Dispose();

        Assert.Equal(0, _hub.OpenDeviceCount);
        Assert.Same(fault, subscription.DeviceFault);
    }

    [Fact]
    public void AcrossATwoSecondDropoutTheStreamTracksWallTimeAndTheDropoutIsSilent()
    {
        var recorder = new EnergySubscriber();
        using var subscription = _hub.SubscribeDevice(A, SettingsFlow.Output, recorder);
        Assert.True(recorder.WaitFor(20, Timeout));
        Thread.Sleep(1000);
        var removed = Unplug(A);
        Thread.Sleep(2000);
        var returned = Replug(A, "Game");
        Assert.True(Wait.Until(() => _resumed.Count == 1, ResumeWithin));
        Thread.Sleep(1500);

        // Contiguous from the first block, and the frames sent track wall time to within 0.1 s.
        AssertContiguous(recorder);
        var stream = subscription.Diagnostics.Stream;
        var behind = stream.OutputIndexAt(Stopwatch.GetTimestamp()) - stream.OutputFrames;
        Assert.InRange(behind, 0, AudioFormat.SampleRate / 10);

        // Tone before, silence across the dropout, tone again once resumed.
        var resumedAt = Assert.Single(_resumed).Qpc;
        AssertTone(recorder.Rms(recorder[0].Start + Ms(20), removed - Ms(50)));
        Assert.All(recorder.RmsBetween(removed + Ms(50), returned - Ms(50)), rms => Assert.InRange(rms, 0, 1e-6));
        AssertTone(recorder.Rms(resumedAt + Ms(100), Stopwatch.GetTimestamp() - Ms(100)));
    }

    /// <summary>A sine of the test's amplitude has RMS a/√2.</summary>
    private static void AssertTone(double rms) => Assert.InRange(rms, Amplitude / Math.Sqrt(2) * 0.9, Amplitude / Math.Sqrt(2) * 1.1);

    private static long Ms(int milliseconds) => milliseconds * Qpf / 1000;

    private static void AssertContiguous(EnergySubscriber recorder)
    {
        var origin = recorder[0].Start;
        long frames = 0;
        for (var i = 0; i < recorder.Count; i++)
        {
            var expected = origin + Media.QpcClock.ToUnits(frames, AudioFormat.SampleRate, Qpf);
            Assert.InRange(recorder[i].Start - expected, -1, 1);
            frames += recorder[i].Frames;
        }
    }

    /// <summary>The device vanishes: its client goes quiet, it can't open, and it leaves the list. Returns the QPC.</summary>
    private long Unplug(string id)
    {
        var at = Stopwatch.GetTimestamp();
        _bank.Unplug(id);
        _source.Remove(id);
        return at;
    }

    /// <summary>The device is back under <paramref name="id"/>: it opens, and it is listed. Returns the QPC.</summary>
    private long Replug(string id, string name)
    {
        _bank.SetOpenable(id, true);
        var at = Stopwatch.GetTimestamp();
        _source.Add(id, name, "BEACN", SettingsFlow.Output);
        return at;
    }

    /// <summary>Lists and defines B under other names, before anything subscribes.</summary>
    private void RenameB(string name, string parent)
    {
        _source.Remove(B, raise: false);
        _source.Add(B, name, parent, SettingsFlow.Output, raise: false);
        _bank.Define(B, name, parent, AudioFlow.Output, Amplitude, 1000);
    }
}
