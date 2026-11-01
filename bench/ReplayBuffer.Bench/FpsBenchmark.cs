using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ReplayBuffer.Bench;

/// <summary>One run of the FPS script, with the engine's slot figures from its stats file.</summary>
internal sealed record FpsRun(
    string Run,
    string Condition,
    string Pass,
    double? AvgFps,
    double? OnePercentLowFps,
    double? FpsLossPercent,
    long? FramesEncoded,
    long? FreshSlots,
    double? FreshSlotPercent,
    long? SkippedSlots,
    long? QueueDrops,
    double? DuplicatePercent,
    double? LatencyP99Ms,
    double? CpuPercentOneCore,
    double? AllocBytesPerSecond,
    string? GcSteady);

/// <summary>The capture condition's means over its runs.</summary>
internal sealed record FpsCapture(
    double? AvgFps,
    double? OnePercentLowFps,
    double? FpsLossPercent,
    double? OnePercentLowLossPercent,
    double? FreshSlotPercent,
    double? DuplicatePercent,
    double? LatencyP99Ms,
    double? CpuPercentOneCore,
    double? AllocBytesPerSecond,
    long? Gen2Collections);

/// <summary><c>fps.json</c>.</summary>
/// <param name="Load">What was measured: the synthetic load and its calibration, or the real game; null in a file written before it was recorded.</param>
internal sealed record FpsReport(
    DateTimeOffset Created,
    string Game,
    CaptureSettings Capture,
    int Seconds,
    int Passes,
    string Summary,
    string? Aborted,
    double? BaselineAvgFps,
    double? BaselineOnePercentLowFps,
    FpsCapture? CaptureMeans,
    IReadOnlyList<FpsRun> Runs,
    double TargetLossPercent,
    string Verdict,
    GpuLoadInfo? Load = null);

/// <summary>
/// <c>bench fps</c>: <c>tools/measure-path-a.ps1</c> runs baseline against capture in balanced passes, at
/// the output size and rate; Bench maps its <c>summary.json</c> and each capture run's stats file into <c>fps.json</c>.
/// Without <c>--game</c> it measures Bench's own synthetic GPU load (<see cref="GpuLoadProcess"/>), which the
/// script finds and PresentMon traces by its process name, like a game; Bench stops it on every path.
/// </summary>
internal static class FpsBenchmark
{
    public const string FileName = "fps.json";
    public const double TargetLossPercent = 3;

    /// <summary>The options of the synthetic load, refused with <c>--game</c>.</summary>
    private static readonly string[] LoadOptionNames = ["target-fps", "load-size", "load-timeout", "load-warmup", "load-exe"];

    public static int Run(BenchContext context, TextWriter? errors = null)
    {
        var options = context.Options;
        var realGame = options.Text("game");
        if (realGame is not null && LoadOptionNames.FirstOrDefault(options.Has) is { } loadOption)
        {
            throw new BenchUsageException($"--{loadOption} applies to the synthetic load only, not with --game.");
        }

        var targetFps = options.Double("target-fps", GpuLoadProcess.DefaultTargetFps, 1, 1000);
        var loadSize = options.Text("load-size");
        if (loadSize is not null)
        {
            BenchOptions.ParseSize(loadSize, "load-size");
        }

        var loadTimeout = TimeSpan.FromSeconds(options.Int("load-timeout", 60, 5, 600));
        var loadWarmup = options.Has("load-warmup") ? options.Double("load-warmup", 10, 1, 600) : (double?)null;
        var game = realGame ?? GpuLoadProcess.ProcessName;
        var seconds = options.Int("seconds", 300, 10, 7200);
        var passes = options.Int("passes", 3, 1, 20);
        var settle = options.Int("settle", 20, 0, 600);
        var settings = options.CaptureSettings();
        var pwsh = context.ResolvePwsh();
        var presentMon = context.ResolvePresentMon();
        var engine = context.ResolveEngine();
        var loadExe = realGame is null ? GpuLoadProcess.Resolve(context) : null;
        var folder = Path.Combine(context.RunFolder, "fps");
        var script = Path.Combine(context.RepoRoot, "tools", "measure-path-a.ps1");

        var args = new List<string>
        {
            "-NoProfile", "-File", script,
            "-Game", game,
            "-Conditions", "baseline,capture",
            "-Seconds", seconds.ToString(CultureInfo.InvariantCulture),
            "-Repeats", passes.ToString(CultureInfo.InvariantCulture),
            "-SettleSeconds", settle.ToString(CultureInfo.InvariantCulture),
            "-Size", settings.Size,
            "-Fps", settings.Fps.ToString(CultureInfo.InvariantCulture),
            "-Bitrate", settings.BitrateKbps.ToString(CultureInfo.InvariantCulture),
            "-PresentMon", presentMon,
            "-Engine", engine,
            "-OutDir", folder,
        };
        if (settings.OutputDevice is { } output)
        {
            args.AddRange(["-OutputDevice", output]);
        }

        if (settings.InputDevice is { } input)
        {
            args.AddRange(["-InputDevice", input]);
        }

        if (options.Flag("no-gpu-sampler"))
        {
            args.Add("-NoGpuSampler");
        }

        errors ??= Console.Error;
        GpuLoadInfo loadInfo;
        GpuLoadProcess? load = null;
        try
        {
            if (loadExe is not null)
            {
                try
                {
                    load = GpuLoadProcess.Start(loadExe, targetFps, loadSize, loadWarmup, loadTimeout);
                }
                catch (BenchFailure e)
                {
                    errors.WriteLine(e.Message);
                    return 1;
                }

                loadInfo = load.Info;
            }
            else
            {
                loadInfo = GpuLoadInfo.RealGame(game);
            }

            Console.WriteLine($"FPS: {pwsh} {string.Join(' ', args)}");

            // The script's stdout streams to the console; its stderr (where its refusals go) is relayed line by
            // line to errors, so the message reaches whoever runs Bench.
            var start = new ProcessStartInfo(pwsh) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start) ?? throw new BenchFailure($"Could not start {pwsh}.");
            string? line;
            while ((line = process.StandardError.ReadLine()) is not null)
            {
                errors.WriteLine(line);
            }

            process.WaitForExit();
            if (load is { HasExited: true })
            {
                errors.WriteLine("The GPU load exited during the run.");
            }

            if (process.ExitCode != 0)
            {
                errors.WriteLine($"measure-path-a.ps1 exited with {process.ExitCode}.");
                return 1;
            }
        }
        finally
        {
            // Never leave the load running, whatever happened.
            load?.Dispose();
        }

