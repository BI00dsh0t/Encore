using System.Text.Json;

namespace Encore.Bench.Tests;

public sealed class DriftMathTests
{
    private const long Frequency = 10_000_000;
    private const long Anchor = 5_000_000_000;

    /// <summary>A synthetic 120 s run: a flash each second, and each burst 0.2 s later plus an offset and a linear drift.</summary>
    private static AvInput Synthetic(double driftMs, double offsetMs = 23, double anchorErrorSeconds = 0.08, double bucketSeconds = 10, int dropBursts = 0)
    {
        var flashQpcs = Enumerable.Range(0, 120).Select(k => Anchor + (k * Frequency) + (31 * Frequency / 100)).ToList();
        var burstQpcs = flashQpcs.Select(q => q + (2 * Frequency / 10)).ToList();
        var flashFile = flashQpcs.Select(q => Math.Ceiling((q - Anchor) / (double)Frequency * 60) / 60).ToList();
        var onsets = burstQpcs.Select((q, k) => ((q - Anchor) / (double)Frequency) + (offsetMs / 1000) + (driftMs / 1000 * k / 119)).ToList();
        onsets = [.. onsets.Where((_, k) => k % 10 >= dropBursts)];
        return new AvInput(
            flashFile,
            onsets,
            flashQpcs,
            burstQpcs,
            Frequency,
            Anchor + (long)(anchorErrorSeconds * Frequency),
            (0, 120),
            (0, 120),
            1000.0 / 60,
            bucketSeconds);
    }

    [Fact]
    public void ALinearDriftIsTheChangeOfTheBucketMedians()
    {
        var result = DriftMath.AnalyzeAv(Synthetic(driftMs: 6));

        Assert.Equal(DriftMath.Pass, result.Verdict);
        Assert.Equal(1.0, result.FlashMatchedFraction);
        Assert.Equal(1.0, result.BurstMatchedFraction);
        Assert.Equal(120, result.Pairs);
        Assert.InRange(result.DriftMs!.Value, 5, 6.5);
        Assert.InRange(result.RegressionDriftMs!.Value, 5.5, 6.5);
        Assert.InRange(result.SlopeMsPerMinute!.Value, 2.8, 3.2);
    }

    [Fact]
    public void NoDriftLeavesOnlyTheConstantOffset()
    {
        var result = DriftMath.AnalyzeAv(Synthetic(driftMs: 0, offsetMs: 40));

        Assert.InRange(result.DriftMs!.Value, 0, 1);

        // The offset is the pipeline's 40 ms less the flash's quantisation to the next frame.
        Assert.InRange(result.AbsoluteOffsetMs!.Value, 40 - 16.7, 40);
    }

    [Fact]
    public void AMissedDriftIsAMiss()
    {
        var result = DriftMath.AnalyzeAv(Synthetic(driftMs: 40));

        Assert.Equal(DriftMath.Miss, result.Verdict);
    }

    [Fact]
    public void FewMatchedBurstsAreInconclusive()
    {
        var result = DriftMath.AnalyzeAv(Synthetic(driftMs: 0, dropBursts: 2));

        Assert.Equal(DriftMath.Inconclusive, result.Verdict);
        Assert.InRange(result.BurstMatchedFraction, 0.75, 0.85);
        Assert.NotNull(result.Why);
    }

    [Fact]
    public void NothingDetectedIsInconclusive()
    {
        var input = Synthetic(driftMs: 0) with { FlashFileTimes = [], OnsetFileTimes = [] };

        var result = DriftMath.AnalyzeAv(input);

        Assert.Equal(DriftMath.Inconclusive, result.Verdict);
        Assert.Equal(0, result.Pairs);
        Assert.Null(result.DriftMs);
    }

    [Fact]
    public void AnAnchorOffByMoreThanHalfASecondStillPairsTheRightMarkers()
    {
        var result = DriftMath.AnalyzeAv(Synthetic(driftMs: 6, anchorErrorSeconds: 1.7));

        Assert.Equal(DriftMath.Pass, result.Verdict);
        Assert.InRange(result.DriftMs!.Value, 5, 6.5);
    }

    [Fact]
    public void BucketsNeedAQuarterOfTheirSecondsInErrors()
    {
        var errors = Enumerable.Range(0, 70).Select(i => ((double)i, i < 60 ? 1.0 : 3.0)).ToList();

        var buckets = DriftMath.Buckets(errors, 60);

        Assert.Single(buckets);
        Assert.Equal(60, buckets[0].Count);
        Assert.Equal(1.0, buckets[0].MedianMs);
    }

    [Fact]
    public void PercentileIsNearestRankAndMedianAveragesTheMiddlePair()
    {
        double[] values = [5, 1, 4, 2, 3, 10];

        Assert.Equal(3.5, DriftMath.Median(values));
        Assert.Equal(3, DriftMath.Percentile(values, 50));
        Assert.Equal(10, DriftMath.Percentile(values, 99));
    }

