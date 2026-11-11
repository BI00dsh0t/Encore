using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Encore.Contracts;

namespace Encore.Bench;

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
    string? GcSteady,
    string? PresentMode = null);

/// <summary><c>bench fps --overlay</c>: the App's Overlay was on for the capture runs, with this dot mode and message interval.</summary>
internal sealed record FpsOverlay(string Dot, bool? OverlayPlanes, int MessageSeconds, bool Recording = false);

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

/// <summary>The <c>fps.json</c> report; Load is null in a file written before it was recorded.</summary>
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
    GpuLoadInfo? Load = null,
    FpsOverlay? Overlay = null);

/// <summary><c>bench fps</c>: a script runs baseline against capture, and Bench maps its results into <c>fps.json</c>.</summary>
internal static class FpsBenchmark
{
    public const string FileName = "fps.json";
    public const double TargetLossPercent = 3;

    /// <summary>The script's condition whose capture is the App's own.</summary>
    public const string AppCondition = "app";

    /// <summary>The default run length with a Recording: 2.5 minutes, as the builder chose.</summary>
    public const int RecordingSeconds = 150;

    /// <summary>With <c>--overlay</c>, the App shows a message this often while its dot is on.</summary>
    public const int MessageSeconds = 10;

    /// <summary>The options of the synthetic load, refused with <c>--game</c>.</summary>
    private static readonly string[] LoadOptionNames = ["target-fps", "load-size", "load-timeout", "load-warmup", "load-exe"];

