using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ReplayBuffer.Platform.Audio;

namespace ReplayBuffer.Bench;

/// <summary>The source log of one marked capture: kept beside the file, so it can be analysed again.</summary>
/// <param name="Minutes">The capture's length; null in a log written before it was recorded.</param>
/// <param name="Capture">The capture's output settings; null in a log written before they were recorded.</param>
internal sealed record MarkerLog(
    long QpcFrequency,
    long EngineStartQpc,
    string Hwnd,
    string OutputDevice,
    string OutputDeviceId,
    string InputDevice,
    string InputDeviceId,
    IReadOnlyList<long> FlashQpcs,
    IReadOnlyList<long> BurstQpcs,
    double? Minutes = null,
    CaptureSettings? Capture = null);

/// <summary>One audio track's sample count in the file against the video's duration (from its first packet's timestamp to the end of its last packet).</summary>
/// <param name="Samples">Decoded samples for the burst track; AAC packets × 1024 for the others.</param>
/// <param name="DurationSeconds">Samples / 48 kHz.</param>
internal sealed record TrackCheck(int Track, string? Title, long Samples, double DurationSeconds, double MinusVideoMs, double TargetMs, string Mark);

/// <summary><c>drift.json</c>.</summary>
internal sealed record DriftReport(
    DateTimeOffset Created,
    double Minutes,
    string Engine,
    CaptureSettings Capture,
    string? OutputDevice,
    string? InputDevice,
    string File,
    string? Stats,
    int? EngineExit,
    string? EngineError,
    double? StartupToAnchorMs,
    int VideoFrames,
    AvResult? Av,
    AudioToAudioResult? AudioToAudio,
    IReadOnlyList<TrackCheck> Tracks,
    string Verdict,
    IReadOnlyList<string> Warnings);

/// <summary>
/// A capture of the marker window while the tone player plays its bursts into the Output Device: the
/// engine's <c>debug-capture</c> with both devices, the marker flashing on the bursts' second grid.
/// </summary>
internal static class MarkedCapture
{
    /// <param name="codec">The engine's <c>--codec</c> (h264, hevc or av1); null is its default, H.264.</param>
    /// <param name="probe">Screen grabs around the marker: one before the engine starts, one <see cref="BorderProbe.Delay"/> into its capture.</param>
    public static (int ExitCode, string Stderr, MarkerLog Log) Run(
        string engine, CaptureSettings settings, int seconds, string mp4, string? statsPath, string logPrefix, double toneDbfs, List<string> warnings,
        BorderProbe? probe = null, string? codec = null)
    {
        var output = ResolveDevice(AudioFlow.Output, settings.OutputDevice);
        var input = ResolveDevice(AudioFlow.Input, settings.InputDevice);
        Console.WriteLine($"Output Device: {output.FriendlyName}  [{output.Id}]");
        Console.WriteLine($"Input Device:  {input.FriendlyName}  [{input.Id}]");

        var frequency = Stopwatch.Frequency;
        using var tone = new TonePlayer(output.Id, toneDbfs);
        tone.Start();
        var waited = Stopwatch.StartNew();
        while (tone.BurstCount == 0)
        {
            if (tone.Fault is { } toneFault)
            {
                throw new BenchFailure($"The tone player failed on '{output.FriendlyName}': {toneFault.Message}");
            }

            if (waited.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new BenchFailure($"No burst played on '{output.FriendlyName}' within 5 s.");
            }

            Thread.Sleep(20);
        }

        using var marker = MarkerWindow.Open(settings.Fps);

        // Flash on the bursts' own second grid, so each burst sits next to a flash at source.
        var first = tone.BurstQpc(0);
        var ahead = Stopwatch.GetTimestamp() + (frequency / 2) - first;
        marker.StartFlashing(first + ((ahead / frequency) + 1) * frequency);

        var hwnd = "0x" + marker.Handle.ToString("X", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "debug-capture", "--hwnd", hwnd, "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--output", mp4,
            "--size", settings.Size, "--fps", settings.Fps.ToString(CultureInfo.InvariantCulture),
            "--bitrate", settings.BitrateKbps.ToString(CultureInfo.InvariantCulture),
            "--output-device", output.Id, "--input-device", input.Id,
        };
        if (statsPath is not null)
        {
            args.AddRange(["--stats", statsPath]);
        }

        if (codec is not null)
        {
            args.AddRange(["--codec", codec]);
        }

        Console.WriteLine($"Engine: {engine} {string.Join(' ', args)}");
        // Ctrl+C reaches the engine too, which ends its capture early and finishes the file; Bench stays to analyse it.
        ConsoleCancelEventHandler keep = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += keep;
        int exit;
        string stderr;
        long startQpc;
        Task? grab = null;
        try
        {
            if (probe is not null)
            {
                probe.Before(marker.Handle);
                grab = Task.Run(() =>
                {
                    Thread.Sleep(probe.Delay);
                    probe.During(marker.Handle);
                });
            }

            (exit, stderr, startQpc) = EngineProcess.Run(engine, args, logPrefix, TimeSpan.FromSeconds(seconds + 180));
        }
        finally
        {
            Console.CancelKeyPress -= keep;

            // The marker must outlive the grab.
            grab?.Wait();
        }

        // The engine's tail is past its --seconds; give the last flash and burst a moment, then stop both.
        Thread.Sleep(500);
        var flashes = marker.FlashQpcs;
        var bursts = Enumerable.Range(0, tone.BurstCount).Select(tone.BurstQpc).ToArray();
        if (marker.Fault is { } markerFault)
        {
            warnings.Add($"The marker window failed: {markerFault.Message}");
        }

        if (tone.Fault is { } fault)
        {
            warnings.Add($"The tone player failed: {fault.Message}");
        }

        var log = new MarkerLog(frequency, startQpc, hwnd, output.FriendlyName, output.Id, input.FriendlyName, input.Id, flashes, bursts, seconds / 60.0, settings);
        return (exit, stderr, log);
    }

