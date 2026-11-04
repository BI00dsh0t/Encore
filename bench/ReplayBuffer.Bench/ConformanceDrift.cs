using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Audio;
using PlatformFlow = ReplayBuffer.Platform.Audio.AudioFlow;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Bench;

/// <summary><c>conformance-drift-markers.json</c>: what a <c>bench drift --app</c> run played and pressed, kept so <c>--reanalyze</c> needs no App or devices.</summary>
/// <param name="AnchorQpc">The QPC at the Recording's file time 0: the Record press plus the engine's logged key offset (or <see cref="KeyAfterPressSeconds"/> null and a 50 ms guess).</param>
internal sealed record ConformanceLog(
    long QpcFrequency,
    long PressQpc,
    double? KeyAfterPressSeconds,
    long AnchorQpc,
    string Recording,
    string? StopReason,
    string Instance,
    string App,
    string DeviceA,
    string DeviceAId,
    string DeviceB,
    string DeviceBId,
    double ToneDbfs,
    double Minutes,
    CaptureSettings Capture,
    JsonElement Layout,
    IReadOnlyList<long> FlashQpcs,
    IReadOnlyList<long> BurstQpcsA,
    IReadOnlyList<long> BurstQpcsB,
    IReadOnlyList<ConformanceSave> Saves);

/// <summary>One Save press during the Recording: the Clip's path, or why there is none.</summary>
internal sealed record ConformanceSave(double AtSeconds, string? Path, string? Failure);

/// <summary>One Track's bursts from one toned Output Device against the marker's flashes.</summary>
internal sealed record ConformanceSeries(int Track, string? Title, string Device, AvResult Av);

/// <summary>Track-to-Track drift between two series: the largest change of their bucket medians' difference from the first bucket's.</summary>
internal sealed record ConformancePair(string A, string B, int Buckets, double? DriftMs, double TargetMs, string Verdict);

/// <summary><c>conformance-drift.json</c>.</summary>
internal sealed record ConformanceDriftReport(
    DateTimeOffset Created,
    double Minutes,
    string App,
    string Instance,
    CaptureSettings Capture,
    string Recording,
    string? StopReason,
    string DeviceA,
    string DeviceB,
    IReadOnlyList<string> Tracks,
    double? KeyAfterPressSeconds,
    int VideoFrames,
    IReadOnlyList<ConformanceSeries> Series,
    IReadOnlyList<ConformancePair> Pairs,
    double? AvDriftMs,
    double? TrackToTrackMs,
    IReadOnlyList<TrackCheck> TrackLengths,
    int SavesPressed,
    int SavesSaved,
    double TargetMs,
    string Verdict,
    IReadOnlyList<string> Warnings);

/// <summary>
/// <c>bench drift --app</c> (story 8.7, R7): the final conformance drift run. The real App and engine (the local
/// publish, <see cref="LiveApp"/>) run on a throwaway test instance with everything on: Bench's marker window is
/// the Target Game (listed with <c>game.add</c>, Recordings on), so the Replay Buffer buffers it and its Overlay
/// shows; the Library and Cleanup run on the instance's Output Folder; Save is pressed every
/// <c>--save-seconds</c>; and a real Recording runs for <c>--minutes</c> (30 by default, halved by the builder,
/// 2026-11-04) from a Record Hotkey press. The Track Layout is the builder's devices (<c>--layout</c> reads one
/// from a settings file, read-only; by default 2.9's four Tracks over <c>--device-a</c>, <c>--device-b</c> and
/// <c>--input-device</c>). Two tone players burst into A and B half a second apart, on the marker's flash grid.
/// The Recording is then analysed: per Track and toned device, A/V drift as <c>bench drift</c> measures it; between
/// every two series, Track-to-Track drift; and every Track's length against the video's (which also bounds an
/// Input-only Track, such as the Mic, that hears no burst). Each against one frame. Holds the <c>.bench-running</c>
/// lock for the whole capture.
/// </summary>
internal static class ConformanceDrift
{
    public const string FileName = "conformance-drift.json";
    public const string MarkdownName = "conformance-drift.md";
    public const string MarkersName = "conformance-drift-markers.json";
    public const double DefaultMinutes = 30;

