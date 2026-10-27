namespace ReplayBuffer.Media.Tests;

public sealed class AdaptiveResamplerTests
{
    private const int Rate = 48_000;

    private static float[] Sine(int frames, double hz, double amplitude, long offset = 0)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (i + offset) / Rate));
            samples[2 * i] = v;
            samples[(2 * i) + 1] = -v;
        }

        return samples;
    }

    /// <summary>Pushes <paramref name="input"/> in packets and collects every output frame; silent runs become zeros.</summary>
    private static List<float> Run(AdaptiveResampler resampler, float[] input, int packetFrames = 480)
    {
        var output = new List<float>();
        var buffer = new float[1024 * 2];
        for (var offset = 0; offset < input.Length; offset += packetFrames * 2)
        {
            var length = Math.Min(packetFrames * 2, input.Length - offset);
            resampler.Push(input.AsSpan(offset, length));
            int count;
            while ((count = resampler.Produce(buffer, out var silent)) > 0)
            {
                for (var i = 0; i < count * 2; i++)
                {
                    output.Add(silent ? 0 : buffer[i]);
                }
            }
        }

        return output;
    }

    [Fact]
    public void AtRatioOneASinePassesUnchanged()
    {
        var resampler = new AdaptiveResampler(2048);
        var input = Sine(Rate, 1000, 0.5);
        var output = Run(resampler, input);

        // Output frame j is input frame j; only the last HalfTaps frames still wait for input.
        Assert.Equal((input.Length / 2) - AdaptiveResampler.HalfTaps, output.Count / 2);
        for (var i = 0; i < output.Count; i++)
        {
            Assert.Equal(input[i], output[i], 1e-6f);
        }
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(-1e-3)]
    public void AOneKilohertzSineKeepsItsAmplitudeThroughAThousandPpm(double deviation)
    {
        var resampler = new AdaptiveResampler(2048) { Step = 1 + deviation };
        const double amplitude = 0.5;
        var output = Run(resampler, Sine(2 * Rate, 1000, amplitude));

        // Peak over the steady part, past the kernel's start-up.
        var left = Enumerable.Range(0, output.Count / 2).Select(i => (double)output[2 * i]).Skip(1000).ToList();
        var rms = Math.Sqrt(left.Average(v => v * v));
        var db = 20 * Math.Log10(rms * Math.Sqrt(2) / amplitude);
        Assert.InRange(db, -0.1, 0.1);

        // The frequency moved by the ratio: 1 kHz content consumed faster comes out higher.
        var crossings = 0;
        for (var i = 1; i < left.Count; i++)
        {
            if (left[i - 1] < 0 && left[i] >= 0)
            {
                crossings++;
            }
        }

        var hz = crossings * (double)Rate / left.Count;
        Assert.InRange(hz, 1000 * (1 + deviation) - 2, 1000 * (1 + deviation) + 2);
    }

    [Fact]
    public void TheInputPositionMapsToTheOutputIndexAtTheCurrentStep()
    {
        var resampler = new AdaptiveResampler(2048) { Step = 1.0005 };
        Run(resampler, Sine(4800, 440, 0.1));
        var produced = resampler.OutputFrames;
        Assert.Equal(produced, resampler.OutputIndexOf(resampler.Position), 9);
        Assert.Equal(produced + (100 / 1.0005), resampler.OutputIndexOf(resampler.Position + 100), 9);
    }

    [Fact]
    public void SilenceComesOutAsSilentRunsAndResetStartsFromSilence()
    {
        var resampler = new AdaptiveResampler(2048);
        resampler.PushSilence(1000);
        var buffer = new float[2048 * 2];
        Assert.Equal(1000 - AdaptiveResampler.HalfTaps, resampler.Produce(buffer, out var silent));
        Assert.True(silent);

        // Sound after silence: the frames still waiting come out as samples (zeros until the sound), then the sound.
        resampler.Push(Sine(1000, 1000, 0.5));
        var count = resampler.Produce(buffer, out silent);
        Assert.False(silent);
        Assert.Equal(1000, count);
        Assert.All(Enumerable.Range(0, AdaptiveResampler.HalfTaps), i => Assert.Equal(0, buffer[2 * i]));

        resampler.Reset();
        Assert.Equal(0, resampler.InputFrames);
        Assert.Equal(0, resampler.OutputFrames);
        Assert.Equal(1, resampler.Step);
        resampler.Push(Sine(100, 1000, 0.5));
        Assert.True(resampler.Produce(buffer, out silent) > 0);
        Assert.False(silent);
    }
}