    internal static AudioEndpoint ResolveDevice(AudioFlow flow, string? query)
    {
        if (query is null)
        {
            return AudioEndpoints.Default(flow);
        }

        var match = AudioEndpoints.Match(AudioEndpoints.List(flow), query);
        return match.Outcome == EndpointMatchOutcome.Found
            ? match.Endpoint!
            : throw new BenchFailure(
                $"{match.Outcome}: no single {flow} device matches '{query}'. Devices:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, match.Candidates.Select(e => $"  {e.FriendlyName}  [{e.Id}]")));
    }
}

/// <summary>Runs the engine with its output logged beside the run's other files.</summary>
internal static class EngineProcess
{
    /// <returns>The exit code, the stderr text, and the QPC read just before the process started.</returns>
    public static (int ExitCode, string Stderr, long StartQpc) Run(string engine, IReadOnlyList<string> args, string logPrefix, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        var startQpc = Stopwatch.GetTimestamp();
        using var process = Process.Start(start) ?? throw new BenchFailure($"Could not start {engine}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        var minute = 0;
        while (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            if (clock.Elapsed > timeout)
            {
                process.Kill();
                process.WaitForExit();
                break;
            }

            if ((int)clock.Elapsed.TotalMinutes > minute)
            {
                minute = (int)clock.Elapsed.TotalMinutes;
                Console.WriteLine($"  ... {minute} min");
            }
        }

        var errorText = stderr.GetAwaiter().GetResult();
        File.WriteAllText(logPrefix + "-engine.out.log", stdout.GetAwaiter().GetResult());
        File.WriteAllText(logPrefix + "-engine.err.log", errorText);
        return (process.ExitCode, errorText.Trim(), startQpc);
    }
}

/// <summary>
/// <c>bench drift</c>: a marked capture of <c>--minutes</c>, then the analysis of its file: A/V from the
/// flashes and bursts, audio-to-audio from the stats file's per-device steering.
/// </summary>
internal static class DriftBenchmark
{
    public const string FileName = "drift.json";