    [Fact]
    public void AudioToAudioBoundsBothDevicesErrorsAgainstOneFrame()
    {
        using var stats = JsonDocument.Parse(
            """
            { "audio": [
              { "role": "output", "device": "Game", "stamps": { "drift": { "ppm": 12.5 } }, "timeline": { "gapFills": 1, "trims": 0 },
                "resampler": { "meanRatioPpm": -12.4, "stepCorrections": 0, "settledPackets": 100, "errorP99Samples": 20, "errorMaxSamples": 31 } },
              { "role": "input", "device": "Mic", "stamps": { "drift": { "ppm": -40 } }, "timeline": { "gapFills": 0, "trims": 2 },
                "resampler": { "meanRatioPpm": 40.1, "stepCorrections": 1, "settledPackets": 90, "errorP99Samples": 30, "errorMaxSamples": -45 } }
            ] }
            """);

        var result = DriftMath.AudioToAudio(stats.RootElement, 60);

        Assert.Equal(DriftMath.Pass, result.Verdict);
        Assert.Equal(76, result.BoundSamples);
        Assert.Equal(800, result.TargetSamples);
        Assert.Equal(1.583, result.BoundMs);
        Assert.Equal(2, result.Devices[1].Trims);
        Assert.Equal(12.5, result.Devices[0].RawDriftPpm);
    }

    [Fact]
    public void AudioToAudioWithADeviceMissingIsInconclusive()
    {
        using var stats = JsonDocument.Parse(
            """{ "audio": [ { "role": "output", "device": "Game", "resampler": { "errorMaxSamples": 3 } } ] }""");

        var result = DriftMath.AudioToAudio(stats.RootElement, 60);

        Assert.Equal(DriftMath.Inconclusive, result.Verdict);
        Assert.Null(result.BoundSamples);
        Assert.Contains("input", result.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioToAudioPastOneFrameIsAMiss()
    {
        using var stats = JsonDocument.Parse(
            """
            { "audio": [
              { "role": "output", "device": "Game", "resampler": { "errorMaxSamples": 500, "settledPackets": 10 } },
              { "role": "input", "device": "Mic", "resampler": { "errorMaxSamples": 400, "settledPackets": 10 } } ] }
            """);

        Assert.Equal(DriftMath.Miss, DriftMath.AudioToAudio(stats.RootElement, 60).Verdict);
    }

    [Theory]
    [InlineData("\"errorMaxSamples\": 3, \"settledPackets\": 0", "no settled packets")]
    [InlineData("\"errorMaxSamples\": \"NaN\", \"settledPackets\": 10", "no numeric errorMaxSamples")]
    [InlineData("\"settledPackets\": 10", "no numeric errorMaxSamples")]
    public void AnUnmeasuredDeviceIsInconclusiveNotAZeroError(string inputResampler, string why)
    {
        using var stats = JsonDocument.Parse(
            $$"""
            { "audio": [
              { "role": "output", "device": "Game", "resampler": { "errorMaxSamples": 5, "settledPackets": 10 } },
              { "role": "input", "device": "Mic", "resampler": { {{inputResampler}} } } ] }
            """);

        var result = DriftMath.AudioToAudio(stats.RootElement, 60);

        Assert.Equal(DriftMath.Inconclusive, result.Verdict);
        Assert.Null(result.BoundSamples);
        Assert.Equal($"the input device has {why}", result.Why);
    }

    [Fact]
    public void TracksAreCheckedAgainstTheVideoSpanNotItsFrameCount()
    {
        // The 60 min drift run: the pacer skipped 33 slots, so 215967 frames span the full 3600 s.
        var scan = new MediaScan(
            60,
            [
                new StreamSpan(0, "video", null, 0, 3600.0, 215967),
                new StreamSpan(1, "audio", "Game", 0, 3600.021, 168751),
                new StreamSpan(2, "audio", "Mic", 0, 3600.021, 168751),
            ],
            215967,
            [],
            0,
            172801024,
            []);

        var tracks = DriftBenchmark.CheckTracks(scan, 1000.0 / 60);

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.Equal(DriftMath.Pass, t.Mark));
        Assert.InRange(tracks[0].MinusVideoMs, 21, 22);
        Assert.InRange(tracks[1].MinusVideoMs, 21, 22);
    }

    [Fact]
    public void ATrackLongerThanTheVideoByMoreThanAFrameIsAMiss()
    {
        var scan = new MediaScan(
            60,
            [new StreamSpan(0, "video", null, 0, 10.0, 600), new StreamSpan(1, "audio", "Game", 0, 10.1, 474)],
            600,
            [],
            0,
            484_800,
            []);

        var track = Assert.Single(DriftBenchmark.CheckTracks(scan, 1000.0 / 60));

        Assert.Equal(DriftMath.Miss, track.Mark);
        Assert.InRange(track.MinusVideoMs, 99, 101);
    }
}
