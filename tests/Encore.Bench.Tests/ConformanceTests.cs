namespace Encore.Bench.Tests;

/// <summary>Story 8.7: the final conformance run's options and its pure analysis.</summary>
public sealed class ConformanceTests
{
    [Fact]
    public void TheConformanceOptionsParse()
    {
        var drift = BenchOptions.Parse(["drift", "--app", "--minutes", "2", "--save-seconds", "60", "--layout", "settings.json"]);
        var fps = BenchOptions.Parse(["fps", "--overlay", "--recording", "--seconds", "60"]);
        var playback = BenchOptions.Parse(["playback", "--auto", "--codecs", "h264,av1", "--recording-file", "drift.mp4"]);

        Assert.True(drift.Flag("app"));
        Assert.Equal("settings.json", drift.Text("layout"));
        Assert.True(fps.Flag("recording"));
        Assert.Equal(["h264", "av1"], playback.List("codecs", PlaybackCheck.DefaultCodecs));
        Assert.Equal(30, ConformanceDrift.DefaultMinutes);
        Assert.Equal(150, FpsBenchmark.RecordingSeconds);
    }

    [Fact]
    public void FpsRecordingNeedsTheOverlay()
    {
        using var run = new BenchRun();
        var context = run.Context("fps", "--recording");

        Assert.Throws<BenchUsageException>(() => FpsBenchmark.Run(context));
    }

    [Fact]
    public void AMixedTracksOnsetsSplitByTheirBurstGrid()
    {
        // A's bursts at whole seconds + 0.04, B's half a second later, both in one Track.
        double[] onsets = [1.04, 1.54, 2.04, 2.55, 3.03, 3.54];

        Assert.Equal([1.04, 2.04, 3.03], ConformanceDrift.OnGrid(onsets, 0.0));
        Assert.Equal([1.54, 2.55, 3.54], ConformanceDrift.OnGrid(onsets, 0.5));
    }

    [Fact]
    public void TrackToTrackDriftIsTheChangeOfTheMedianDifference()
    {
        var a = Series("Game", [0, 10, 12]);
        var b = Series("Chat", [5, 14, 25]);

        var pair = ConformanceDrift.Pair(a, b, 1000.0 / 60);

        // Differences −5, −4, −13: the largest change from the first is 8 ms, under a frame.
        Assert.Equal(8, pair.DriftMs);
        Assert.Equal(DriftMath.Pass, pair.Verdict);
    }

    [Fact]
    public void AnAppRunCountsAsTheCaptureCondition()
    {
        var summary = """{ "runs": [ { "run": "01-baseline-1", "condition": "baseline", "pass": 1, "avgFps": 80.0 }, { "run": "02-app-1", "condition": "app", "pass": 1, "avgFps": 78.0, "fpsLossPercent": 2.5 } ] }""";

        var report = FpsBenchmark.Map(summary, "summary.json", Path.GetTempPath(), "load", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 150, 1);

        Assert.Equal(2.5, report.CaptureMeans!.FpsLossPercent);
        Assert.Equal(DriftMath.Pass, report.Verdict);
    }

    private static ConformanceSeries Series(string title, double[] medians)
    {
        var buckets = medians.Select((m, i) => new DriftBucket(i, i * 60, 60, m)).ToList();
        var av = new AvResult(0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 60, buckets, medians[0], null, null, null, null, 16.7, DriftMath.Pass, null);
        return new ConformanceSeries(0, title, "device", av);
    }
}
