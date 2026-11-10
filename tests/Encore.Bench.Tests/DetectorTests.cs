namespace Encore.Bench.Tests;

public sealed class DetectorTests
{
    /// <summary>TonePlayer's signal: a 440 Hz tone 10 dB under the bursts, and a 50 ms 3 kHz burst at each listed sample.</summary>
    private static float[] ToneWithBursts(int seconds, IReadOnlyList<long> burstStarts, double dbfs = -30)
    {
        var burst = Math.Pow(10, dbfs / 20);
        var tone = Math.Pow(10, (dbfs - 10) / 20);
        var samples = new float[seconds * OnsetDetector.SampleRate];
        for (var n = 0; n < samples.Length; n++)
        {
            var inBurst = burstStarts.Any(s => n >= s && n < s + (OnsetDetector.SampleRate / 20));
            samples[n] = (float)(inBurst
                ? burst * Math.Sin(2 * Math.PI * 3000 * n / OnsetDetector.SampleRate)
                : tone * Math.Sin(2 * Math.PI * 440 * n / OnsetDetector.SampleRate));
        }

        return samples;
    }

    [Fact]
    public void OnsetsAreFoundWithinAMillisecondOfEachBurst()
    {
        long[] bursts = [48_000, 96_123, 144_500, 192_007];
        var samples = ToneWithBursts(5, bursts);
        var detector = new OnsetDetector();

        // In odd chunk sizes: the detector keeps its state across calls.
        for (var at = 0; at < samples.Length; at += 777)
        {
            detector.Process(samples.AsSpan(at, Math.Min(777, samples.Length - at)));
        }

        Assert.Equal(samples.Length, detector.Samples);
        Assert.Equal(bursts.Length, detector.Onsets.Count);
        Assert.All(bursts.Zip(detector.Onsets), p => Assert.InRange(p.Second - p.First, 0, 48));
    }

    [Fact]
    public void TheToneAloneHasNoOnsets()
    {
        var detector = new OnsetDetector();

        detector.Process(ToneWithBursts(3, []));

        Assert.Empty(detector.Onsets);
    }

    [Fact]
    public void TheThresholdsFollowTheBurstLevel()
    {
        long[] bursts = [24_000, 72_000];
        var quiet = ToneWithBursts(2, bursts, dbfs: -50);

        var defaults = new OnsetDetector();
        defaults.Process(quiet);
        var matched = new OnsetDetector(-50);
        matched.Process(quiet);

        Assert.Empty(defaults.Onsets);
        Assert.Equal(2, matched.Onsets.Count);
    }

    [Fact]
    public void StereoIsAveragedToMono()
    {
        long[] bursts = [10_000];
        var mono = ToneWithBursts(1, bursts);
        var stereo = new float[mono.Length * 2];
        for (var n = 0; n < mono.Length; n++)
        {
            stereo[2 * n] = mono[n];
            stereo[(2 * n) + 1] = mono[n];
        }

        var detector = new OnsetDetector();
        detector.ProcessStereo(stereo, new float[1000]);

        Assert.Equal(mono.Length, detector.Samples);
        Assert.Single(detector.Onsets);
        Assert.InRange(detector.Onsets[0] - bursts[0], 0, 48);
    }

    [Fact]
    public void AFlashStartsWhereTheFrameFirstGoesBright()
    {
        // 16 is black in limited range; a capture border and the title keep the dark frames above it.
        var means = Enumerable.Range(0, 300).Select(i => i % 60 < 3 ? 200.0 : 22.0 + (i % 7)).ToList();

        var flashes = FlashDetector.Detect(means);

        Assert.Equal([0, 60, 120, 180, 240], flashes);
    }

    [Fact]
    public void TooLittleContrastIsNoFlashAtAll()
    {
        var means = Enumerable.Range(0, 300).Select(i => i % 60 < 3 ? 30.0 : 20.0).ToList();

        Assert.Empty(FlashDetector.Detect(means));
    }

    [Fact]
    public void MeanLumaIsTheAverageByte()
    {
        byte[] frame = [0, 255, 0, 255];

        Assert.Equal(127.5, FlashDetector.MeanLuma(frame));
        Assert.Equal(0, FlashDetector.MeanLuma([]));
    }
}