    public static int Run(BenchContext context)
    {
        var options = context.Options;
        if (options.Flag("app"))
        {
            return ConformanceDrift.Run(context);
        }

        if (new[] { "save-seconds", "device-a", "device-b", "layout", "key", "record-key", "app-exe", "lock" }.FirstOrDefault(options.Has) is { } appOption)
        {
            throw new BenchUsageException($"--{appOption} is for drift --app.");
        }

        var minutes = options.Double("minutes", 60, 1, 600);
        var settings = options.CaptureSettings();
        var toneDbfs = options.Double("tone-dbfs", OnsetDetector.DefaultBurstDbfs, -60, -3);
        var bucketSeconds = options.Double("bucket-seconds", 60, 5, 3600);
        var burstTrack = options.Int("burst-track", 0, 0, 16);
        var tools = context.ResolveFfmpeg();

        var mp4 = Path.Combine(context.RunFolder, "drift.mp4");
        var stats = Path.Combine(context.RunFolder, "drift-stats.json");
        var logPath = Path.Combine(context.RunFolder, "drift-markers.json");
        var warnings = new List<string>();
        string engine;
        int? exit = null;
        string? engineError = null;
        MarkerLog log;
        if (options.Flag("reanalyze"))
        {
            if (!File.Exists(logPath) || !File.Exists(mp4))
            {
                throw new BenchFailure($"--reanalyze needs drift.mp4 and drift-markers.json in {context.RunFolder}.");
            }

            log = JsonSerializer.Deserialize<MarkerLog>(File.ReadAllText(logPath), BenchJson.Options)
                ?? throw new BenchFailure($"{logPath} is empty.");

            // The run's own length and output settings, when its log has them (the frame target follows its fps).
            minutes = log.Minutes ?? minutes;
            settings = log.Capture ?? settings;

            // Keep what the capture run recorded about the engine.
            engine = "(reanalysed)";
            try
            {
                var previous = JsonSerializer.Deserialize<DriftReport>(File.ReadAllText(Path.Combine(context.RunFolder, FileName)), BenchJson.Options);
                (engine, exit) = (previous?.Engine ?? engine, previous?.EngineExit);
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                // No earlier drift.json, or a malformed one.
            }

            Console.WriteLine($"Reanalysing {mp4}");
        }
        else
        {
            engine = context.ResolveEngine();
            var seconds = (int)Math.Round(minutes * 60);
            Console.WriteLine($"Drift: {minutes} min of the marker window, into {context.RunFolder}");
            var (code, stderr, marked) = MarkedCapture.Run(engine, settings, seconds, mp4, stats, Path.Combine(context.RunFolder, "drift"), toneDbfs, warnings);
            log = marked;
            exit = code;
            File.WriteAllText(logPath, JsonSerializer.Serialize(log, BenchJson.Options));
            if (code != 0 || !File.Exists(mp4))
            {
                engineError = stderr.Length > 0 ? stderr : $"the engine exited with {code} and wrote no file";
                var failed = new DriftReport(
                    DateTimeOffset.Now, minutes, engine, settings, log.OutputDevice, log.InputDevice, mp4, null, exit, engineError, null, 0,
                    null, null, [], "failed", warnings);
                BenchJson.Write(Path.Combine(context.RunFolder, FileName), failed);
                Console.Error.WriteLine($"The engine failed: {engineError}");
                return 1;
            }
        }

        Console.WriteLine("Analysing the file ...");
        var report = Analyze(tools, mp4, File.Exists(stats) ? stats : null, log, settings, minutes, engine, exit, bucketSeconds, burstTrack, toneDbfs, warnings);
        BenchJson.Write(Path.Combine(context.RunFolder, FileName), report);
        Print(report);
        return 0;
    }

    public static DriftReport Analyze(
        FfmpegTools tools,
        string mp4,
        string? statsPath,
        MarkerLog log,
        CaptureSettings settings,
        double minutes,
        string engine,
        int? exit,
        double bucketSeconds,
        int burstTrack,
        double toneDbfs,
        List<string> warnings)
    {
        var scan = MediaAnalysis.Scan(tools, mp4, burstTrack, toneDbfs, CancellationToken.None);

        double? startup = null;
        AudioToAudioResult? audioToAudio = null;
        if (statsPath is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statsPath), new JsonDocumentOptions { AllowTrailingCommas = true });
                var root = document.RootElement;
                if (root.TryGetProperty("resources", out var resources) && resources.TryGetProperty("startupToAnchorMs", out var s) && s.ValueKind == JsonValueKind.Number)
                {
                    startup = s.GetDouble();
                }

