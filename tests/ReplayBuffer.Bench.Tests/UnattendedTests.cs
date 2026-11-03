using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReplayBuffer.Bench.GpuLoad;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Bench.Tests;

/// <summary>
/// The pieces of the unattended runs that need no process: the new options, the GPU load's ready line and
/// calibration, <c>fps.json</c> with the load, the injected key batch, the scripted players' results and the
/// capture-border grab's arithmetic.
/// </summary>
public sealed class UnattendedTests : IDisposable
{
    private readonly BenchRun _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public void TheNewOptionsParse()
    {
        var fps = BenchOptions.Parse(["fps", "--target-fps", "90", "--load-size", "1280x720", "--load-timeout", "30", "--load-warmup", "5", "--load-exe", "x.exe"]);
        Assert.Equal(90, fps.Double("target-fps", 80, 1, 1000));
        Assert.Equal("1280x720", fps.Text("load-size"));
        Assert.Equal(30, fps.Int("load-timeout", 60, 5, 600));
        Assert.Equal("x.exe", fps.Text("load-exe"));

        Assert.True(BenchOptions.Parse(["hotkey", "--inject"]).Flag("inject"));
        Assert.False(BenchOptions.Parse(["hotkey"]).Flag("inject"));

        var playback = BenchOptions.Parse(["playback", "--auto", "--chrome", "c.exe", "--play-seconds", "3", "--player-timeout", "20"]);
        Assert.True(playback.Flag("auto"));
        Assert.Equal(3, playback.Int("play-seconds", 5, 1, 120));
    }

    [Theory]
    [InlineData("hotkey", "--auto")]
    [InlineData("playback", "--inject")]
    [InlineData("drift", "--target-fps", "80")]
    [InlineData("hotkey", "--inject", "--inject")]
    public void NewOptionsAreRefusedWhereTheyDontApply(params string[] args) =>
        Assert.Throws<BenchUsageException>(() => BenchOptions.Parse(args));

    [Theory]
    [InlineData("--target-fps", "80")]
    [InlineData("--load-size", "1280x720")]
    [InlineData("--load-exe", "x.exe")]
    public void LoadOptionsAreRefusedWithARealGame(string name, string value) =>
        Assert.Throws<BenchUsageException>(() => FpsBenchmark.Run(_run.Context("fps", "--game", "Cyberpunk2077", name, value), TextWriter.Null));

    [Fact]
    public void ABadLoadSizeIsRefused() =>
        Assert.Throws<BenchUsageException>(() => FpsBenchmark.Run(_run.Context("fps", "--load-size", "1921x1080"), TextWriter.Null));

    [Theory]
    [InlineData("--chrome", "c.exe")]
    [InlineData("--play-seconds", "3")]
    public void AutoOptionsAreRefusedWithoutAuto(string name, string value) =>
        Assert.Throws<BenchUsageException>(() => PlaybackCheck.Run(_run.Context("playback", name, value)));

    [Fact]
    public void TheReadyLineRoundTrips()
    {
        var line = ReadyLine.Format(0x2D0BD4, 2560, 1440, 80, 80.537, 6132, "NVIDIA GeForce RTX 5090", tearing: true);

        var ready = GpuLoadProcess.ParseReady(line);

        Assert.Equal((nint)0x2D0BD4, ready.Hwnd);
        Assert.Equal("0x2D0BD4", ready.HwndText);
        Assert.Equal(2560, ready.Width);
        Assert.Equal(1440, ready.Height);
        Assert.Equal(80, ready.TargetFps);
        Assert.Equal(80.54, ready.CalibratedFps);
        Assert.Equal(6132, ready.Iterations);
        Assert.Equal("NVIDIA GeForce RTX 5090", ready.Adapter);
        Assert.True(ready.Tearing);
    }

