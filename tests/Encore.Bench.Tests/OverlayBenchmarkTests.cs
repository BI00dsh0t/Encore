using Encore.Bench.GpuLoad;

namespace Encore.Bench.Tests;

/// <c>bench overlay</c> without a GPU: command lines, ready lines, a PresentMon CSV and the report. One test is Heavy.
public sealed class OverlayBenchmarkTests : IDisposable
{
    private readonly BenchRun _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public void TheLoadsModeReachesItsReadyLine()
    {
        Assert.Equal(LoadMode.Exclusive, LoadOptions.Parse(["--mode", "exclusive"]).Mode);
        Assert.Equal(LoadMode.Borderless, LoadOptions.Parse([]).Mode);

        var ready = GpuLoadProcess.ParseReady(ReadyLine.Format(0x1234, 2560, 1440, 80, 80, 4500, "GPU", tearing: false, LoadMode.Fullscreen, fullscreen: true));
        Assert.Equal("fullscreen", ready.Mode);
        Assert.True(ready.Fullscreen);

        var host = OverlayHostProcess.ParseReady("ready 0x2B0E7A excluded=true exstyle=0x80800A8");
        Assert.Equal((nint)0x2B0E7A, host.Hwnd);
        Assert.True(host.Excluded);
        Assert.Equal(0x80800A8u, host.ExtendedStyle);
    }

    [Fact]
    public void APresentMonCsvGivesTheFpsAndThePresentMode()
    {
        var csv = Path.Combine(_run.Folder, "presents.csv");
        File.WriteAllLines(csv,
        [
            "﻿Application,ProcessID,PresentMode,MsBetweenPresents",
            "load.exe,1,Hardware Composed: Independent Flip,12.5",
            "load.exe,1,Hardware Composed: Independent Flip,12.5",
            "load.exe,1,Hardware Composed: Independent Flip,12.5",
            "load.exe,1,Composed: Flip,12.5",
        ]);

        var presents = PresentMonCsv.Analyze(csv);

        Assert.Null(presents.Error);
        Assert.Equal(4, presents.Frames);
        Assert.Equal(80, presents.AvgFps);
        Assert.Equal("Hardware Composed: Independent Flip", presents.DominantMode);
        Assert.Equal(75, presents.ModePercent["Hardware Composed: Independent Flip"]);
    }

    [Fact]
    public void TheSummaryGivesTheDotsCostAndWhetherItCanBeSeen()
    {
        const string Iflip = "Hardware Composed: Independent Flip";
        const string Legacy = "Hardware: Legacy Flip";
        var borderless = new[] { Phase("borderless", "off", 80, Iflip, 2), Phase("borderless", "on", 79, Iflip, 2), Phase("borderless", "off-again", 80, Iflip, 2) };
        var exclusive = new[] { Phase("exclusive", "off", 80, Legacy, 3), Phase("exclusive", "on", 80, Legacy, 3), Phase("exclusive", "off-again", 80, Legacy, 3) };

        var flip = OverlayBenchmark.Summarize("borderless", borderless, []);
        var legacy = OverlayBenchmark.Summarize("exclusive", exclusive, []);

        Assert.Equal(1.25, flip.FpsCostPercent);
        Assert.True(flip.IndependentFlipKept);
        Assert.True(flip.OverlayShows);
        Assert.False(flip.FullscreenD3D);
        Assert.False(legacy.OverlayShows);
        Assert.True(legacy.FullscreenD3D);
        Assert.True(legacy.ExclusiveKept);
        Assert.True(OverlayBenchmark.Summarize("exclusive", exclusive, ["load: rebuilt the buffers"]).ExclusiveKept);
        Assert.False(OverlayBenchmark.Summarize("exclusive", exclusive, ["20:17:31.250 fullscreen false"]).ExclusiveKept);

        var report = new OverlayReport(DateTimeOffset.Now, "app", RemoteSession: true, 60, 10, [], null, [.. borderless, .. exclusive], [flip, legacy], null);
        var markdown = OverlayBenchmark.Markdown(report);
        Assert.Contains(OverlayBenchmark.RemoteLabel, markdown, StringComparison.Ordinal);
        Assert.Contains("| borderless | Hardware Composed: Independent Flip | Hardware Composed: Independent Flip |", markdown, StringComparison.Ordinal);
        Assert.Contains("Overlay host: app.", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--host", "foo")]
    [InlineData("--host", "app", "--overlay-exe", "x.exe")]
    [InlineData("--app-exe", "x.exe")]
    [InlineData("--host", "app", "--settle", "3")]
    [InlineData("--overlay-dot", "always")]
    public void ABadHostIsAUsageError(params string[] args)
    {
        var context = _run.Context(["overlay", .. args]);

        Assert.Throws<BenchUsageException>(() => OverlayBenchmark.Run(context));
    }

    /// <summary>The real App's Overlay over the legacy exclusive load: needs the GPU, PresentMon and the console.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void TheRealAppsOverlayKeepsTheExclusiveLoadInFullscreen()
    {
        var context = _run.Context("overlay", "--host", "app", "--modes", "exclusive", "--seconds", "10", "--settle", "5");

        var exit = OverlayBenchmark.Run(context);

        var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_run.Folder, OverlayBenchmark.FileName))).RootElement;
        var markdown = File.ReadAllText(Path.Combine(_run.Folder, OverlayBenchmark.MarkdownName));
        Assert.True(exit == 0, markdown);
        Assert.True(report.GetProperty("summaries")[0].GetProperty("exclusiveKept").GetBoolean(), markdown);
        var phases = report.GetProperty("phases").EnumerateArray().ToList();
        Assert.All(phases, p => Assert.Equal("Hardware: Legacy Flip", p.GetProperty("presentMode").GetString()));
        Assert.Equal([false, true, false], phases.Select(p => p.GetProperty("overlayWindowVisible").GetBoolean()));
    }

    /// <c>bench fps --overlay</c> over the synthetic load with the real App; needs the GPU, PresentMon and the console.
    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void FpsWithTheOverlayOnRunsTheLoadWithTheAppsOverlay()
    {
        var context = _run.Context("fps", "--overlay", "--seconds", "10", "--passes", "1", "--settle", "3");

        var exit = FpsBenchmark.Run(context);

        Assert.Equal(0, exit);
        var overlay = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_run.Folder, FpsBenchmark.FileName))).RootElement.GetProperty("overlay");
        Assert.Equal("auto", overlay.GetProperty("dot").GetString());
        Assert.Equal(FpsBenchmark.MessageSeconds, overlay.GetProperty("messageSeconds").GetInt32());
        Assert.True(overlay.TryGetProperty("overlayPlanes", out _));
    }

    private static OverlayPhase Phase(string mode, string phase, double fps, string presentMode, int quns)
    {
        var shown = phase == "on";
        return new OverlayPhase(
            mode, phase, shown, 100, fps, fps - 10, presentMode, new Dictionary<string, double> { [presentMode] = 100 }, quns,
            OverlayBenchmark.NotificationStateName(quns), true, shown, OverlayBenchmark.OnScreen(shown, presentMode), null);
    }
}
