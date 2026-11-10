namespace Encore.Media.Tests;

/// <summary>Records every interleaved sample written, silence as zeros, and how it was written.</summary>
internal sealed class SampleSink : IAudioSampleSink
{
    public List<float> Samples { get; } = [];

    public int Writes { get; private set; }

    public int SilenceWrites { get; private set; }

    public long Frames => Samples.Count / 2;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        Writes++;
        foreach (var sample in interleaved)
        {
            Samples.Add(sample);
        }
    }

    public void WriteSilence(long frames)
    {
        SilenceWrites++;
        for (long i = 0; i < frames * 2; i++)
        {
            Samples.Add(0);
        }
    }

    /// <summary>The left sample of frame <paramref name="frame"/>.</summary>
    public float Left(long frame) => Samples[(int)(frame * 2)];

    public float Right(long frame) => Samples[(int)(frame * 2) + 1];
}

public sealed class TrackMixerTests
{
    private readonly SampleSink _output = new();

    /// <summary>
    /// A device's stream: frame k is (v, -v) with v = seed / 20 + (k mod 4096) / 16384, so frames
    /// within 4096 of each other are distinct, and any two devices' sum (seeds up to 7) stays within
    /// full scale, which the limiter leaves bit-identical.
    /// </summary>
    private static float Value(float seed, long frame) => (seed / 20) + ((frame % 4096) / 16384f);

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

    /// <summary>Writes <paramref name="total"/> frames of a device's stream in chunks of <paramref name="chunk"/>.</summary>
    private static IEnumerable<Action> Feed(IAudioSampleSink sink, float seed, int chunk, long total)
    {
        for (long start = 0; start < total; start += chunk)
        {
            var frames = (int)Math.Min(chunk, total - start);
            var at = start;
            yield return () => sink.Write(Chunk(seed, at, frames));
        }
    }

