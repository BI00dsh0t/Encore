namespace ReplayBuffer.Bench.Tests;

public sealed class ReportTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("bench-run-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AnEmptyFolderReportsEverythingNotRun()
    {
        var report = Report.Generate(_folder);

        Assert.Equal(["FPS", "Drift", "Playback", "Hotkey"], report.NotRun);
        var md = File.ReadAllText(Path.Combine(_folder, Report.MarkdownName));
        Assert.Equal(4, md.Split("Not run.").Length - 1);
        Assert.True(File.Exists(Path.Combine(_folder, Report.JsonName)));
    }

    [Fact]
    public void AMalformedFileIsNamedAndSkipped()
    {
        File.WriteAllText(Path.Combine(_folder, DriftBenchmark.FileName), "{ not json");
        BenchJson.Write(Path.Combine(_folder, HotkeyBenchmark.FileName), HotkeyBenchmark.Result(
            KeyCombo.Parse("ctrl+alt+f9"),
            3,
            [new HotkeyPressTiming(1, 0.4, false), new HotkeyPressTiming(2, 0.6, false), new HotkeyPressTiming(3, 2.5, false)],
            0,
            null));

        var report = Report.Generate(_folder);

        Assert.Contains(report.Errors, e => e.Contains(DriftBenchmark.FileName, StringComparison.Ordinal));
        Assert.DoesNotContain("Hotkey", report.NotRun);
        Assert.NotNull(report.Hotkey);
        Assert.Equal(DriftMath.Pass, report.Hotkey.Verdict);
        Assert.Equal(0.6, report.Hotkey.P50Ms);
        Assert.Equal(2.5, report.Hotkey.P99Ms);

        var md = File.ReadAllText(Path.Combine(_folder, Report.MarkdownName));
        Assert.Contains("## Skipped files", md, StringComparison.Ordinal);
        Assert.Contains("| p50 | 0.6 ms | < 1000 ms; old bar 1 ms | pass; old bar met |", md, StringComparison.Ordinal);
        Assert.Contains("| p99 | 2.5 ms | < 1000 ms; old bar 1 ms | pass; old bar not met |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void APressOverASecondIsAMiss()
    {
        var result = HotkeyBenchmark.Result(KeyCombo.Parse("f9"), 2, [new HotkeyPressTiming(1, 0.5, false), new HotkeyPressTiming(2, 1200, false)], 0, null);

        Assert.Equal(DriftMath.Miss, result.Verdict);
        Assert.Equal(1200, result.MaxMs);
    }

    [Fact]
    public void NoPressesAreInconclusive()
    {
        var result = HotkeyBenchmark.Result(KeyCombo.Parse("f9"), 20, [], 0, "hotkey-taken: another process holds f9");

        Assert.Equal(DriftMath.Inconclusive, result.Verdict);
        Assert.Null(result.P50Ms);
    }

    [Fact]
    public void TheFpsSummaryIsMappedWithTheRunsSlotFigures()
    {
        var fps = Path.Combine(_folder, "fps");
        Directory.CreateDirectory(fps);
        File.WriteAllText(Path.Combine(fps, "02-capture-1.json"), """{ "video": { "framesEncoded": 1000, "freshSlots": 950, "skippedSlots": 1 } }""");
        var summary =
            """
            {
              "aborted": null,
              "byCondition": [],
              "runs": [
                { "run": "01-baseline-1", "condition": "baseline", "pass": 1, "avgFps": 100.0, "onePercentLowFps": 80.0, "fpsLossPercent": 0.0 },
                { "run": "02-capture-1", "condition": "capture", "pass": 1, "avgFps": 98.0, "onePercentLowFps": 76.0, "fpsLossPercent": 2.0,
                  "latencyP99Ms": 6.5, "cpuPercentOneCore": 1.5, "allocBytesPerSecond": 0, "gcSteady": "0/0/0", "duplicatePercent": 5.0 },
                { "run": "03-capture-final", "condition": "capture", "pass": "final", "avgFps": 50.0, "fpsLossPercent": 50.0 }
              ]
            }
            """;

        var report = FpsBenchmark.Map(summary, "summary.json", fps, "Game", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 300, 1);

        Assert.Equal(DriftMath.Pass, report.Verdict);
        Assert.Equal(100, report.BaselineAvgFps);
        Assert.Equal(2, report.CaptureMeans!.FpsLossPercent);
        Assert.Equal(5, report.CaptureMeans.OnePercentLowLossPercent);
        Assert.Equal(95, report.CaptureMeans.FreshSlotPercent);
        Assert.Equal(0, report.CaptureMeans.Gen2Collections);
        Assert.Equal(950, report.Runs[1].FreshSlots);
        Assert.Equal("final", report.Runs[2].Pass);

        BenchJson.Write(Path.Combine(_folder, FpsBenchmark.FileName), report);
        var md = Report.Markdown(Report.Generate(_folder));
        Assert.Contains("| FPS loss, capture against baseline | 2 % | < 3 % | pass |", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DriftBenchmark.FileName, "{}")]
    [InlineData(PlaybackCheck.FileName, """{ "clip": "c.mp4", "capture": { "width": 2, "height": 2, "fps": 60, "bitrateKbps": 1 }, "players": [ { "status": "checked" } ], "warnings": [] }""")]
    public void ValidJsonOfTheWrongShapeIsNamedAndSkipped(string file, string json)
    {
        File.WriteAllText(Path.Combine(_folder, file), json);

        var report = Report.Generate(_folder);

        Assert.Contains(report.Errors, e => e.Contains(file, StringComparison.Ordinal));
        Assert.Null(report.Drift);
        Assert.Null(report.Playback);
        Assert.Contains("## Skipped files", Report.Markdown(report), StringComparison.Ordinal);
    }

    [Fact]
    public void ASkippedSyncAnswerIsNotAPass()
    {
        var players = new List<PlayerCheck>
        {
            new("Chrome", "checked", true, null, "both", null),
            new("VLC", "checked", true, false, "both", null),
        };
        BenchJson.Write(Path.Combine(_folder, PlaybackCheck.FileName), new PlaybackReport(
            DateTimeOffset.Now, "clip.mp4", 30, new CaptureSettings(1920, 1080, 60, 20_000, null, null), 2, "no", players, []));

        var md = Report.Markdown(Report.Generate(_folder));

        Assert.Contains("| Chrome | yes |  | both |  | not-checked |", md, StringComparison.Ordinal);
        Assert.Contains("| VLC | yes | no | both |  | miss |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void NoGcFiguresAreNotZeroGen2()
    {
        var summary = """{ "runs": [ { "run": "01-capture-1", "condition": "capture", "pass": 1, "avgFps": 98.0, "fpsLossPercent": 2.0 } ] }""";

        var report = FpsBenchmark.Map(summary, "summary.json", _folder, "Game", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 300, 1);

        Assert.Null(report.CaptureMeans!.Gen2Collections);
    }
}
