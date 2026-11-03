namespace ReplayBuffer.Media.Tests;

public sealed class MicMuteGateTests
{
    private const long Qpf = 10_000_000;
    private const long Anchor = 5_000_000;

    /// <summary>A device's stream: frame k is (v, -v), v nonzero and distinct within 4096 frames.</summary>
    private static float Value(float seed, long frame) => (seed / 20) + (((frame % 4096) + 1) / 16384f);

    private static float[] Chunk(float seed, long start, int frames)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[2 * i] = Value(seed, start + i);
            samples[(2 * i) + 1] = -Value(seed, start + i);
        }

        return samples;
    }

    /// <summary>Writes <paramref name="frames"/> frames of seed <paramref name="seed"/> in <paramref name="chunk"/>-frame writes.</summary>
    private static void Feed(IAudioSampleSink sink, float seed, long start, int frames, int chunk)
    {
        for (var done = 0; done < frames;)
        {
            var n = Math.Min(chunk, frames - done);
            sink.Write(Chunk(seed, start + done, n));
            done += n;
        }
    }

    /// <summary>The first frame at or after <paramref name="qpc"/>, computed independently.</summary>
    private static long ExpectedFrame(long qpc) => (long)Math.Ceiling((double)(qpc - Anchor) * 48_000 / Qpf);

    private static void AssertPassed(SampleSink sink, float seed, long from, long to)
    {
        for (var k = from; k < to; k++)
        {
            Assert.True(sink.Left(k) == Value(seed, k) && sink.Right(k) == -Value(seed, k), $"frame {k} was changed");
        }
    }

    private static void AssertSilent(SampleSink sink, long from, long to)
    {
        for (var k = from; k < to; k++)
        {
            Assert.True(sink.Left(k) == 0 && sink.Right(k) == 0, $"frame {k} wasn't silent");
        }
    }

    [Fact]
    public void AMuteSilencesFromThePressFrameAndLeavesEarlierFramesBitIdentical()
    {
        var press = Anchor + 12_345; // between frames: frame 59.256 → 60
        var schedule = new MicMuteSchedule(muted: false);
        var sink = new SampleSink();
        var gate = new MicMuteGate(sink, schedule, Qpf, Anchor);

        // Some frames pass before the press is set, none of them at or past its frame.
        var frame = ExpectedFrame(press);
        var before = (int)Math.Min(40, frame);
        Feed(gate, 1, 0, before, 16);
        schedule.Set(true, press);
        Feed(gate, 1, before, 2040 - before, 480);

        Assert.Equal(frame, MicMuteGate.FrameFrom(press, Anchor, Qpf));
        Assert.Equal(2040, sink.Frames);
        AssertPassed(sink, 1, 0, frame);
        AssertSilent(sink, frame, 2040);
        Assert.Equal(2040, gate.Position);
    }

    [Fact]
    public void AnUnmuteMakesTheMicAudibleFromItsFrame()
    {
        var schedule = new MicMuteSchedule(muted: true);
        var sink = new SampleSink();
        var gate = new MicMuteGate(sink, schedule, Qpf, Anchor);
        Feed(gate, 2, 0, 100, 100);
        schedule.Set(false, Anchor + (Qpf / 100)); // frame 480
        schedule.Set(true, Anchor + (Qpf / 20) + 7); // frame 2400.03 → 2401
        schedule.Set(false, Anchor + (Qpf / 10)); // frame 4800
        Feed(gate, 2, 100, 5900, 441);

        AssertSilent(sink, 0, 480);
        AssertPassed(sink, 2, 480, 2401);
        AssertSilent(sink, 2401, 4800);
        AssertPassed(sink, 2, 4800, 6000);
        Assert.False(schedule.Muted);
    }
}
