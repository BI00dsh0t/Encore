using System.Diagnostics;

namespace ReplayBuffer.Bench.Tests;

/// <summary>Story 8.1's soak harness, without the soak: the sampler, the bounds, the Clip check and the schedule.</summary>
public sealed class SoakTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSamplerReadsThisProcesssCounters()
    {
        using var self = Process.GetCurrentProcess();
        var counters = SoakSampler.Sample(self, "tests");

        Assert.Equal("tests", counters.Name);
        Assert.Equal(Environment.ProcessId, counters.Pid);
        Assert.True(counters.PrivateBytes > 0);
        Assert.True(counters.WorkingSet > 0);
        Assert.True(counters.Handles > 0);
        Assert.True(counters.Gdi >= 0 && counters.User >= 0);
    }

    [Fact]
    public void AFlatSeriesMeetsEveryBound()
    {
        var bounds = SoakBounds.Evaluate(Series(_ => 200 * Mb, _ => 400), TimeSpan.FromMinutes(5));

        Assert.NotEmpty(bounds);
        Assert.All(bounds, b => Assert.True(b.Pass, b.Detail));
    }

    [Fact]
    public void MemoryGrowthOfThreeMbAnHourFailsTheSlope()
    {
        // An hour at 10 s: +3 MB/h after the warm-up, well inside the 20 MB end bound.
        var bounds = SoakBounds.Evaluate(Series(t => 200 * Mb + (long)(3 * Mb * t.TotalHours), _ => 400, TimeSpan.FromHours(1)), TimeSpan.FromMinutes(5));

        var slope = Assert.Single(bounds, b => b.Counter == "privateBytesSlope");
        Assert.False(slope.Pass);
        Assert.Contains("≥ 2 MB/h", slope.Detail, StringComparison.Ordinal);
        Assert.True(bounds.Single(b => b.Counter == "privateBytes").Pass);
    }

    [Fact]
    public void SixtyMoreHandlesFailTheHandleBound()
    {
        var length = TimeSpan.FromMinutes(20);
        var bounds = SoakBounds.Evaluate(Series(_ => 200 * Mb, t => t >= length ? 460 : 400, length), TimeSpan.FromMinutes(5));

        var handles = Assert.Single(bounds, b => b.Counter == "handles");
        Assert.False(handles.Pass);
        Assert.Contains("handles +60 > 50", handles.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AClipMissingATrackFails()
    {
        var probe = new ClipProbe([Video(29.5), Audio("Everything", 29.5)], null);

        var result = SoakClipCheck.Evaluate(probe, ["Everything", "Mic"], 30, 60);

        Assert.False(result.Pass);
        Assert.Contains(result.Failures, f => f.Contains("Mic", StringComparison.Ordinal));
        Assert.True(SoakClipCheck.Evaluate(probe, ["Everything"], 30, 60).Pass);
    }

    [Fact]
    public void AClipCutShortFails()
    {
        var result = SoakClipCheck.Evaluate(new ClipProbe([Video(3), Audio("Everything", 3)], null), ["Everything"], 30, 60);

        Assert.False(result.Pass);
        Assert.Contains(result.Failures, f => f.StartsWith("video 3 s outside", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScheduleSamplesAndPressesOnTime()
    {
        var time = new ManualTime(T0);
        var schedule = new SoakSchedule(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), time, (span, _) => time.Advance(span));
        var sampled = new List<double>();
        var pressed = new List<double>();

        var stopped = schedule.Run(
            () => sampled.Add((time.GetUtcNow() - T0).TotalSeconds),
            () => pressed.Add((time.GetUtcNow() - T0).TotalSeconds),
            () => null,
            CancellationToken.None);

        Assert.Null(stopped);
        Assert.Equal([0, 10, 20, 30, 40, 50, 60], sampled);
        Assert.Equal([20, 40], pressed);
        Assert.Equal(7, schedule.Samples);
        Assert.Equal(2, schedule.Presses);
    }

    /// <summary>Samples every 10 s for <paramref name="length"/> (20 minutes by default), for one process.</summary>
    private static Dictionary<string, IReadOnlyList<ProcessCounters>> Series(Func<TimeSpan, long> privateBytes, Func<TimeSpan, int> handles, TimeSpan? length = null)
    {
        var samples = new List<ProcessCounters>();
        for (var t = TimeSpan.Zero; t <= (length ?? TimeSpan.FromMinutes(20)); t += TimeSpan.FromSeconds(10))
        {
            samples.Add(new ProcessCounters(T0 + t, 42, "engine", privateBytes(t), 300 * Mb, handles(t), 20, 10));
        }

        return new() { ["engine"] = samples };
    }

    private static ClipStream Video(double seconds) => new("video", null, seconds, (long)(seconds * 60));

    private static ClipStream Audio(string name, double seconds) => new("audio", name, seconds, null);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }
}
