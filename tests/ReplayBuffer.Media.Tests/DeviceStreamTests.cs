using ReplayBuffer.Core.Audio;

namespace ReplayBuffer.Media.Tests;

/// <summary>Checks that blocks are contiguous on the stream's grid, and counts them.</summary>
internal sealed class BlockSink(long qpcFrequency) : IAudioPcmSubscriber
{
    public DeviceStream? Stream { get; set; }

    public long Frames { get; private set; }

    public long SilentFrames { get; private set; }

    public bool LastWasSilent { get; private set; }

    public void OnPcm(in AudioBlock block)
    {
        var expected = Stream!.OriginQpc + QpcClock.ToUnits(Frames, AudioFormat.SampleRate, qpcFrequency);
        Assert.Equal(expected, block.StartQpc);
        Assert.True(block.IsSilent || block.Interleaved.Length == block.Frames * 2);
        Frames += block.Frames;
        LastWasSilent = block.IsSilent;
        if (block.IsSilent)
        {
            SilentFrames += block.Frames;
        }
    }
}

/// <summary>
/// A device with its own clock: frame n is captured at <c>t0 + n / 48 000 × (1 + ppm × 10⁻⁶)</c>
/// seconds of QPC, delivered in 10 ms packets whose stamps carry uniform jitter.
/// </summary>
internal sealed class SyntheticDevice
{
    public const long Qpf = 10_000_000;
    public const int PacketFrames = 480;
    private const long T0 = 50_000_000_000;

    private readonly double _ppm;
    private readonly double _jitterTicks;
    private readonly Random _random = new(1234);
    private readonly float[] _packet = new float[PacketFrames * 8];

    public SyntheticDevice(double ppm = 0, double jitterMs = 0)
    {
        _ppm = ppm;
        _jitterTicks = jitterMs * Qpf / 1000;
        Sink = new BlockSink(Qpf);
        Stream = new DeviceStream(Qpf, Sink);
        Sink.Stream = Stream;
        for (var i = 0; i < 4 * PacketFrames; i++)
        {
            _packet[2 * i] = _packet[(2 * i) + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 440 * i / 48_000));
        }
    }

    public DeviceStream Stream { get; }

    public BlockSink Sink { get; }

    /// <summary>The device frame of the next packet.</summary>
    public long Frame { get; set; }

    public long TrueQpc(long frame) => T0 + (long)Math.Round(frame / 48_000.0 * (1 + (_ppm * 1e-6)) * Qpf);

    /// <summary>
    /// Delivers the next packet and returns the true placement error of its first frame, in samples:
    /// where it comes out, on the stream's QPC grid, minus when it was captured.
    /// </summary>
    public double Next(AudioPacketFlags flags = AudioPacketFlags.None, long? stamp = null, int frames = PacketFrames)
    {
        var trueQpc = TrueQpc(Frame);
        var jitter = _jitterTicks == 0 ? 0 : (long)((_random.NextDouble() * 2 - 1) * _jitterTicks);
        var landsAt = Stream.Started ? Stream.OutputIndexOf(Stream.InputFrames) : 0;
        Stream.Push(stamp ?? trueQpc + jitter, frames, _packet, flags);
        Frame += frames;
        if (!Stream.Started)
        {
            return double.NaN;
        }

        // Exact placements move the landing point: measure them after the push.
        if ((flags & AudioPacketFlags.DataDiscontinuity) != 0)
        {
            landsAt = Stream.OutputIndexOf(Stream.InputFrames - frames);
        }

        return landsAt - Stream.OutputIndexAt(trueQpc);
    }

    /// <summary>The idle fill the hub runs on a wait timeout, 50 ms behind <paramref name="nowQpc"/>.</summary>
    public void Idle(long nowQpc) => Stream.FillTo(nowQpc - (50 * Qpf / 1000));
}

public sealed class DeviceStreamTests
{
    private const int PacketsPerSecond = 100;

    [Theory]
    [InlineData(54)]
    [InlineData(-54)]
    public void SteeringLocksADriftingDeviceToQpc(double ppm)
    {
        var device = new SyntheticDevice(ppm, jitterMs: 0.15);
        var worst = 0.0;
        for (var i = 0; i < 30 * PacketsPerSecond; i++)
        {
            var error = device.Next();
            if (i >= 5 * PacketsPerSecond)
            {
                worst = Math.Max(worst, Math.Abs(error));
            }
        }

        Assert.True(worst <= 2, $"true error reached {worst:0.00} samples after the settle");
        Assert.Equal(0, device.Stream.StepCorrections);
        Assert.Equal(0, device.Stream.GapFills);
        Assert.Equal(0, device.Stream.Trims);

        // A slow device (positive drift) is consumed slower than 48 kHz.
        Assert.InRange(device.Stream.MeanRatioPpm, -ppm - 5, -ppm + 5);
        Assert.InRange(device.Stream.MaxSettledErrorSamples, 0, 20);

        // The output count tracks elapsed QPC: all but the kernel's look-ahead is out.
        var end = device.Stream.OutputIndexAt(device.TrueQpc(device.Frame));
        Assert.InRange(device.Sink.Frames + AdaptiveResampler.HalfTaps - end, -2, 2);
    }

    [Fact]
    public void AnIdleLoopbackIsFilledWithSilenceAndResumesExactly()
    {
        var device = new SyntheticDevice();
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            device.Next();
        }