    /// <summary>A Track's onset counts as device X's when it is within this of X's burst grid (A and B are 0.5 s apart).</summary>
    private const double PhaseTolerance = 0.25;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClipTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex KeyLine = new(
        @"The Recording starts on the keyframe [-\d.]+ s after the anchor \((?<press>-?[\d.]+) s after the press\)", RegexOptions.CultureInvariant);

    public static int Run(BenchContext context)
    {
        var options = context.Options;
        var minutes = options.Double("minutes", DefaultMinutes, 0.5, 600);
        var settings = options.CaptureSettings();
        var toneDbfs = options.Double("tone-dbfs", OnsetDetector.DefaultBurstDbfs, -60, -3);
        var bucketSeconds = options.Double("bucket-seconds", 60, 5, 3600);
        var saveSeconds = options.Int("save-seconds", 300, 10, 24 * 3600);
        var tools = context.ResolveFfmpeg();
        var run = context.RunFolder;
        var markersPath = Path.Combine(run, MarkersName);
        var warnings = new List<string>();

        ConformanceLog log;
        if (options.Flag("reanalyze"))
        {
            log = File.Exists(markersPath)
                ? JsonSerializer.Deserialize<ConformanceLog>(File.ReadAllText(markersPath), BenchJson.Options) ?? throw new BenchFailure($"{markersPath} is empty.")
                : throw new BenchFailure($"--reanalyze needs {MarkersName} in {run}.");
            Console.WriteLine($"Reanalysing {log.Recording}");
        }
        else
        {
            var lockPath = options.Text("lock") is { } given ? Path.GetFullPath(given) : SoakRun.DefaultLock(context.RepoRoot);
            using var held = BenchLock.Acquire(lockPath);
            log = Capture(context, minutes, settings, toneDbfs, saveSeconds, warnings);
            File.WriteAllText(markersPath, JsonSerializer.Serialize(log, BenchJson.Options));
        }

        if (!File.Exists(log.Recording))
        {
            throw new BenchFailure($"The Recording {log.Recording} is gone.");
        }

        Console.WriteLine("Analysing the Recording ...");
        var report = Analyze(tools, log, bucketSeconds, warnings);
        BenchJson.Write(Path.Combine(run, FileName), report);
        File.WriteAllText(Path.Combine(run, MarkdownName), Markdown(report));
        Console.WriteLine($"A/V drift {Ms(report.AvDriftMs)}, Track-to-Track {Ms(report.TrackToTrackMs)}, against {report.TargetMs:0.###} ms: {report.Verdict}.");
        foreach (var warning in report.Warnings)
        {
            Console.WriteLine("Warning: " + warning);
        }

        return report.Verdict == DriftMath.Pass ? 0 : 1;
    }