        var summary = Path.Combine(folder, "summary.json");
        if (!File.Exists(summary))
        {
            throw new BenchFailure($"measure-path-a.ps1 wrote no {summary}.");
        }

        var report = Map(File.ReadAllText(summary), summary, folder, game, settings, seconds, passes, loadInfo);
        BenchJson.Write(Path.Combine(context.RunFolder, FileName), report);
        Console.WriteLine($"FPS loss {Format(report.CaptureMeans?.FpsLossPercent)} % against {TargetLossPercent} % ({report.Verdict}).");
        return 0;
    }

    /// <summary>Maps the script's <c>summary.json</c> (and the capture runs' stats files beside it) into the report.</summary>
    internal static FpsReport Map(
        string summaryJson, string summaryPath, string folder, string game, CaptureSettings settings, int seconds, int passes, GpuLoadInfo? load = null)
    {
        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;
        var runs = new List<FpsRun>();
        foreach (var row in Array(root, "runs"))
        {
            var tag = Text(row, "run") ?? "";
            var condition = Text(row, "condition") ?? "";
            long? framesEncoded = null, freshSlots = null, skipped = null;
            var stats = Path.Combine(folder, tag + ".json");
            if (condition != "baseline" && File.Exists(stats))
            {
                try
                {
                    using var statsDocument = JsonDocument.Parse(File.ReadAllText(stats));
                    if (statsDocument.RootElement.TryGetProperty("video", out var video))
                    {
                        framesEncoded = Whole(video, "framesEncoded");
                        freshSlots = Whole(video, "freshSlots");
                        skipped = Whole(video, "skippedSlots");
                    }
                }
                catch (JsonException)
                {
                    // A malformed stats file leaves the slot figures empty.
                }
            }

            runs.Add(new FpsRun(
                tag,
                condition,
                row.TryGetProperty("pass", out var pass) ? pass.ToString() : "",
                Number(row, "avgFps"),
                Number(row, "onePercentLowFps"),
                Number(row, "fpsLossPercent"),
                framesEncoded,
                freshSlots,
                framesEncoded is > 0 && freshSlots is { } fresh ? Math.Round(100.0 * fresh / framesEncoded.Value, 3) : null,
                skipped,
                Whole(row, "queueDrops"),
                Number(row, "duplicatePercent"),
                Number(row, "latencyP99Ms"),
                Number(row, "cpuPercentOneCore"),
                Number(row, "allocBytesPerSecond"),
                Text(row, "gcSteady")));
        }

        var interleaved = runs.Where(r => r.Pass != "final").ToList();
        var baseline = interleaved.Where(r => r.Condition == "baseline").ToList();
        var capture = interleaved.Where(r => r.Condition == "capture").ToList();
        var baselineFps = Mean(baseline.Select(r => r.AvgFps));
        var baselineLow = Mean(baseline.Select(r => r.OnePercentLowFps));
        FpsCapture? means = null;
        if (capture.Count > 0)
        {
            var low = Mean(capture.Select(r => r.OnePercentLowFps));
            means = new FpsCapture(
                Mean(capture.Select(r => r.AvgFps)),
                low,
                Mean(capture.Select(r => r.FpsLossPercent)),
                baselineLow is > 0 && low is { } l ? Math.Round((1 - (l / baselineLow.Value)) * 100, 3) : null,
                Mean(capture.Select(r => r.FreshSlotPercent)),
                Mean(capture.Select(r => r.DuplicatePercent)),
                Mean(capture.Select(r => r.LatencyP99Ms)),
                Mean(capture.Select(r => r.CpuPercentOneCore)),
                Mean(capture.Select(r => r.AllocBytesPerSecond)),
                Gen2Sum(capture));
        }

        var loss = means?.FpsLossPercent;
        var verdict = loss is null ? DriftMath.Inconclusive : loss < TargetLossPercent ? DriftMath.Pass : DriftMath.Miss;
        var aborted = root.TryGetProperty("aborted", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        return new FpsReport(
            DateTimeOffset.Now, game, settings, seconds, passes, summaryPath, aborted, baselineFps, baselineLow, means, runs, TargetLossPercent, verdict, load);
    }

    /// <summary>The Gen2 collections over the runs that report them; null when none does.</summary>
    private static long? Gen2Sum(IEnumerable<FpsRun> runs)
    {
        var counts = runs.Select(r => Gen2(r.GcSteady)).OfType<long>().ToList();
        return counts.Count == 0 ? null : counts.Sum();
    }

    private static long? Gen2(string? gc)
    {
        var parts = gc?.Split('/');
        return parts is { Length: 3 } && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static double? Mean(IEnumerable<double?> values)
    {
        var present = values.OfType<double>().ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 3);
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Array => value.EnumerateArray(),
                JsonValueKind.Object => [value],
                _ => [],
            }
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static long? Whole(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static string Format(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a";
}