    [Theory]
    [InlineData("calibrating")]
    [InlineData("ready not json")]
    [InlineData("""ready {"hwnd":"0x10","width":1,"height":1,"targetFps":80,"calibratedFps":80,"iterations":1}""")]
    [InlineData("""ready {"hwnd":"16","width":1,"height":1,"targetFps":80,"calibratedFps":80,"iterations":1,"adapter":"a"}""")]
    [InlineData("""ready {"hwnd":"0x0","width":1,"height":1,"targetFps":80,"calibratedFps":80,"iterations":1,"adapter":"a"}""")]
    public void ABadReadyLineIsAFormatError(string line) =>
        Assert.Throws<FormatException>(() => GpuLoadProcess.ParseReady(line));

    [Fact]
    public void TheLoadsOptionsParse()
    {
        var options = LoadOptions.Parse(["--target-fps", "78", "--parent", "1234", "--size", "1280x720", "--warmup", "3"]);

        Assert.Equal(new LoadOptions(78, 1234, 1280, 720, 3), options);
        Assert.Equal(new LoadOptions(80, null, null, null, 10), LoadOptions.Parse([]));
        Assert.Throws<LoadUsageException>(() => LoadOptions.Parse(["--size", "big"]));
        Assert.Throws<LoadUsageException>(() => LoadOptions.Parse(["--parent", "0"]));
        Assert.Throws<LoadUsageException>(() => LoadOptions.Parse(["--target-fps"]));
        Assert.Throws<LoadUsageException>(() => LoadOptions.Parse(["--nope", "1"]));
    }

    [Theory]
    [InlineData(0.05, 0.0004)]
    [InlineData(1.0, 0.00002)]
    [InlineData(0.2, 0.01)]
    public void CalibrationSettlesNearTheTarget(double fixedMs, double msPerIteration)
    {
        // A GPU whose frame time is a fixed cost plus a cost per iteration.
        var calibrator = new Calibrator(80);
        double Fps(uint n) => 1000 / (fixedMs + (msPerIteration * n));
        for (var window = 0; window < 20; window++)
        {
            calibrator.Next(Fps(calibrator.Iterations));
        }

        Assert.InRange(Fps(calibrator.Iterations), 76, 84);
    }

    [Fact]
    public void ACalibrationStepIsBounded()
    {
        var calibrator = new Calibrator(80, 1000);

        Assert.Equal(4000u, calibrator.Next(10_000));
        Assert.Equal(1000u, calibrator.Next(1));
        Assert.Equal(1000u, calibrator.Next(double.NaN));
    }