    private static ConformanceLog Capture(BenchContext context, double minutes, CaptureSettings settings, double toneDbfs, int saveSeconds, List<string> warnings)
    {
        var options = context.Options;
        var (layout, a, b) = ResolveLayout(options, settings);
        var appExe = LiveApp.Resolve(context);
        if (!appExe.Contains(Path.Combine("publish", "ReplayBuffer"), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"The App is not the local publish ({LiveApp.PublishedFolder}): {appExe}. Run pwsh tools/publish.ps1 first for the final run.");
        }

        var game = Environment.ProcessPath ?? throw new BenchFailure("Bench's own executable is unknown; it is the Target Game.");
        var save = KeyCombo.Parse(options.Text("key", "ctrl+alt+shift+f16"));
        var record = KeyCombo.Parse(options.Text("record-key", "ctrl+alt+shift+f17"));
        var run = context.RunFolder;
        var clips = Directory.CreateDirectory(Path.Combine(run, "clips")).FullName;
        Console.WriteLine($"Drift (app): a {minutes:0.##}-minute Recording of the marker window with every Track, Save every {saveSeconds} s, into {run}");
        foreach (var track in layout.Tracks)
        {
            var devices = track.DeviceKeys.Select(k => layout.Devices.First(d => d.Key == k).FriendlyName);
            Console.WriteLine($"  Track {track.Name}: {string.Join(", ", devices)}");
        }

        var frequency = Stopwatch.Frequency;
        var (toneA, toneB) = TracksCheck.StartStaggered(a, b, toneDbfs, warnings);
        using var disposeA = toneA;
        using var disposeB = toneB;
        using var marker = MarkerWindow.Open(settings.Fps);
        var first = toneA.BurstQpc(0);
        var ahead = Stopwatch.GetTimestamp() + (frequency / 2) - first;
        marker.StartFlashing(first + ((ahead / frequency) + 1) * frequency);

        // Ctrl+C ends the Recording early; Bench stays to stop it cleanly and analyse what was written.
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        var saves = new List<ConformanceSave>();
        try
        {
            using var app = LiveApp.Start(appExe, "drift", save, record,
            [
                SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(clips)),
                SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
                SettingsOp.SetGlobal(Keys.Video.Name, Keys.Video.ToJson(new VideoSettings(VideoCodec.H264, settings.Width, settings.Height, settings.Fps, settings.BitrateKbps, RateControl.Cbr))),

                // Cleanup runs on every save, with limits the run never reaches: it must not delete the Recording it measures.
                SettingsOp.SetGlobal(Keys.Cleanup.Name, Keys.Cleanup.ToJson(new CleanupSettings(500L * 1024 * 1024 * 1024, 30L * 24 * 3600))),
            ]);
            try
            {
                var profile = app.AddGame(game, "Bench marker", ignored: false);
                app.WaitBuffering(profile, StartTimeout, () => marker.Fault is { } f ? "the marker window failed: " + f.Message : null);
                Console.WriteLine("Buffering on the marker window. Starting the Recording.");
                cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                var (pressQpc, recording) = app.StartRecording(TimeSpan.FromSeconds(15));
                Console.WriteLine($"Recording to {recording}");

                var clock = Stopwatch.StartNew();
                var length = TimeSpan.FromMinutes(minutes);
                var nextSave = TimeSpan.FromSeconds(saveSeconds);
                var minute = 0;
                while (clock.Elapsed < length && !cancel.IsCancellationRequested)
                {
                    if (clock.Elapsed >= nextSave)
                    {
                        var at = clock.Elapsed.TotalSeconds;
                        var (path, failure) = app.Save(ClipTimeout);
                        saves.Add(new ConformanceSave(Math.Round(at, 1), path, failure));
                        Console.WriteLine($"  save at {at:0} s: {path ?? failure}");
                        nextSave += TimeSpan.FromSeconds(saveSeconds);
                    }

                    if ((int)clock.Elapsed.TotalMinutes > minute)
                    {
                        minute = (int)clock.Elapsed.TotalMinutes;
                        Console.WriteLine($"  ... {minute} min");
                    }

                    if (app.Client.State.Capture.Recording != ReplayBuffer.Contracts.Messages.RecordingState.Running)
                    {
                        warnings.Add($"The Recording stopped by itself after {clock.Elapsed.TotalSeconds:0} s ({app.Client.State.LastRecordingStopped?.Reason}).");
                        break;
                    }

                    cancel.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
                }

                if (cancel.IsCancellationRequested)
                {
                    warnings.Add($"Stopped early (Ctrl+C) after {clock.Elapsed.TotalMinutes:0.##} min.");
                }

                var stopped = app.Client.State.Capture.Recording == ReplayBuffer.Contracts.Messages.RecordingState.Running
                    ? app.StopRecording(TimeSpan.FromSeconds(30))
                    : app.Client.State.LastRecordingStopped;
                var recorded = clock.Elapsed.TotalMinutes;
                Console.WriteLine($"Recording stopped ({stopped?.Reason}).");

                // The tail: the last flash and burst a moment past the stop, then the engine's logs before the instance goes.
                Thread.Sleep(500);
                var logs = Path.Combine(run, "drift-app-logs");
                app.CopyLogs(logs);
                var keyAfterPress = KeyOffset(logs, warnings);
                var anchor = pressQpc + (long)((keyAfterPress ?? 0.05) * frequency);
                if (marker.Fault is { } markerFault)
                {
                    warnings.Add($"The marker window failed: {markerFault.Message}");
                }

                foreach (var (tone, name) in new[] { (toneA, "A"), (toneB, "B") })
                {
                    if (tone.Fault is { } fault)
                    {
                        warnings.Add($"The tone player on device {name} failed: {fault.Message}");
                    }
                }

                return new ConformanceLog(
                    frequency, pressQpc, keyAfterPress, anchor, recording, stopped?.Reason, app.Instance.Name, appExe,
                    a.FriendlyName, a.Id, b.FriendlyName, b.Id, toneDbfs, Math.Round(recorded, 3), settings,
                    Keys.TrackLayout.ToJson(layout), marker.FlashQpcs, Stamps(toneA), Stamps(toneB), saves);
            }
            catch
            {
                app.CopyLogs(Path.Combine(run, "drift-app-logs"));
                throw;
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// The Track Layout and the two toned Output Devices: <c>--layout</c> reads the <c>trackLayout</c> of a settings
    /// file (read only), with A and B its first two Output Devices unless <c>--device-a</c> and <c>--device-b</c> name
    /// two of them; otherwise 2.9's layout over <c>--device-a</c>, <c>--device-b</c> (default the BEACN Game and Chat
    /// outputs) and <c>--input-device</c> (default the BEACN Mic).
    /// </summary>
    internal static (TrackLayout Layout, AudioEndpoint A, AudioEndpoint B) ResolveLayout(BenchOptions options, CaptureSettings settings)
    {
        var outputs = AudioEndpoints.List(PlatformFlow.Output);
        if (options.Text("layout") is { } file)
        {
            var layout = ReadLayout(file);
            var toned = layout.Devices.Where(d => d.Flow == SettingsFlow.Output).ToList();
            LayoutDevice Pick(string option, int index)
            {
                if (options.Text(option) is { } query)
                {
                    return toned.FirstOrDefault(d => d.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        ?? throw new BenchUsageException($"--{option} '{query}' is not an Output Device of the layout in {file}.");
                }

                return index < toned.Count ? toned[index] : throw new BenchFailure($"The layout in {file} has fewer than two Output Devices to play bursts into.");
            }

            var (da, db) = (Pick("device-a", 0), Pick("device-b", 1));
            AudioEndpoint Live(LayoutDevice d) =>
                outputs.FirstOrDefault(e => string.Equals(e.Id, d.EndpointId, StringComparison.OrdinalIgnoreCase))
                ?? throw new BenchFailure($"The layout's Output Device '{d.FriendlyName}' [{d.EndpointId}] is not present.");
            return (layout, Live(da), Live(db));
        }

        var a = MarkedCapture.ResolveDevice(PlatformFlow.Output, options.Text("device-a", "Game (BEACN Mix Create)"));
        var b = MarkedCapture.ResolveDevice(PlatformFlow.Output, options.Text("device-b", "Chat (BEACN Mix Create)"));
        var mic = MarkedCapture.ResolveDevice(PlatformFlow.Input, settings.InputDevice ?? "BEACN Mic");
        if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new BenchUsageException($"--device-a and --device-b both name '{a.FriendlyName}'; give two different Output Devices.");
        }

        return (TracksCheck.Layout(a, b, mic), a, b);
    }

    /// <summary>The Global <c>trackLayout</c> of a settings file, read only.</summary>
    internal static TrackLayout ReadLayout(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("global", out var global)
                && global.TryGetProperty(Keys.TrackLayout.Name, out var value)
                && Keys.TrackLayout.TryRead(value, out var layout)
                ? layout
                : throw new BenchFailure($"{file} has no valid Global trackLayout.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new BenchFailure($"Could not read the Track Layout from {file}: {e.Message}");
        }
    }

    /// <summary>The engine's "starts on the keyframe … (<i>x</i> s after the press)" from its log; null when it isn't there.</summary>
    internal static double? KeyOffset(string logs, List<string> warnings)
    {
        if (Directory.Exists(logs))
        {
            foreach (var file in Directory.GetFiles(logs, "engine*").Order(StringComparer.Ordinal))
            {
                foreach (Match match in KeyLine.Matches(File.ReadAllText(file)))
                {
                    if (double.TryParse(match.Groups["press"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    {
                        return seconds;
                    }
                }
            }
        }

        warnings.Add("The engine's log has no Recording key offset: file time 0 is taken as 50 ms after the Record press.");
        return null;
    }

    public static ConformanceDriftReport Analyze(FfmpegTools tools, ConformanceLog log, double bucketSeconds, List<string> warnings)
    {
        var layout = Keys.TrackLayout.TryRead(log.Layout, out var read) ? read : throw new BenchFailure("The markers log has no valid Track Layout.");
        var frameMs = log.Capture.FrameMs;
        var scan = MediaAnalysis.Scan(tools, log.Recording, 0, log.ToneDbfs, CancellationToken.None);
        var audio = scan.AudioStreams.ToList();
        if (audio.Count != layout.Tracks.Count)
        {
            warnings.Add($"The Recording has {audio.Count} audio track(s) and the layout {layout.Tracks.Count}.");
        }

        var f = (double)log.QpcFrequency;
        double FileTime(long qpc) => (qpc - log.AnchorQpc) / f;
        var toned = new[] { (Name: log.DeviceA, Id: log.DeviceAId, Bursts: log.BurstQpcsA), (Name: log.DeviceB, Id: log.DeviceBId, Bursts: log.BurstQpcsB) };
        var series = new List<ConformanceSeries>();
        for (var i = 0; i < Math.Min(audio.Count, layout.Tracks.Count); i++)
        {
            var track = layout.Tracks[i];
            var ids = track.DeviceKeys.Select(k => layout.Devices.FirstOrDefault(d => d.Key == k)?.EndpointId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var feeding = toned.Where(t => ids.Contains(t.Id)).ToList();
            if (feeding.Count == 0)
            {
                continue;
            }

            var onsets = i == 0 ? scan.OnsetTimes : MediaAnalysis.TrackOnsets(tools, log.Recording, scan, i, log.ToneDbfs, CancellationToken.None).Times;
            foreach (var device in feeding)
            {
                // A Track that mixes A and B has both grids, half a second apart: keep the onsets on this device's.
                var mine = feeding.Count == 1 || device.Bursts.Count == 0 ? onsets : OnGrid(onsets, FileTime(device.Bursts[0]));
                var av = DriftMath.AnalyzeAv(new AvInput(
                    scan.FlashTimes, mine, log.FlashQpcs, device.Bursts, log.QpcFrequency, log.AnchorQpc,
                    (scan.Video.StartSeconds, scan.Video.EndSeconds), (audio[i].StartSeconds, audio[i].EndSeconds), frameMs, bucketSeconds));
                series.Add(new ConformanceSeries(i, audio[i].Title ?? track.Name, device.Name, av));
            }
        }

        var pairs = new List<ConformancePair>();
        for (var x = 0; x < series.Count; x++)
        {
            for (var y = x + 1; y < series.Count; y++)
            {
                pairs.Add(Pair(series[x], series[y], frameMs));
            }
        }

        var lengths = DriftBenchmark.CheckTracks(scan, frameMs);
        var saved = log.Saves.Count(s => s.Path is not null);
        if (saved < log.Saves.Count)
        {
            warnings.Add($"{log.Saves.Count - saved} of {log.Saves.Count} Save presses made no Clip: {string.Join("; ", log.Saves.Where(s => s.Failure is not null).Select(s => s.Failure))}.");
        }

        if (log.StopReason is { } reason && reason != ReplayBuffer.Contracts.Messages.RecordingStopReasons.Stopped)
        {
            warnings.Add($"The Recording stopped with reason '{reason}', not by the Record press.");
        }

        var avDrift = Max(series.Select(s => s.Av.DriftMs));
        var t2t = Max(pairs.Select(p => p.DriftMs));
        var marks = series.Select(s => s.Av.Verdict).Concat(pairs.Select(p => p.Verdict)).Concat(lengths.Select(t => t.Mark)).ToList();
        var verdict = series.Count == 0 ? DriftMath.Inconclusive
            : marks.Contains(DriftMath.Miss) ? DriftMath.Miss
            : marks.Contains(DriftMath.Inconclusive) ? DriftMath.Inconclusive
            : DriftMath.Pass;
        return new ConformanceDriftReport(
            DateTimeOffset.Now, log.Minutes, log.App, log.Instance, log.Capture, log.Recording, log.StopReason, log.DeviceA, log.DeviceB,
            [.. layout.Tracks.Select(t => t.Name)], log.KeyAfterPressSeconds, scan.VideoFrames, series, pairs, avDrift, t2t, lengths,
            log.Saves.Count, saved, Math.Round(frameMs, 3), verdict, warnings);
    }

    /// <summary>The onsets within <see cref="PhaseTolerance"/> of the grid through <paramref name="origin"/> (one burst a second).</summary>
    internal static List<double> OnGrid(IReadOnlyList<double> onsets, double origin) =>
        [.. onsets.Where(t => Math.Abs(Wrap(t - origin)) < PhaseTolerance)];

    /// <summary>
    /// Track-to-Track drift: per bucket both series have, the difference of their medians; the drift is the largest
    /// change of that difference from the first common bucket's.
    /// </summary>
    internal static ConformancePair Pair(ConformanceSeries a, ConformanceSeries b, double frameMs)
    {
        var name = (ConformanceSeries s) => $"{s.Title} ({s.Device})";
        var medians = b.Av.Buckets.ToDictionary(x => x.Index, x => x.MedianMs);
        var differences = a.Av.Buckets.Where(x => medians.ContainsKey(x.Index)).OrderBy(x => x.Index).Select(x => x.MedianMs - medians[x.Index]).ToList();
        if (differences.Count < 2)
        {
            return new ConformancePair(name(a), name(b), differences.Count, null, Math.Round(frameMs, 3), DriftMath.Inconclusive);
        }

        var drift = Math.Round(differences.Max(d => Math.Abs(d - differences[0])), 3);
        return new ConformancePair(name(a), name(b), differences.Count, drift, Math.Round(frameMs, 3), drift < frameMs ? DriftMath.Pass : DriftMath.Miss);
    }

    internal static string Markdown(ConformanceDriftReport r)
    {
        var md = new StringBuilder();
        md.AppendLine("# Final conformance drift (story 8.7, R7)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"A {r.Minutes:0.##}-minute Recording of Bench's marker window by the real App and engine (`{r.App}`, instance `{r.Instance}`), with the Replay Buffer, the Overlay, the Library and Cleanup on, and Save pressed {r.SavesPressed} times ({r.SavesSaved} Clips). {r.Capture.Size} at {r.Capture.Fps} fps, H.264 CBR {r.Capture.BitrateKbps} kbps. Bursts into `{r.DeviceA}` (A) and `{r.DeviceB}` (B, half a second later). Tracks: {string.Join(", ", r.Tracks)}. File `{r.Recording}`, stopped: {r.StopReason ?? "n/a"}.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"**Verdict: {r.Verdict}.** A/V drift {Ms(r.AvDriftMs)}, Track-to-Track {Ms(r.TrackToTrackMs)}, against one frame ({r.TargetMs:0.###} ms).");
        md.AppendLine();
        md.AppendLine("| Track | Device | A/V drift | Offset | Slope | Flashes | Bursts | Result |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in r.Series)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.Track} {s.Title} | {s.Device} | {Ms(s.Av.DriftMs)} | {Ms(s.Av.AbsoluteOffsetMs)} | {(s.Av.SlopeMsPerMinute is { } sl ? sl.ToString("0.###", CultureInfo.InvariantCulture) + " ms/min" : "n/a")} | {s.Av.FlashesMatched}/{s.Av.FlashesExpected} | {s.Av.BurstsMatched}/{s.Av.BurstsExpected} | {s.Av.Verdict}{(s.Av.Why is null ? "" : ": " + s.Av.Why)} |");
        }

        md.AppendLine();
        md.AppendLine("| Track-to-Track | Buckets | Drift | Result |");
        md.AppendLine("|---|---|---|---|");
        foreach (var p in r.Pairs)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {p.A} / {p.B} | {p.Buckets} | {Ms(p.DriftMs)} | {p.Verdict} |");
        }

        md.AppendLine();
        md.AppendLine("| Track | Title | Length − video | Tolerance | Result |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var t in r.TrackLengths)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {t.Track} | {t.Title} | {t.MinusVideoMs:0.##} ms | {t.TargetMs:0.###} ms | {t.Mark} |");
        }

        if (r.Warnings.Count > 0)
        {
            md.AppendLine();
            foreach (var w in r.Warnings)
            {
                md.AppendLine("- Warning: " + w);
            }
        }

        return md.ToString();
    }

    private static double Wrap(double seconds) => seconds - Math.Round(seconds);

    private static double? Max(IEnumerable<double?> values)
    {
        var present = values.OfType<double>().ToList();
        return present.Count == 0 ? null : present.Max(Math.Abs);
    }

    private static long[] Stamps(TonePlayer tone) => [.. Enumerable.Range(0, tone.BurstCount).Select(tone.BurstQpc)];

    private static string Ms(double? value) => value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) + " ms" : "n/a";
}