        // 2 s without packets: the hub fills 50 ms behind now on every 20 ms wait timeout.
        var idleStart = device.Frame;
        for (var ms = 20; ms <= 2000; ms += 20)
        {
            var now = device.TrueQpc(idleStart + (ms * 48));
            device.Idle(now);
            var behind = device.Stream.OutputIndexAt(now - (50 * SyntheticDevice.Qpf / 1000)) - device.Sink.Frames;
            Assert.True(behind <= AdaptiveResampler.HalfTaps + 2, $"the stream is {behind:0} frames behind the fill target");
        }

        Assert.True(device.Sink.LastWasSilent);
        Assert.True(device.Stream.IdleFillFrames > 90_000);

        // Packets come back where the device's clock says.
        device.Frame = idleStart + (2000 * 48);
        device.Next();
        Assert.InRange(device.Stream.LastErrorSamples, -1, 1);
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            Assert.InRange(device.Next(), -1, 1);
        }

        Assert.Equal(0, device.Stream.StepCorrections);
    }

    [Fact]
    public void AFlaggedGapIsFilledWithSilenceAndKeepsTheRate()
    {
        var device = new SyntheticDevice(ppm: 54);
        for (var i = 0; i < 10 * PacketsPerSecond; i++)
        {
            device.Next();
        }

        var ratio = device.Stream.RatioPpm;
        var before = device.Stream.InputFrames;
        device.Frame += 3 * SyntheticDevice.PacketFrames; // 30 ms missing
        var error = device.Next(AudioPacketFlags.DataDiscontinuity);

        Assert.InRange(error, -1, 1);
        Assert.Equal(ratio, device.Stream.RatioPpm);
        Assert.Equal(1, device.Stream.GapFills);
        Assert.InRange(device.Stream.GapFillFrames, 1438, 1442);
        Assert.InRange(device.Stream.InputFrames - before - SyntheticDevice.PacketFrames, 1438, 1442);
        Assert.Equal(0, device.Stream.StepCorrections);
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            Assert.InRange(device.Next(), -1.5, 1.5);
        }
    }

    [Fact]
    public void AnUnflaggedGapIsOneStepCorrection()
    {
        var device = new SyntheticDevice();
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            device.Next();
        }

        device.Frame += 2 * SyntheticDevice.PacketFrames; // the stamp is 20 ms after the prediction
        device.Next();
        Assert.Equal(1, device.Stream.StepCorrections);
        Assert.Equal(1, device.Stream.GapFills);
        Assert.InRange(device.Stream.GapFillFrames, 959, 961);
        Assert.InRange(device.Stream.LastErrorSamples, -1, 1);
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            Assert.InRange(device.Next(), -1, 1);
        }

        Assert.Equal(1, device.Stream.StepCorrections);
    }

    [Fact]
    public void AnOverlapIsTrimmed()
    {
        var device = new SyntheticDevice();
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            device.Next();
        }

        // A 20 ms packet, then one stamped 10 ms into it: 10 ms before the prediction, still after the last stamp.
        device.Next(frames: 2 * SyntheticDevice.PacketFrames);
        device.Frame -= SyntheticDevice.PacketFrames;
        device.Next();
        Assert.Equal(1, device.Stream.StepCorrections);
        Assert.Equal(1, device.Stream.Trims);
        Assert.InRange(device.Stream.TrimmedFrames, 479, 481);
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            Assert.InRange(device.Next(), -1, 1);
        }
    }

    [Fact]
    public void AnOverlapLongerThanAPacketIsOneCorrection()
    {
        var device = new SyntheticDevice();
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            device.Next();
        }

        // A 30 ms packet, then 10 ms packets stamped from 10 ms into it: 20 ms of overlap, two packets' worth.
        device.Next(frames: 3 * SyntheticDevice.PacketFrames);
        device.Frame -= 2 * SyntheticDevice.PacketFrames;
        device.Next();
        device.Next();
        Assert.Equal(1, device.Stream.StepCorrections);
        Assert.Equal(1, device.Stream.Trims);
        Assert.InRange(device.Stream.TrimmedFrames, 959, 961);
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            Assert.InRange(device.Next(), -1, 1);
        }

        Assert.Equal(1, device.Stream.StepCorrections);
        Assert.Equal(1, device.Stream.Trims);
    }

    [Fact]
    public void BadStampsAreExtrapolatedFromTheLastPacket()
    {
        var device = new SyntheticDevice();
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            device.Next();
        }

        Assert.InRange(device.Next(stamp: 0), -1, 1);
        Assert.InRange(device.Next(AudioPacketFlags.TimestampError, stamp: 123), -1, 1);
        Assert.InRange(device.Next(stamp: device.TrueQpc(device.Frame - (3 * SyntheticDevice.PacketFrames))), -1, 1);
        Assert.Equal(3, device.Stream.ResolvedStamps);
        for (var i = 0; i < PacketsPerSecond; i++)
        {
            Assert.InRange(device.Next(), -1, 1);
        }

        Assert.Equal(0, device.Stream.StepCorrections);
    }

    [Fact]
    public void SilentPacketsComeOutAsSilentBlocks()
    {
        var device = new SyntheticDevice();
        for (var i = 0; i < 10; i++)
        {
            device.Next();
        }

        for (var i = 0; i < 10; i++)
        {
            device.Next(AudioPacketFlags.Silent);
        }

        Assert.True(device.Sink.LastWasSilent);
        Assert.True(device.Sink.SilentFrames > 4000);
    }
}
