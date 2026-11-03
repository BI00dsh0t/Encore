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

    [Theory]
    [InlineData(12_345)] // between frames: frame 59.256 → 60
    [InlineData(12_500)] // exactly frame 60
    [InlineData(1)] // just after the anchor: frame 1
    public void AMuteSilencesFromThePressFrameAndLeavesEarlierFramesBitIdentical(long ticksAfterAnchor)
    {
        var press = Anchor + ticksAfterAnchor;
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

    [Theory]
    [InlineData(-1_000)]
    [InlineData(0)]
    public void AChangeAtOrBeforeTheAnchorAppliesFromFrameZero(long ticksAfterAnchor)
    {
        var schedule = new MicMuteSchedule(muted: false);
        schedule.Set(true, Anchor + ticksAfterAnchor);
        var sink = new SampleSink();
        var gate = new MicMuteGate(sink, schedule, Qpf, Anchor);

        Feed(gate, 1, 0, 500, 480);

        AssertSilent(sink, 0, 500);
    }

    [Fact]
    public void ChangesInsideOneWriteSplitItIntoRuns()
    {
        var schedule = new MicMuteSchedule(muted: false);
        schedule.Set(true, Anchor + (300 * Qpf / 48_000));
        schedule.Set(false, Anchor + (700 * Qpf / 48_000));
        var sink = new SampleSink();
        var gate = new MicMuteGate(sink, schedule, Qpf, Anchor);

        gate.Write(Chunk(3, 0, 1000));

        Assert.Equal((2, 1), (sink.Writes, sink.SilenceWrites));
        AssertPassed(sink, 3, 0, 300);
        AssertSilent(sink, 300, 700);
        AssertPassed(sink, 3, 700, 1000);
    }

    [Fact]
    public void SilentWritesPassThroughAndCountTowardsThePosition()
    {
        var schedule = new MicMuteSchedule(muted: false);
        schedule.Set(true, Anchor + (600 * Qpf / 48_000));
        var sink = new SampleSink();
        var gate = new MicMuteGate(sink, schedule, Qpf, Anchor);

        gate.WriteSilence(500);
        gate.Write(Chunk(1, 500, 200));

        Assert.Equal((1, 2), (sink.Writes, sink.SilenceWrites));
        Assert.Equal(700, gate.Position);
        AssertSilent(sink, 0, 500);
        AssertPassed(sink, 1, 500, 600);
        AssertSilent(sink, 600, 700);
    }

    [Fact]
    public void TheScheduleIgnoresASetToTheSameStateAndKeepsChangesInOrder()
    {
        var schedule = new MicMuteSchedule(muted: false);
        schedule.Set(false, Anchor + 100);
        Assert.False(schedule.Muted);

        // An unmute stamped before the mute it follows applies with it: no frame is muted.
        schedule.Set(true, Anchor + (480 * Qpf / 48_000));
        schedule.Set(false, Anchor);
        var sink = new SampleSink();
        var gate = new MicMuteGate(sink, schedule, Qpf, Anchor);

        Feed(gate, 1, 0, 1000, 256);

        AssertPassed(sink, 1, 0, 1000);
        Assert.False(schedule.Muted);
    }

    [Fact]
    public void ASingleInputMixerBehindAGateIsSilencedFromThePressFrame()
    {
        var schedule = new MicMuteSchedule(muted: false);
        var output = new SampleSink();
        var mixer = new TrackMixer(1, output);
        var gate = new MicMuteGate(mixer.Input(0), schedule, Qpf, Anchor);
        var press = Anchor + 123_457; // frame 592.59 → 593

        Feed(gate, 4, 0, 480, 480);
        schedule.Set(true, press);
        Feed(gate, 4, 480, 1520, 480);

        Assert.Equal(2000, mixer.EmittedFrames);
        AssertPassed(output, 4, 0, 593);
        AssertSilent(output, 593, 2000);
    }

    [Fact]
    public void InATwoInputMixerOnlyTheGatedInputIsSilenced()
    {
        var schedule = new MicMuteSchedule(muted: false);
        var output = new SampleSink();
        var mixer = new TrackMixer(2, output);
        var mic = new MicMuteGate(mixer.Input(0), schedule, Qpf, Anchor);
        var game = mixer.Input(1);
        var press = Anchor + (1000 * Qpf / 48_000);

        schedule.Set(true, press);
        for (var start = 0; start < 2880; start += 480)
        {
            mic.Write(Chunk(1, start, 480));
            game.Write(Chunk(5, start, 480));
        }

        Assert.Equal(2880, mixer.EmittedFrames);
        for (var k = 0; k < 2880; k++)
        {
            // The mixer sums from zero, in input order.
            var left = k < 1000 ? 0f + Value(1, k) + Value(5, k) : 0f + 0f + Value(5, k);
            Assert.True(output.Left(k) == left, $"frame {k}: {output.Left(k)}, expected {left}");
        }
    }

    [Fact]
    public void WritingDoesNotAllocateOnceWarmedUp()
    {
        var schedule = new MicMuteSchedule(muted: false);
        for (var i = 1; i <= 6; i++)
        {
            schedule.Set(i % 2 == 1, Anchor + (i * 1000 * Qpf / 48_000));
        }

        var gate = new MicMuteGate(new NullSink(), schedule, Qpf, Anchor);
        var block = Chunk(1, 0, 480);
        gate.Write(block);
        gate.WriteSilence(10);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            gate.Write(block);
            gate.WriteSilence(5);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private sealed class NullSink : IAudioSampleSink
    {
        public void Write(ReadOnlySpan<float> interleaved)
        {
        }

        public void WriteSilence(long frames)
        {
        }
    }
}
