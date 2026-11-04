namespace ReplayBuffer.Bench.Tests;

public sealed class ReportTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("bench-run-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AnEmptyFolderReportsEverythingNotRun()
    {
        var report = Report.Generate(_folder);

        Assert.Equal(["FPS", "Drift", "Playback", "Hotkey", "Named Tracks"], report.NotRun);
        var md = File.ReadAllText(Path.Combine(_folder, Report.MarkdownName));
        Assert.Equal(5, md.Split("Not run.").Length - 1);
        Assert.True(File.Exists(Path.Combine(_folder, Report.JsonName)));
    }

    [Fact]
    public void TheNamedTracksSectionHasARowPerCheck()
    {
        BenchJson.Write(Path.Combine(_folder, TracksCheck.FileName), new TracksReport(
            DateTimeOffset.Now, 5, "engine.exe", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 0,
            "Speakers A", "Speakers B", "Mic", "tracks.mp4", 0, null, 300.01, 0.5,
            [
                new TrackFigures(0, "Everything", true, 300.02, 598, 598, -2.4, 2.02, 0.997),
                new TrackFigures(1, "Game", false, 300.02, 300, 299, -0.04, 1.5, 0.998),
                new TrackFigures(2, "Chat", false, 300.02, 299, 299, 0.02, 1.5, 0.998),
                new TrackFigures(3, "Mic", false, 300.01, null, null, null, null),
            ],
            new SyncFigures(300, 299, 299, 299, 599, 1, 0.08, 0.02),
            [
                new NamedTrackCheck("titles", "Everything, Game, Chat, Mic", "Everything, Game, Chat, Mic", DriftMath.Pass),
                new NamedTrackCheck("sync", "max |Δ| 0.08 ms", "≤ 1 ms", DriftMath.Pass),
                new NamedTrackCheck("vlc", "VLC not found", "the file's titles, in order", TrackAnalysis.NotChecked),
            ],
            null,
            TrackAnalysis.PassVerdict,
            []));

        var report = Report.Generate(_folder);
        var md = Report.Markdown(report);

        Assert.DoesNotContain("Named Tracks", report.NotRun);
        Assert.NotNull(report.Tracks);
        Assert.Contains("## Named Tracks (R3, R7)", md, StringComparison.Ordinal);
        Assert.Contains("| titles | Everything, Game, Chat, Mic | Everything, Game, Chat, Mic | pass |", md, StringComparison.Ordinal);
        Assert.Contains("| sync | max /Δ/ 0.08 ms | ≤ 1 ms | pass |", md, StringComparison.Ordinal);
        Assert.Contains("| vlc | VLC not found | the file's titles, in order | not-checked |", md, StringComparison.Ordinal);
        Assert.Contains("| 1 | Everything | yes | 300.02 s | 598 (expected ≥ 598) | -2.4 dBFS | 0.997 | 2.02 |", md, StringComparison.Ordinal);
        Assert.Contains("| 2 | Game | no | 300.02 s | 300 (expected ≥ 299) | -0.04 dBFS | 0.998 | 1.5 |", md, StringComparison.Ordinal);
        Assert.Contains("Verdict: **pass**", md, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedTracksRunShowsTheEngineError()
    {
        BenchJson.Write(Path.Combine(_folder, TracksCheck.FileName), new TracksReport(
            DateTimeOffset.Now, 5, "engine.exe", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 0,
            "Speakers A", "Speakers B", "Mic", "tracks.mp4", 1, "error audio-device-not-found: gone", null, null,
            [], null, [], null, TrackAnalysis.FailedVerdict, ["Could not delete the test instance's folder C:\\x."]));

        var md = Report.Markdown(Report.Generate(_folder));

        Assert.Contains("**The engine failed** (exit 1): error audio-device-not-found: gone", md, StringComparison.Ordinal);
        Assert.Contains("- Warning: Could not delete the test instance's folder C:\\x.", md, StringComparison.Ordinal);
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
    public void EachRunsPresentModeAndTheOverlayReachTheReport()
    {
        File.WriteAllLines(Path.Combine(_folder, "02-capture-1-presentmon.csv"),
        [
            "Application,ProcessID,PresentMode,MsBetweenPresents",
            "load.exe,1,Composed: Flip,12.5",
            "load.exe,1,Composed: Flip,12.5",
            "load.exe,1,Hardware Composed: Independent Flip,12.5",
        ]);
        var summary = """{ "runs": [ { "run": "01-baseline-1", "condition": "baseline", "pass": 1, "avgFps": 80.0 }, { "run": "02-capture-1", "condition": "capture", "pass": 1, "avgFps": 74.0, "fpsLossPercent": 7.5 } ] }""";
        var overlay = new FpsOverlay("auto", false, FpsBenchmark.MessageSeconds);

        var report = FpsBenchmark.Map(summary, "summary.json", _folder, "Game", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 30, 1, overlay: overlay);
        BenchJson.Write(Path.Combine(_folder, FpsBenchmark.FileName), report);
        var saved = Report.Generate(_folder);

        Assert.Null(saved.Fps!.Runs[0].PresentMode);
        Assert.Equal("Composed: Flip", saved.Fps.Runs[1].PresentMode);
        Assert.Equal(overlay, saved.Fps.Overlay);
        var md = Report.Markdown(saved);
        Assert.Contains("overlayDot `auto`) and a message every 10 s; overlay planes on the load's monitor (SupportsOverlays): no.", md, StringComparison.Ordinal);
        Assert.Contains("|  | Composed: Flip |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void NoGcFiguresAreNotZeroGen2()
    {
        var summary = """{ "runs": [ { "run": "01-capture-1", "condition": "capture", "pass": 1, "avgFps": 98.0, "fpsLossPercent": 2.0 } ] }""";

        var report = FpsBenchmark.Map(summary, "summary.json", _folder, "Game", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 300, 1);

        Assert.Null(report.CaptureMeans!.Gen2Collections);
    }
}
