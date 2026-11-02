namespace ReplayBuffer.Media.Tests;

public sealed class PeakLimiterTests
{
    private readonly SampleSink _output = new();

    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x);

    private static float[] Constant(int frames, float left, float right)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[2 * i] = left;
            samples[(2 * i) + 1] = right;
        }

        return samples;
    }

    private static float[] Sine(int frames, double hz, double amplitude)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / AudioTimeline.SampleRate));
            samples[2 * i] = v;
            samples[(2 * i) + 1] = -v;
        }

        return samples;
    }

    [Fact]
    public void InRangeAudioIsBitIdenticalIncludingFullScale()
    {
        var limiter = new PeakLimiter(_output);
        var samples = Sine(4800, 440, 1);
        samples[0] = 1f;
        samples[1] = -1f;
        samples[2] = -0f;
        samples[3] = float.Epsilon;

        limiter.Write(samples);

        Assert.Equal(samples.Select(Bits), _output.Samples.Select(Bits));
        Assert.Equal(1, _output.Writes);
        Assert.Equal(0, limiter.LimitedFrames);
        Assert.Equal(1, limiter.Gain);
    }

    [Fact]
    public void OversNeverExceedFullScale()
    {
        var limiter = new PeakLimiter(_output);
        var samples = Sine(48_000, 220, 2.5);
        samples[1000] = 1e30f;
        samples[1001] = -1e30f;

        // In blocks that aren't a multiple of the scratch chunk.
        for (var start = 0; start < samples.Length; start += 2 * 5000)
        {
            limiter.Write(samples.AsSpan(start, Math.Min(2 * 5000, samples.Length - start)));
        }

        Assert.Equal(48_000, _output.Frames);
        Assert.All(_output.Samples, x => Assert.InRange(x, -1f, 1f));
        Assert.Contains(1f, _output.Samples);
        Assert.Contains(-1f, _output.Samples);
        Assert.True(limiter.LimitedFrames > 0);
    }

    [Fact]
    public void BothChannelsShareOneGain()
    {
        var limiter = new PeakLimiter(_output);

        limiter.Write([1.5f, 0.5f]);

        Assert.Equal(1f, _output.Left(0));
        Assert.Equal((float)(0.5 / 1.5), _output.Right(0), 6);
        Assert.Equal(1, limiter.LimitedFrames);
    }

    [Fact]
    public void ReleaseRecoversAndSnapsToExactlyOne()
    {
        var limiter = new PeakLimiter(_output);
        limiter.Write([2f, 2f]);
        Assert.Equal(1f, _output.Left(0));

        // 10 ms after the over the gain is still recovering.
        var quiet = Constant(480, 0.5f, -0.5f);
        limiter.Write(quiet);
        var expected = 1 - ((1 - 0.5) * Math.Pow(PeakLimiter.ReleaseCoefficient, 481));
        Assert.Equal(expected, limiter.Gain, 9);
        Assert.True(_output.Left(480) < 0.5f);
        Assert.True(_output.Left(480) > _output.Left(1));

        // Well past the time constant it snaps to exactly 1, and audio is bit-identical again.
        limiter.Write(Constant(48_000, 0.5f, -0.5f));
        Assert.Equal(1, limiter.Gain);
        var after = Sine(4800, 997, 1);
        var before = _output.Samples.Count;
        limiter.Write(after);
        Assert.Equal(after.Select(Bits), _output.Samples.Skip(before).Select(Bits));

        // Frames limited: the over and every one written before the snap.
        Assert.InRange(limiter.LimitedFrames, 481, 48_481);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteSamplesAreWrittenAsZero(float bad)
    {
        var limiter = new PeakLimiter(_output);

        limiter.Write([0.25f, -0.75f, bad, 0.5f, 0.125f, bad]);

        Assert.Equal([0.25f, -0.75f, 0f, 0.5f, 0.125f, 0f], _output.Samples);
        Assert.Equal(1, limiter.Gain);
    }

    [Fact]
    public void SilenceAdvancesTheRelease()
    {
        var limiter = new PeakLimiter(_output);
        limiter.Write([4f, 0f]);

        limiter.WriteSilence(240);
        var expected = 1 - ((1 - 0.25) * Math.Pow(PeakLimiter.ReleaseCoefficient, 241));
        Assert.Equal(expected, limiter.Gain, 9);
        Assert.Equal(241, _output.Frames);

        limiter.WriteSilence(48_000);
        Assert.Equal(1, limiter.Gain);

        // Silence is forwarded as silence, and later audio is untouched.
        Assert.Equal(2, _output.SilenceWrites);
        limiter.Write([0.9f, -0.9f]);
        Assert.Equal(0.9f, _output.Left(48_241));
    }

    [Fact]
    public void NothingIsAllocatedPerBlockOnceWarmed()
    {
        var sink = new NullSink();
        var limiter = new PeakLimiter(sink);
        var over = Sine(10_000, 440, 3);
        var inRange = Sine(10_000, 440, 0.5);
        limiter.Write(over);
        limiter.Write(inRange);
        limiter.WriteSilence(100);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            limiter.Write(over);
            limiter.Write(inRange);
            limiter.WriteSilence(480);
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