    [Fact]
    public void OneInputPassesStraightThroughBitIdentically()
    {
        var mixer = new TrackMixer(1, _output);
        var input = mixer.Input(0);
        var chunk = Chunk(0.25f, 0, 480);
        chunk[0] = -0f;
        chunk[1] = float.Epsilon;

        input.Write(chunk);
        input.WriteSilence(7);
        mixer.Flush();

        Assert.Equal(1, _output.Writes);
        Assert.Equal(1, _output.SilenceWrites);
        Assert.Equal(487, _output.Frames);
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(_output.Samples[0]));
        Assert.Equal(float.Epsilon, _output.Samples[1]);
        Assert.Equal(chunk, _output.Samples.Take(960));
    }

    [Fact]
    public void UnevenChunksAreSummedFrameByFrame()
    {
        var mixer = new TrackMixer(2, _output);
        const long total = 48_000;
        var a = Feed(mixer.Input(0), 1, 480, total).ToList();
        var b = Feed(mixer.Input(1), 3, 441, total).ToList();

        // Interleave the two devices' writes as two threads might.
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count)
            {
                a[i]();
            }

            if (i < b.Count)
            {
                b[i]();
            }
        }

        mixer.Flush();

        Assert.Equal(total, _output.Frames);
        for (long k = 0; k < total; k++)
        {
            Assert.Equal(Value(1, k) + Value(3, k), _output.Left(k));
            Assert.Equal(-Value(1, k) + -Value(3, k), _output.Right(k));
        }
    }

    [Fact]
    public void NothingIsEmittedUntilEveryInputHasReachedIt()
    {
        var mixer = new TrackMixer(2, _output);

        mixer.Input(0).Write(Chunk(1, 0, 1000));
        Assert.Equal(0, _output.Frames);

        mixer.Input(1).Write(Chunk(2, 0, 600));
        Assert.Equal(600, _output.Frames);

        mixer.Input(1).Write(Chunk(2, 600, 600));
        Assert.Equal(1000, _output.Frames);
    }

    [Fact]
    public void SilenceInputsCountAsZero()
    {
        var mixer = new TrackMixer(3, _output);

        mixer.Input(0).Write(Chunk(1, 0, 500));
        mixer.Input(1).WriteSilence(500);
        mixer.Input(2).WriteSilence(200);
        mixer.Input(2).Write(Chunk(5, 200, 300));
        mixer.Flush();

        Assert.Equal(500, _output.Frames);
        for (long k = 0; k < 500; k++)
        {
            var expected = k < 200 ? Value(1, k) : Value(1, k) + Value(5, k);
            Assert.Equal(expected, _output.Left(k));
        }
    }

    [Fact]
    public void AStalledInputIsFilledWithSilenceAtTheLagBoundAndItsLateFramesDropped()
    {
        var mixer = new TrackMixer(2, _output);
        var leader = mixer.Input(0);
        const int chunk = 480;
        var lead = 3 * TrackMixer.MaxLagFrames;

        // The second input stalls entirely while the first runs 6 s.
        foreach (var write in Feed(leader, 1, chunk, lead))
        {
            write();
        }

        Assert.Equal(lead - TrackMixer.MaxLagFrames, _output.Frames);
        for (long k = 0; k < _output.Frames; k += 997)
        {
            Assert.Equal(Value(1, k), _output.Left(k));
        }

        // It comes back with its stream from frame 0: what the mix has passed is dropped.
        var laggard = mixer.Input(1);
        var passed = _output.Frames;
        laggard.Write(Chunk(7, 0, (int)passed + 100));
        Assert.Equal(passed, mixer.DroppedFrames);
        Assert.Equal(passed + 100, _output.Frames);
        Assert.Equal(Value(1, passed) + Value(7, passed), _output.Left(passed));

        laggard.Write(Chunk(7, passed + 100, 200));
        Assert.Equal(passed + 300, _output.Frames);
        Assert.Equal(Value(1, passed + 299) + Value(7, passed + 299), _output.Left(passed + 299));
    }

    [Fact]
    public void MemoryStaysBoundedWhenOneInputNeverWrites()
    {
        var mixer = new TrackMixer(2, _output);

        // 60 s in one silent write: the mix keeps pace 2 s behind, without a 60 s buffer.
        mixer.Input(0).WriteSilence(60 * 48_000);

        Assert.Equal((60 * 48_000) - TrackMixer.MaxLagFrames, _output.Frames);
    }

    [Fact]
    public void FlushPadsTheMixToTheLongestInput()
    {
        var mixer = new TrackMixer(2, _output);

        mixer.Input(0).Write(Chunk(1, 0, 1000));
        mixer.Input(1).Write(Chunk(2, 0, 400));
        Assert.Equal(400, _output.Frames);

        mixer.Flush();

        Assert.Equal(1000, _output.Frames);
        Assert.Equal(Value(1, 399) + Value(2, 399), _output.Left(399));
        Assert.Equal(Value(1, 400), _output.Left(400));
        Assert.Equal(Value(1, 999), _output.Left(999));
    }

    [Fact]
    public void AFanOutWritesOneStreamToEverySink()
    {
        var single = new SampleSink();
        var mixed = new TrackMixer(2, _output);
        var fanOut = new AudioFanOut([single, mixed.Input(0)]);

        fanOut.Write(Chunk(1, 0, 300));
        fanOut.WriteSilence(100);
        mixed.Input(1).Write(Chunk(4, 0, 400));

        Assert.Equal(400, single.Frames);
        Assert.Equal(Value(1, 299), single.Left(299));
        Assert.Equal(0, single.Left(300));
        Assert.Equal(400, _output.Frames);
        Assert.Equal(Value(1, 10) + Value(4, 10), _output.Left(10));
        Assert.Equal(Value(4, 350), _output.Left(350));
    }

    /// <summary>Frames of a full-scale square wave: +1 or -1 on both channels, flipping every <paramref name="half"/> frames.</summary>
    private static float[] Square(long start, int frames, int half)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = ((start + i) / half) % 2 == 0 ? 1f : -1f;
            samples[2 * i] = v;
            samples[(2 * i) + 1] = v;
        }

        return samples;
    }

    private static float[] Sine(long start, int frames, double hz)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)Math.Sin(2 * Math.PI * hz * (start + i) / AudioTimeline.SampleRate);
            samples[2 * i] = v;
            samples[(2 * i) + 1] = -v;
        }

        return samples;
    }

    [Fact]
    public void ASingleDeviceIsBitIdenticalThroughTheLimiterIncludingFullScale()
    {
        var mixer = new TrackMixer(1, _output);
        var chunk = Sine(0, 4800, 997);
        chunk[0] = 1f;
        chunk[1] = -1f;
        chunk[2] = -0f;

        mixer.Input(0).Write(chunk);

        Assert.Equal(chunk.Select(BitConverter.SingleToInt32Bits), _output.Samples.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(0, mixer.LimitedFrames);
        Assert.Equal(4800, mixer.EmittedFrames);
    }

    [Fact]
    public void TwoFullScaleSourcesNeverExceedFullScale()
    {
        var mixer = new TrackMixer(2, _output);
        const int total = 48_000;
        for (var start = 0; start < total; start += 480)
        {
            mixer.Input(0).Write(Square(start, 480, 50));
            mixer.Input(1).Write(Sine(start, 480, 440));
        }

        mixer.Flush();

        Assert.Equal(total, _output.Frames);
        Assert.All(_output.Samples, x => Assert.InRange(x, -1f, 1f));
        Assert.Contains(1f, _output.Samples);
        Assert.True(mixer.LimitedFrames > 0);
    }

    [Fact]
    public void ADeviceFannedOutToTwoMixersIsInBothSampleAligned()
    {
        var alone = new SampleSink();
        var mixed = new SampleSink();
        var a = new TrackMixer(1, alone);
        var b = new TrackMixer(2, mixed);
        var device = new AudioFanOut([a.Input(0), b.Input(0)]);
        const long total = 9_600;

        // D runs in 480-frame blocks, E in 441-frame blocks, the two interleaved.
        var d = Feed(device, 2, 480, total).ToList();
        var e = Feed(b.Input(1), 5, 441, total).ToList();
        for (var i = 0; i < Math.Max(d.Count, e.Count); i++)
        {
            if (i < d.Count)
            {
                d[i]();
            }

            if (i < e.Count)
            {
                e[i]();
            }
        }

        a.Flush();
        b.Flush();

        Assert.Equal(total, alone.Frames);
        Assert.Equal(total, mixed.Frames);
        for (long k = 0; k < total; k++)
        {
            Assert.Equal(Value(2, k), alone.Left(k));
            Assert.Equal(-Value(2, k), alone.Right(k));
            Assert.Equal(Value(2, k) + Value(5, k), mixed.Left(k));
            Assert.Equal(-Value(2, k) + -Value(5, k), mixed.Right(k));
        }
    }

    [Fact]
    public void AMixerWithNoInputsWritesSilenceUpToPadTo()
    {
        var mixer = new TrackMixer(0, _output);

        mixer.PadTo(4800);
        mixer.PadTo(4000);
        mixer.PadTo(9600);
        mixer.Flush();

        Assert.Equal(0, mixer.Inputs);
        Assert.Equal(0, mixer.LongestInputFrames);
        Assert.Equal(9600, mixer.EmittedFrames);
        Assert.Equal(9600, _output.Frames);
        Assert.Equal(0, _output.Writes);
        Assert.All(_output.Samples, x => Assert.Equal(0f, x));
    }
}