    /// <summary>The script's line as a run starts settling: <c>[02-capture-1] settling 20 s ...</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex SettlingLine =
        new(@"^\[\d+-(?<condition>[A-Za-z]+)-[^\]]+\] settling\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static int Run(BenchContext context, TextWriter? errors = null)
    {
        var options = context.Options;
        var withOverlay = options.Flag("overlay");
        if (!withOverlay && options.Has("overlay-dot"))
        {
            throw new BenchUsageException("--overlay-dot is for --overlay.");
        }

        if (!withOverlay && options.Has("app-exe"))
        {
            throw new BenchUsageException("--app-exe is for --overlay.");
        }

        // The capture runs are the real App's capture of the load, with a Recording, in the script's app condition.
        var withRecording = options.Flag("recording");
        if (withRecording && !withOverlay)
        {
            throw new BenchUsageException("--recording is for --overlay.");
        }

        var dot = AppOverlayHost.ParseDot(options);
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

        // The load calibrates for its warm-up, then measures for a few seconds: wait at least that long.
        if (loadWarmup is { } warmupSeconds && loadTimeout < TimeSpan.FromSeconds(warmupSeconds + 30))
        {
            loadTimeout = TimeSpan.FromSeconds(warmupSeconds + 30);
        }
        var game = realGame ?? GpuLoadProcess.ProcessName;
        var seconds = options.Int("seconds", withRecording ? RecordingSeconds : 300, 10, 7200);
        var passes = options.Int("passes", 3, 1, 20);
        var settle = options.Int("settle", 20, 0, 600);
        var settings = options.CaptureSettings();
        var pwsh = context.ResolvePwsh();
        var presentMon = context.ResolvePresentMon();
        var engine = context.ResolveEngine();
        var loadExe = realGame is null ? GpuLoadProcess.Resolve(context) : null;
        var appExe = withRecording ? LiveApp.Resolve(context) : withOverlay ? AppOverlayHost.Resolve(context) : null;
        var folder = Path.Combine(context.RunFolder, "fps");
        var script = Path.Combine(context.RepoRoot, "tools", "measure-path-a.ps1");

        var args = new List<string>
        {
            "-NoProfile", "-File", script,
            "-Game", game,
            "-Conditions", withRecording ? "baseline," + AppCondition : options.Text("conditions") ?? "baseline,capture",
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

        // The script's stderr is relayed from another thread, and the message timer may report too.
        errors = TextWriter.Synchronized(errors ?? Console.Error);
        GpuLoadInfo loadInfo;
        GpuLoadProcess? load = null;
        AppOverlayHost? appHost = null;
        LiveCapture? live = null;
        Timer? messages = null;
        OverlaySwitch? overlay = null;
        FpsOverlay? overlayInfo = null;
        var loadDied = false;
        try
        {
            if (loadExe is not null)
            {
                // The script takes the first process of the name and PresentMon traces them all: only Bench's own may run.
                if (GpuLoadProcess.Running() is { Count: > 0 } running)
                {
                    throw new BenchFailure(
                        $"{GpuLoadProcess.ProcessName} is already running (pid {string.Join(", ", running)}); close it before measuring.");
                }

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

            if (appExe is not null)
            {
                // The App's Game Detection must not capture the load: its profile is set Ignored by its exe path.
                var (ignoredExe, pid) = load is not null ? (loadExe!, load.Id) : GameExe(game);
                try
                {
                    if (withRecording)
                    {
                        live = LiveCapture.Start(appExe, ignoredExe, settings, dot);
                        live.LogsTo = Path.Combine(folder, "app-logs");
                    }
                    else
                    {
                        appHost = AppOverlayHost.Start(appExe, ignoredExe, messages: false, dot);
                    }
                }
                catch (BenchFailure e)
                {
                    errors.WriteLine(e.Message);
                    return 1;
                }

                overlayInfo = new FpsOverlay(AppOverlayHost.DotName(dot), Encore.Platform.Desktop.Monitors.OverlayPlanes(pid), MessageSeconds, withRecording);
                Console.WriteLine(withRecording
                    ? $"Capture runs: the App's own Rewind on the load with a Recording, its dot ({overlayInfo.Dot}) and a message every {MessageSeconds} s; overlay planes on the load's monitor: {YesNo(overlayInfo.OverlayPlanes)}."
                    : $"Overlay: the App's dot ({overlayInfo.Dot}) and a message every {MessageSeconds} s in the capture runs; overlay planes on the load's monitor: {YesNo(overlayInfo.OverlayPlanes)}.");
                var overlaySwitch = overlay = live is not null
                    ? new OverlaySwitch(live.On, live.Off, live.Message, errors)
                    : new OverlaySwitch(appHost!.On, appHost!.Off, appHost!.Message, errors);
                messages = new Timer(_ => overlaySwitch.Message());
            }

            Console.WriteLine($"FPS: {pwsh} {string.Join(' ', args)}");

            // The script's stderr is relayed to errors line by line; its stdout streams to the console and is read for the overlay.
            var start = new ProcessStartInfo(pwsh) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = overlay is not null };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start) ?? throw new BenchFailure($"Could not start {pwsh}.");
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    errors.WriteLine(e.Data);
                }
            };
            process.BeginErrorReadLine();
            try
            {
                if (overlay is not null)
                {
                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) is not null)
                    {
                        Console.WriteLine(line);
                        if (OverlayFor(line) is { } next)
                        {
                            overlay.Set(next);
                            if (next)
                            {
                                messages!.Change(TimeSpan.FromSeconds(MessageSeconds), TimeSpan.FromSeconds(MessageSeconds));
                            }
                            else
                            {
                                messages!.Change(Timeout.Infinite, Timeout.Infinite);
                            }
                        }
                    }
                }