    [Fact]
    public void FpsJsonCarriesTheLoad()
    {
        var summary = """{ "runs": [ { "run": "01-baseline-1", "condition": "baseline", "pass": 1, "avgFps": 80.0 }, { "run": "02-capture-1", "condition": "capture", "pass": 1, "avgFps": 79.0, "fpsLossPercent": 1.25 } ] }""";
        var load = new GpuLoadInfo(GpuLoadInfo.Synthetic, GpuLoadProcess.ProcessName, 2560, 1440, 80, 80.54, 6132, "NVIDIA GeForce RTX 5090", true);

        var report = FpsBenchmark.Map(summary, "summary.json", _run.Folder, GpuLoadProcess.ProcessName, new CaptureSettings(1920, 1080, 60, 20_000, null, null), 30, 1, load);
        BenchJson.Write(_run.PathOf(FpsBenchmark.FileName), report);
        var saved = Report.Generate(_run.Folder);

        Assert.Equal(load, saved.Fps!.Load);
        Assert.Equal(DriftMath.Pass, saved.Fps.Verdict);
        var md = Report.Markdown(saved);
        Assert.Contains("**Load: Bench's synthetic GPU load** (`ReplayBuffer.Bench.GpuLoad`), a borderless 2560x1440 popup on NVIDIA GeForce RTX 5090: target 80 fps, calibrated 80.54 fps at 6132 shader iterations, tearing on.", md, StringComparison.Ordinal);
        Assert.Contains("| FPS loss, capture against baseline | 1.25 % | < 3 % | pass |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderFpsJsonWithoutTheLoadStillReads()
    {
        var report = FpsBenchmark.Map("""{ "runs": [] }""", "summary.json", _run.Folder, "Cyberpunk2077", new CaptureSettings(1920, 1080, 60, 20_000, null, null), 300, 3);
        var json = JsonSerializer.Serialize(report, BenchJson.Options).Replace(",\n  \"load\": null", "", StringComparison.Ordinal).Replace(",\r\n  \"load\": null", "", StringComparison.Ordinal);
        Assert.DoesNotContain("\"load\"", json, StringComparison.Ordinal);
        File.WriteAllText(_run.PathOf(FpsBenchmark.FileName), json);

        var saved = Report.Generate(_run.Folder);

        Assert.Empty(saved.Errors);
        Assert.Null(saved.Fps!.Load);
    }

    [Fact]
    public void AnAbortedScriptOrNoCaptureDataIsNotAMeasurement()
    {
        var settings = new CaptureSettings(1920, 1080, 60, 20_000, null, null);
        FpsReport Mapped(string summary) => FpsBenchmark.Map(summary, "summary.json", _run.Folder, "game", settings, 30, 1);

        var aborted = Mapped("""{ "aborted": "The engine exited early. Skipped the remaining 2 run(s).", "runs": [ { "run": "01-baseline-1", "condition": "baseline", "pass": 1, "avgFps": 80.0 } ] }""");
        var noCapture = Mapped("""{ "runs": [ { "run": "01-baseline-1", "condition": "baseline", "pass": 1, "avgFps": 80.0 }, { "run": "02-capture-1", "condition": "capture", "pass": 1 } ] }""");
        var measured = Mapped("""{ "runs": [ { "run": "01-baseline-1", "condition": "baseline", "pass": 1, "avgFps": 80.0 }, { "run": "02-capture-1", "condition": "capture", "pass": 1, "avgFps": 79.0 } ] }""");

        Assert.Equal("measure-path-a.ps1 aborted: The engine exited early. Skipped the remaining 2 run(s).", FpsBenchmark.Unmeasured(aborted));
        Assert.Equal("No capture run has a frame rate; nothing was measured.", FpsBenchmark.Unmeasured(noCapture));
        Assert.Null(FpsBenchmark.Unmeasured(measured));
    }

    [Fact]
    public void TheEngineIsNeverTheNativeAotIntermediate()
    {
        var repo = _run.Folder;
        var tfm = Path.Combine(repo, "src", "ReplayBuffer.Engine", "bin", "Release", "net10.0-windows10.0.26100.0");
        var built = Touch(Path.Combine(tfm, "ReplayBuffer.Engine.exe"), 1);
        var published = Touch(Path.Combine(repo, "publish", "engine", "ReplayBuffer.Engine.exe"), 2);

        // publish-engine.ps1 leaves the compiler's output, newest of all, with no FFmpeg beside it.
        Touch(Path.Combine(tfm, "win-x64", "native", "ReplayBuffer.Engine.exe"), 3);
        Touch(Path.Combine(tfm, "win-x64", "ReplayBuffer.Engine.exe"), 3);

        Assert.Equal(published, BenchContext.FindEngine(repo));

        // A Release build newer than the publish wins.
        File.SetLastWriteTimeUtc(built, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(built, BenchContext.FindEngine(repo));

        File.Delete(built);
        File.Delete(published);
        Assert.Null(BenchContext.FindEngine(repo));
    }

    [Fact]
    public void AnInjectedRunThatTimedNoPressIsANamedFailure()
    {
        var timeout = TimeSpan.FromSeconds(60);

        var fault = HotkeyBenchmark.InjectFault(inject: true, null, 0, cancelled: false, timeout);

        Assert.Equal("no injected press reached the engine within 60 s: SendInput may be blocked by UIPI or a secure desktop", fault);
        Assert.Equal(DriftMath.Inconclusive, HotkeyBenchmark.Result(KeyCombo.Parse("ctrl+alt+f9"), 20, [], 0, fault, injected: true).Verdict);

        // Pressed by hand, cancelled, already failing, or timed: no new failure.
        Assert.Null(HotkeyBenchmark.InjectFault(inject: false, null, 0, cancelled: false, timeout));
        Assert.Null(HotkeyBenchmark.InjectFault(inject: true, null, 0, cancelled: true, timeout));
        Assert.Equal("SendInput failed", HotkeyBenchmark.InjectFault(inject: true, "SendInput failed", 0, cancelled: false, timeout));
        Assert.Null(HotkeyBenchmark.InjectFault(inject: true, null, 1, cancelled: false, timeout));
    }

    /// <summary>An empty file, last written <paramref name="order"/> minutes after a fixed past time: higher is newer.</summary>
    private static string Touch(string path, int order)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(order - 10));
        return path;
    }

    [Fact]
    public void AnInjectedPressIsTheModifiersAroundTheKey()
    {
        var batch = KeyInjector.Batch(KeyCombo.Parse("ctrl+alt+f9"));

        Assert.Equal(40, Marshal.SizeOf<KeyInjector.Input>());
        Assert.Equal([0x11, 0x12, 0x78, 0x78, 0x12, 0x11], batch.Select(i => (int)i.Keyboard.Vk));
        Assert.Equal([false, false, false, true, true, true], batch.Select(i => (i.Keyboard.Flags & 2) != 0));
        Assert.All(batch, i => Assert.Equal(1u, i.Type));
    }

    [Fact]
    public void AnInjectedRunIsMarkedInTheReport()
    {
        var combo = KeyCombo.Parse("ctrl+alt+f9");
        var report = HotkeyBenchmark.Result(combo, 2, [new(1, 0.4, true), new(2, 0.6, true)], 0, null, injected: true);
        BenchJson.Write(_run.PathOf(HotkeyBenchmark.FileName), report);

        var md = Report.Markdown(Report.Generate(_run.Folder));

        Assert.True(report.Injected);
        Assert.Equal(DriftMath.Pass, report.Verdict);
        Assert.Contains("**Presses injected** by Bench with `SendInput`", md, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptedResultsParse()
    {
        var chrome = PlayerScripts.Parse("Chrome", """{"width":1920,"height":1080,"durationSeconds":30.1,"audioTracks":2,"trackLabels":["Game","Mic"],"playedSeconds":4.98,"wallSeconds":5.0,"framesDecoded":300,"framesDropped":0}""");
        Assert.True(chrome.Plays);
        Assert.Equal(2, chrome.AudioTracks);
        Assert.Equal(["Game", "Mic"], chrome.TrackLabels!);
        Assert.Null(chrome.Error);

        var stalled = PlayerScripts.Parse("Windows Media Player", """{"playedSeconds":0.5,"wallSeconds":5.0}""");
        Assert.False(stalled.Plays);

        var refused = PlayerScripts.Parse("Chrome", """{"error":"media error 4: unsupported","errorKind":"media"}""");
        Assert.False(refused.Plays);
        Assert.Equal("media error 4: unsupported", refused.Error);

        var stuck = PlayerScripts.Parse("Windows Media Player", """{"error":"it did not start playing within 20 s (playState 9)","errorKind":"timeout"}""");
        Assert.Null(stuck.Plays);

        var missing = PlayerScripts.Parse("Windows Media Player", """{"error":"WMPlayer.OCX is not available","errorKind":"setup"}""");
        Assert.Null(missing.Plays);

        var empty = PlayerScripts.Parse("Chrome", "{}");
        Assert.Null(empty.Plays);
        Assert.NotNull(empty.Error);

        var garbage = PlayerScripts.Parse("Chrome", "not json");
        Assert.Null(garbage.Plays);
        Assert.StartsWith("unreadable result", garbage.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptedChecksAreMarkedForWhatTheySaw()
    {
        var plays = PlayerScripts.ToCheck(PlayerScripts.Parse("Chrome", """{"width":1920,"height":1080,"audioTracks":2,"playedSeconds":5,"wallSeconds":5}"""));
        var refused = PlayerScripts.ToCheck(PlayerScripts.Parse("Windows Media Player", """{"error":"it never started playing","errorKind":"media"}"""));
        var broken = PlayerScripts.ToCheck(PlayerScripts.Parse("Windows Media Player", """{"error":"timed out","errorKind":"setup"}"""));
        Assert.Equal("checked", plays.Status);
        Assert.Null(plays.AvInSync);
        Assert.Equal("2 listed", plays.AudioTracks);
        Assert.Equal(PlayerScripts.Scripted, plays.Method);
        Assert.Equal("not-checked", broken.Status);

        var players = new List<PlayerCheck> { plays, refused, broken, PlayerScripts.NeedsPerson("Discord") };
        BenchJson.Write(_run.PathOf(PlaybackCheck.FileName), new PlaybackReport(
            DateTimeOffset.Now, "clip.mp4", 30, new CaptureSettings(1920, 1080, 60, 20_000, null, null), 2, "no", players, [], true,
            new BorderProbeResult("no", 40_000, 0, 3, "before.png", "during.png", null)));

        var report = Report.Generate(_run.Folder);
        var md = Report.Markdown(report);

        Assert.Empty(report.Errors);
        Assert.Contains("| Chrome | yes |  | 2 listed | scripted: advanced 5 s in 5 s; 1920x1080; A/V sync needs a person | plays: pass; A/V sync: not-checked |", md, StringComparison.Ordinal);
        Assert.Contains("| Windows Media Player | no |  | not listed by the player | scripted: refused the file: it never started playing; A/V sync needs a person | miss |", md, StringComparison.Ordinal);
        Assert.Contains("| Discord |  |  |  | needs a person | not-checked |", md, StringComparison.Ordinal);
        Assert.Contains("0 yellow pixels before the capture, 3 during it, of 40000", md, StringComparison.Ordinal);
        Assert.Contains("An unattended `--auto` run", md, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayersWithNoScriptNeedAPerson()
    {
        var checks = PlaybackCheck.Scripted(["Discord", "VLC", "DaVinci Resolve"], "clip.mp4", _run.Folder, null, 5, TimeSpan.FromSeconds(10));

        Assert.All(checks, c =>
        {
            Assert.Equal("not-checked", c.Status);
            Assert.Equal("needs a person", c.Notes);
        });
    }

    [Fact]
    public void AMissingChromeIsNotChecked()
    {
        var checks = PlaybackCheck.Scripted(["Chrome"], "clip.mp4", _run.Folder, _run.PathOf("no-chrome.exe"), 5, TimeSpan.FromSeconds(10));

        Assert.Equal("not-checked", checks[0].Status);
        Assert.Contains("Chrome is not at", checks[0].Notes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bytes=0-", 0L, 999L)]
    [InlineData("bytes=100-199", 100L, 199L)]
    [InlineData("bytes=900-5000", 900L, 999L)]
    public void RangesParse(string header, long from, long to) =>
        Assert.Equal((from, to), PlayerScripts.ParseRange(header, 1000));

    [Theory]
    [InlineData(null)]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=-500")]
    [InlineData("bytes=500-100")]
    [InlineData("items=0-1")]
    public void BadRangesAreTheWholeFile(string? header) =>
        Assert.Null(PlayerScripts.ParseRange(header, 1000));

    [Fact]
    public void AYellowBandRoundTheWindowIsABorder()
    {
        const int Width = 100, Height = 80, Margin = 12;
        var window = new Rectangle(Margin, Margin, Width - (2 * Margin), Height - (2 * Margin));
        var plain = new int[Width * Height];
        var bordered = new int[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var onEdge = (x is >= Margin - 2 and < Margin || x >= window.Right && x < window.Right + 2) && y >= Margin - 2 && y < window.Bottom + 2
                    || (y is >= Margin - 2 and < Margin || y >= window.Bottom && y < window.Bottom + 2) && x >= Margin - 2 && x < window.Right + 2;
                plain[(y * Width) + x] = unchecked((int)0xFF000000);
                bordered[(y * Width) + x] = onEdge ? unchecked((int)0xFFFFD800) : unchecked((int)0xFF000000);
            }
        }

        var (before, ring) = BorderProbe.CountRing(plain, Width, window, Margin);
        var (during, _) = BorderProbe.CountRing(bordered, Width, window, Margin);

        Assert.Equal(0, before);
        Assert.True(during > 400, $"{during} yellow");
        Assert.Equal("yes", BorderProbe.Classify(ring, before, during));
        Assert.Equal("no", BorderProbe.Classify(ring, before, before));
        Assert.False(BorderProbe.IsYellow(255, 255, 255));
        Assert.True(BorderProbe.IsYellow(255, 216, 0));
    }
}