                audioToAudio = DriftMath.AudioToAudio(root, settings.Fps);
            }
            catch (JsonException e)
            {
                warnings.Add($"The stats file {statsPath} is malformed: {e.Message}");
            }
        }
        else
        {
            warnings.Add("No stats file: audio-to-audio is not measured.");
        }

        // File time 0 is the engine's anchor: its process start plus its startup to the first frame.
        var anchor = log.EngineStartQpc + (long)((startup ?? 0) * log.QpcFrequency / 1000);
        var av = DriftMath.AnalyzeAv(new AvInput(
            scan.FlashTimes,
            scan.OnsetTimes,
            log.FlashQpcs,
            log.BurstQpcs,
            log.QpcFrequency,
            anchor,
            (scan.Video.StartSeconds, scan.Video.EndSeconds),
            (scan.BurstStream.StartSeconds, scan.BurstStream.EndSeconds),
            settings.FrameMs,
            bucketSeconds));

        var tracks = CheckTracks(scan, settings.FrameMs);

        var verdicts = new[] { av.Verdict, audioToAudio?.Verdict ?? DriftMath.Inconclusive };
        var verdict = verdicts.Contains(DriftMath.Miss) ? DriftMath.Miss : verdicts.Contains(DriftMath.Inconclusive) ? DriftMath.Inconclusive : DriftMath.Pass;
        return new DriftReport(
            DateTimeOffset.Now, minutes, engine, settings, log.OutputDevice, log.InputDevice, mp4, statsPath, exit, null, startup, scan.VideoFrames,
            av, audioToAudio, tracks, verdict, warnings);
    }

    /// <summary>
    /// Each track's sample count against the video's span from its packet timestamps, not its frame count over
    /// the fps: a slot the pacer skips leaves a gap in the timestamps and no frame. A track's ends are whole AAC
    /// frames, so the tolerance is one AAC frame (1024 samples) plus one video frame.
    /// </summary>
    internal static List<TrackCheck> CheckTracks(MediaScan scan, double frameMs)
    {
        const int AacFrameSamples = 1024;
        var toleranceMs = (AacFrameSamples * 1000.0 / OnsetDetector.SampleRate) + frameMs;
        var videoSeconds = scan.Video.DurationSeconds;
        return scan.AudioStreams.Select((stream, i) =>
        {
            var samples = i == scan.BurstTrack ? scan.BurstTrackSamples : stream.Packets * AacFrameSamples;
            var seconds = (double)samples / OnsetDetector.SampleRate;
            var minus = (seconds - videoSeconds) * 1000;
            return new TrackCheck(i, stream.Title, samples, Math.Round(seconds, 4), Math.Round(minus, 2), Math.Round(toleranceMs, 3),
                Math.Abs(minus) < toleranceMs ? DriftMath.Pass : DriftMath.Miss);
        }).ToList();
    }

    private static void Print(DriftReport report)
    {
        if (report.Av is { } av)
        {
            Console.WriteLine($"A/V: drift {av.DriftMs?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a"} ms against {av.TargetMs:0.#} ms ({av.Verdict}); "
                + $"flashes {av.FlashesMatched}/{av.FlashesExpected}, bursts {av.BurstsMatched}/{av.BurstsExpected}, offset {av.AbsoluteOffsetMs?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a"} ms"
                + (av.Why is null ? "" : $"; {av.Why}"));
        }

        if (report.AudioToAudio is { } a2a)
        {
            Console.WriteLine($"Audio-to-audio: bound {a2a.BoundSamples?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a"} samples against {a2a.TargetSamples:0} ({a2a.Verdict})"
                + (a2a.Why is null ? "" : $"; {a2a.Why}"));
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine("Warning: " + warning);
        }
    }
}