                process.WaitForExit();
            }
            finally
            {
                // Leaving early (an exception) must not leave the script, its engine or PresentMon running.
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            if (load is { HasExited: true })
            {
                loadDied = true;
                errors.WriteLine($"The GPU load exited during the run ({load.ExitDescription()}).");
            }

            if (process.ExitCode != 0)
            {
                errors.WriteLine($"measure-path-a.ps1 exited with {process.ExitCode}.");
                return 1;
            }
        }
        finally
        {
            // Never leave the load, the App or its engine running, whatever happened; the timer first, so it sends no more.
            if (messages is not null)
            {
                using var stopped = new ManualResetEvent(false);
                if (messages.Dispose(stopped))
                {
                    stopped.WaitOne(TimeSpan.FromSeconds(15));
                }
            }

            appHost?.Dispose();
            live?.Dispose();
            load?.Dispose();
        }

        var summary = Path.Combine(folder, "summary.json");
        if (!File.Exists(summary))
        {
            throw new BenchFailure($"measure-path-a.ps1 wrote no {summary}.");
        }

        var report = Map(File.ReadAllText(summary), summary, folder, game, settings, seconds, passes, loadInfo, overlayInfo);
        BenchJson.Write(Path.Combine(context.RunFolder, FileName), report);
        Console.WriteLine($"FPS loss {Format(report.CaptureMeans?.FpsLossPercent)} % against {TargetLossPercent} % ({report.Verdict}).");

        // The script records an engine failure as aborted and exits 0: an unattended run must not read that as a measurement.
        if (Unmeasured(report) is { } reason)
        {
            errors.WriteLine(reason);
            return 1;
        }

        return loadDied || overlay is { Failed: true } ? 1 : 0;
    }

    /// <summary>Why <paramref name="report"/> is not a measurement: the script aborted, or no capture run has a frame rate.</summary>
    internal static string? Unmeasured(FpsReport report) =>
        report.Aborted is { } aborted ? $"measure-path-a.ps1 aborted: {aborted}"
        : !report.Runs.Any(r => r.Condition != "baseline" && r.AvgFps is not null) ? "No capture run has a frame rate; nothing was measured."
        : null;

    /// <summary>The Overlay's state from one line of the script's output: on, off, or null for no change.</summary>
    internal static bool? OverlayFor(string line) =>
        SettlingLine.Match(line) is { Success: true } match ? match.Groups["condition"].Value != "baseline" : null;

    /// <summary>Maps the script's summary, the capture runs' stats files and each run's PresentMon CSV into the report.</summary>
    internal static FpsReport Map(
        string summaryJson, string summaryPath, string folder, string game, CaptureSettings settings, int seconds, int passes,
        GpuLoadInfo? load = null, FpsOverlay? overlay = null)
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

            var presents = Path.Combine(folder, tag + "-presentmon.csv");
            var presentMode = tag.Length > 0 && File.Exists(presents) ? PresentMonCsv.Analyze(presents).DominantMode : null;

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
                Text(row, "gcSteady"),
                presentMode));
        }

        var interleaved = runs.Where(r => r.Pass != "final").ToList();
        var baseline = interleaved.Where(r => r.Condition == "baseline").ToList();
        var capture = interleaved.Where(r => r.Condition is "capture" or AppCondition).ToList();
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
            DateTimeOffset.Now, game, settings, seconds, passes, summaryPath, aborted, baselineFps, baselineLow, means, runs, TargetLossPercent, verdict, load, overlay);
    }

    /// <summary>The running game's exe (its profile id) and process id.</summary>
    private static (string Exe, int Pid) GameExe(string game)
    {
        var processes = Process.GetProcessesByName(game);
        try
        {
            var process = processes.FirstOrDefault(p => p.MainWindowHandle != 0)
                ?? throw new BenchFailure($"The game process '{game}' is not running (or has no main window); --overlay sets its profile Ignored by its exe.");
            var exe = process.MainModule?.FileName ?? throw new BenchFailure($"The exe of '{game}' (pid {process.Id}) couldn't be read.");
            return (exe, process.Id);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new BenchFailure($"The exe of '{game}' couldn't be read: {e.Message}");
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>The App's Overlay during the run; one lock serializes on, off and messages; a failure is reported, not thrown.</summary>
    private sealed class OverlaySwitch(Action on, Action off, Action message, TextWriter errors)
    {
        private readonly Lock _lock = new();
        private readonly Action onAction = on;
        private readonly Action offAction = off;
        private bool? _on;

        public bool Failed { get; private set; }

        public void Set(bool on)
        {
            lock (_lock)
            {
                if (_on == on)
                {
                    return;
                }

                _on = on;
                Send(on ? onAction : offAction, on ? "on" : "off");
            }
        }

        public void Message()
        {
            lock (_lock)
            {
                if (_on == true)
                {
                    Send(message, "message");
                }
            }
        }

        private void Send(Action send, string what)
        {
            try
            {
                send();
            }
            catch (Exception e) when (e is ErrorCodeException or TimeoutException or ObjectDisposedException or InvalidOperationException or BenchFailure)
            {
                Failed = true;
                errors.WriteLine($"The App's Overlay {what} failed: {e.Message}");
            }
        }
    }

    private static string YesNo(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };

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
