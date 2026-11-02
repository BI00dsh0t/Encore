using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayBuffer.Bench;

/// <summary>Bench's JSON: camelCase, indented, written through a temporary file.</summary>
internal static class BenchJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary><c>report.json</c>: whichever measurements the run folder holds.</summary>
internal sealed record BenchReport(
    DateTimeOffset Created,
    string RunFolder,
    FpsReport? Fps,
    DriftReport? Drift,
    PlaybackReport? Playback,
    HotkeyReport? Hotkey,
    TracksReport? Tracks,
    IReadOnlyList<string> NotRun,
    IReadOnlyList<string> Errors);

/// <summary>
/// Merges the run folder's <c>fps.json</c>, <c>drift.json</c>, <c>playback.json</c>, <c>hotkey.json</c> and <c>tracks.json</c> into
/// <c>report.json</c> and <c>report.md</c>: a section per measurement present, each figure beside its target
/// with a pass or miss mark, and "not run" for the rest. A malformed file is named and skipped.
/// </summary>
internal static class Report
{
    public const string JsonName = "report.json";
    public const string MarkdownName = "report.md";

    public static BenchReport Generate(string runFolder)
    {
        var errors = new List<string>();
        var notRun = new List<string>();
        var fps = Load<FpsReport>(runFolder, FpsBenchmark.FileName, "FPS", errors, notRun);
        var drift = Load<DriftReport>(runFolder, DriftBenchmark.FileName, "Drift", errors, notRun);
        var playback = Load<PlaybackReport>(runFolder, PlaybackCheck.FileName, "Playback", errors, notRun);
        var hotkey = Load<HotkeyReport>(runFolder, HotkeyBenchmark.FileName, "Hotkey", errors, notRun);
        var tracks = Load<TracksReport>(runFolder, TracksCheck.FileName, "Named Tracks", errors, notRun);
        var report = new BenchReport(DateTimeOffset.Now, runFolder, fps, drift, playback, hotkey, tracks, notRun, errors);
        BenchJson.Write(Path.Combine(runFolder, JsonName), report);
        File.WriteAllText(Path.Combine(runFolder, MarkdownName), Markdown(report));
        return report;
    }

    internal static string Markdown(BenchReport report)
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# ReplayBuffer benchmark, {report.Created:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Run folder: `{report.RunFolder}`");
        md.AppendLine();

        Fps(md, report.Fps);
        Drift(md, report.Drift);
        Playback(md, report.Playback);
        Hotkey(md, report.Hotkey);
        Tracks(md, report.Tracks);

        if (report.Errors.Count > 0)
        {
            md.AppendLine("## Skipped files");
            md.AppendLine();
            foreach (var error in report.Errors)
            {
                md.AppendLine("- " + error);
            }

            md.AppendLine();
        }

        return md.ToString();
    }

    private static void Fps(StringBuilder md, FpsReport? fps)
    {
        md.AppendLine("## FPS loss (R4)");
        md.AppendLine();
        if (fps is null)
        {
            md.AppendLine("Not run.");
            md.AppendLine();
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"Game `{fps.Game}`, capture {fps.Capture.Size} at {fps.Capture.Fps} fps, CBR {fps.Capture.BitrateKbps} kbps; {fps.Passes} balanced passes of {fps.Seconds} s. Script summary: `{fps.Summary}`.");
        md.AppendLine();
        if (fps.Load is { Kind: GpuLoadInfo.Synthetic } load)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"**Load: Bench's synthetic GPU load** (`{load.Process}`), a borderless {load.Width}x{load.Height} popup on {load.Adapter}: target {Num(load.TargetFps)} fps, calibrated {Num(load.CalibratedFps)} fps at {load.Iterations} shader iterations, tearing {(load.Tearing == true ? "on" : "off")}. A real game's run is a person's confirmation, not-checked here.");
            md.AppendLine();
        }
        else if (fps.Load is { Kind: GpuLoadInfo.Game })
        {
            md.AppendLine("Load: the real game.");
            md.AppendLine();
        }

        if (fps.Aborted is not null)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"**Session ended early:** {fps.Aborted}");
            md.AppendLine();
        }

        var c = fps.CaptureMeans;

        // §6.3's dropped frames: the capture runs' queue drops and pacer-skipped slots, summed (interleaved runs only).
        var captureRuns = fps.Runs.Where(r => r.Condition == "capture" && r.Pass != "final").ToList();
        var queueDrops = captureRuns.Select(r => r.QueueDrops).OfType<long>().ToList();
        var skippedSlots = captureRuns.Select(r => r.SkippedSlots).OfType<long>().ToList();
        Table(md, [
            Row("FPS loss, capture against baseline", Pct(c?.FpsLossPercent), $"< {fps.TargetLossPercent} %", fps.Verdict),
            Row("Baseline average FPS", Num(fps.BaselineAvgFps), "", ""),
            Row("Capture average FPS", Num(c?.AvgFps), "", ""),
            Row("1 % low FPS, baseline / capture", $"{Num(fps.BaselineOnePercentLowFps)} / {Num(c?.OnePercentLowFps)}", "", ""),
            Row("1 % low loss", Pct(c?.OnePercentLowLossPercent), "", ""),
            Row("Fresh slots (new frames encoded)", Pct(c?.FreshSlotPercent), "", ""),
            Row("Duplicated slots", Pct(c?.DuplicatePercent), "", ""),
            Row("Queue drops / pacer-skipped slots (capture runs)", $"{(queueDrops.Count > 0 ? queueDrops.Sum().ToString(CultureInfo.InvariantCulture) : "n/a")} / {(skippedSlots.Count > 0 ? skippedSlots.Sum().ToString(CultureInfo.InvariantCulture) : "n/a")}", "", ""),
            Row("Capture-to-encoded latency, p99", Ms(c?.LatencyP99Ms), $"< {Num(1000.0 / fps.Capture.Fps)} ms", Mark(c?.LatencyP99Ms, 1000.0 / fps.Capture.Fps)),
            Row("Engine CPU, % of one core", Pct(c?.CpuPercentOneCore), "reported (§6.3: < 3 % of one CPU)", ""),
            Row("Managed allocations, steady state", c?.AllocBytesPerSecond is { } alloc ? $"{alloc:0} B/s" : "n/a", "~0", ""),
            Row("Gen2 GCs, steady state (all capture runs)", c?.Gen2Collections?.ToString(CultureInfo.InvariantCulture) ?? "n/a", "0", c?.Gen2Collections is { } g ? (g == 0 ? DriftMath.Pass : DriftMath.Miss) : ""),
        ]);

        md.AppendLine("| Run | Condition | Pass | Avg FPS | 1 % low | Loss | Fresh slots | Duplicates | Queue drops | Skipped slots | Latency p99 | CPU (1 core) | Alloc/s | GC 0/1/2 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in fps.Runs)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Run} | {r.Condition} | {r.Pass} | {Num(r.AvgFps)} | {Num(r.OnePercentLowFps)} | {Pct(r.FpsLossPercent)} | {(r.FreshSlots is { } f ? $"{f} ({Pct(r.FreshSlotPercent)})" : "")} | {Pct(r.DuplicatePercent)} | {r.QueueDrops} | {r.SkippedSlots} | {Ms(r.LatencyP99Ms)} | {Pct(r.CpuPercentOneCore)} | {Num(r.AllocBytesPerSecond)} | {r.GcSteady} |");
        }

        md.AppendLine();
    }

    private static void Drift(StringBuilder md, DriftReport? drift)
    {
        md.AppendLine("## A/V and audio-to-audio drift (R5)");
        md.AppendLine();
        if (drift is null)
        {
            md.AppendLine("Not run.");
            md.AppendLine();
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"{drift.Minutes:0.#} min of the marker window, {drift.Capture.Size} at {drift.Capture.Fps} fps, CBR {drift.Capture.BitrateKbps} kbps. Output Device `{drift.OutputDevice}`, Input Device `{drift.InputDevice}`. File `{drift.File}`.");
        md.AppendLine();
        if (drift.EngineError is not null)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"**The engine failed** (exit {drift.EngineExit}): {drift.EngineError}");
            md.AppendLine();
            return;
        }

        var rows = new List<string>();
        if (drift.Av is { } av)
        {
            var bucket = av.BucketSeconds == 60 ? "per-minute" : $"per-{av.BucketSeconds:0.#} s";
            rows.Add(Row($"A/V drift ({bucket} medians, largest change from the first)", Ms(av.DriftMs), $"< {Num(av.TargetMs)} ms", av.Verdict));
            rows.Add(Row("A/V regression slope", av.SlopeMsPerMinute is { } s ? $"{s:0.###} ms/min ({Ms(av.RegressionDriftMs)} over the run)" : "n/a", "", ""));
            rows.Add(Row("A/V absolute offset, first bucket", Ms(av.AbsoluteOffsetMs), "reported only", ""));
            rows.Add(Row("A/V single-burst spread (max − min)", Ms(av.SpreadMs), "reported only (±8 ms frame quantisation)", ""));
            rows.Add(Row("Flashes matched", $"{av.FlashesMatched} / {av.FlashesExpected} ({av.FlashMatchedFraction:P1})", "≥ 90 %", av.FlashMatchedFraction >= DriftMath.MinMatchedFraction ? DriftMath.Pass : DriftMath.Miss));
            rows.Add(Row("Bursts matched", $"{av.BurstsMatched} / {av.BurstsExpected} ({av.BurstMatchedFraction:P1})", "≥ 90 %", av.BurstMatchedFraction >= DriftMath.MinMatchedFraction ? DriftMath.Pass : DriftMath.Miss));
        }

        if (drift.AudioToAudio is { } a2a)
        {
            rows.Add(Row(
                @"Audio-to-audio bound, \|output error\| + \|input error\|",
                a2a.BoundSamples is { } b ? $"{b:0.##} samples ({Ms(a2a.BoundMs)})" : "n/a",
                $"< {a2a.TargetSamples:0} samples ({Num(a2a.TargetMs)} ms)",
                a2a.Verdict));
        }

        Table(md, rows);
        if (drift.Av?.Why is { } avWhy)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"A/V is inconclusive: {avWhy}.");
            md.AppendLine();
        }

        if (drift.AudioToAudio is { } sync)
        {
            if (sync.Why is { } why)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"Audio-to-audio is inconclusive: {why}.");
                md.AppendLine();
            }

            md.AppendLine("The audio-to-audio bound trusts each device's QPC stamps (checked in spike 1.2 and story 1.12): the mic doesn't hear the bursts.");
            md.AppendLine();
            md.AppendLine("| Device | Role | Error max | Error p99 | Raw stamp drift | Mean ratio | Gap fills | Trims | Step corrections |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var d in sync.Devices)
            {
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {d.Device} | {d.Role} | {d.ErrorMaxSamples:0.##} samples | {d.ErrorP99Samples:0.##} samples | {d.RawDriftPpm:0.###} ppm | {d.MeanRatioPpm:0.###} ppm | {d.GapFills} | {d.Trims} | {d.StepCorrections} |");
            }

            md.AppendLine();
        }

        if (drift.Tracks.Count > 0)
        {
            md.AppendLine("| Audio track | Samples | Length | Minus the video (its timestamp span) | Target (an AAC frame + a video frame) | Mark |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var t in drift.Tracks)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {t.Track}: {t.Title} | {t.Samples} | {t.DurationSeconds:0.###} s | {t.MinusVideoMs:0.##} ms | < {t.TargetMs:0.#} ms | {t.Mark} |");
            }

            md.AppendLine();
        }

        if (drift.Av is { Buckets.Count: > 0 } withBuckets)
        {
            md.AppendLine("| Bucket | From | Bursts | Median error |");
            md.AppendLine("|---|---|---|---|");
            foreach (var b in withBuckets.Buckets)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {b.Index + 1} | {b.StartSeconds:0} s | {b.Count} | {b.MedianMs:0.###} ms |");
            }

            md.AppendLine();
        }

        foreach (var warning in drift.Warnings)
        {
            md.AppendLine("- Warning: " + warning);
        }

        if (drift.Warnings.Count > 0)
        {
            md.AppendLine();
        }
    }

    private static void Playback(StringBuilder md, PlaybackReport? playback)
    {
        md.AppendLine("## fMP4 playback (NFR-4)");
        md.AppendLine();
        if (playback is null)
        {
            md.AppendLine("Not run.");
            md.AppendLine();
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"Clip `{playback.Clip}`: {playback.Seconds} s, H.264 {playback.Capture.Size} at {playback.Capture.Fps} fps, {playback.AudioTracks} audio track(s). HEVC and AV1 are not checked.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Capture border shown around the marker window (§7.4 `IsBorderRequired`): **{playback.CaptureBorder ?? "not-checked"}**.");
        md.AppendLine();
        if (playback.BorderProbe is { } probe)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"From screen grabs of the band {BorderProbe.Margin} px each side of the window's edge: {probe.YellowBefore} yellow pixels before the capture, {probe.YellowDuring} during it, of {probe.RingPixels}{(probe.Error is null ? "" : $"; {probe.Error}")}. Images: `{probe.BeforeImage}`, `{probe.DuringImage}`.");
            md.AppendLine();
        }

        if (playback.Auto == true)
        {
            md.AppendLine("An unattended `--auto` run: Chrome (headless) and Windows Media Player (its `WMPlayer.OCX` engine) were scripted; A/V sync in a player and every other player need a person.");
            md.AppendLine();
        }

        md.AppendLine("| Player | Plays | A/V in sync | Audio tracks | Notes | Mark |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var p in playback.Players)
        {
            // A pass needs both answers yes; a "no" to either is a miss; a skipped sync answer is not checked.
            // A scripted check sees playback only: it passes that, and leaves A/V sync to a person.
            var mark = p.Status != "checked" ? "not-checked"
                : p.Plays == false || p.AvInSync == false ? DriftMath.Miss
                : p.Plays == true && p.AvInSync == true ? DriftMath.Pass
                : p.Method == PlayerScripts.Scripted && p.Plays == true ? "plays: pass; A/V sync: not-checked"
                : "not-checked";
            md.AppendLine(CultureInfo.InvariantCulture, $"| {p.Player} | {YesNo(p.Plays)} | {YesNo(p.AvInSync)} | {Cell(p.AudioTracks)} | {Cell(p.Notes)} | {mark} |");
        }

        md.AppendLine();
    }

    private static void Hotkey(StringBuilder md, HotkeyReport? hotkey)
    {
        md.AppendLine("## Hotkey press-to-stamp latency (R11)");
        md.AppendLine();
        if (hotkey is null)
        {
            md.AppendLine("Not run.");
            md.AppendLine();
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"`{hotkey.Key}`, {hotkey.Presses} of {hotkey.PressesRequested} presses, from the low-level hook's key-down to the engine's `WM_HOTKEY` stamp.");
        md.AppendLine();
        if (hotkey.Injected == true)
        {
            md.AppendLine("**Presses injected** by Bench with `SendInput` (an unattended run); real key presses are not-checked.");
            md.AppendLine();
        }
        else if (hotkey.Injected == false || hotkey.Timings.Count > 0)
        {
            var injected = hotkey.Timings.Count(t => t.Injected);
            md.AppendLine(injected == 0
                ? "Real key presses."
                : string.Create(CultureInfo.InvariantCulture, $"Real key presses; {injected} of {hotkey.Timings.Count} came in flagged injected."));
            md.AppendLine();
        }

        if (hotkey.Error is not null)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"**Note:** {hotkey.Error}");
            md.AppendLine();
        }

        Table(md, [
            Row("Every press", Ms(hotkey.MaxMs) + " max", $"< {hotkey.TargetMs:0} ms", hotkey.Verdict),
            Row("p50", Ms(hotkey.P50Ms), $"< {hotkey.TargetMs:0} ms; old bar {hotkey.OldBarMs:0} ms", OldBar(hotkey.P50Ms, hotkey)),
            Row("p99", Ms(hotkey.P99Ms), $"< {hotkey.TargetMs:0} ms; old bar {hotkey.OldBarMs:0} ms", OldBar(hotkey.P99Ms, hotkey)),
            Row("Mean", Ms(hotkey.MeanMs), "", ""),
        ]);
    }

    private static void Tracks(StringBuilder md, TracksReport? tracks)
    {
        md.AppendLine("## Named Tracks (R3, R7)");
        md.AppendLine();
        if (tracks is null)
        {
            md.AppendLine("Not run.");
            md.AppendLine();
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture,
            $"{tracks.Minutes:0.#} min of the marker window, {tracks.Capture.Size} at {tracks.Capture.Fps} fps, CBR {tracks.Capture.BitrateKbps} kbps, with the Track Layout Everything = A + B + Mic, Game = A, Chat = B, Mic = Mic. Device A `{tracks.DeviceA}`, device B `{tracks.DeviceB}`{(tracks.BurstPhaseSeconds is { } phase ? $" (its bursts {phase:0.###} s after A's)" : "")}, Mic `{tracks.InputDevice}`; each plays at {tracks.ToneDbfs:0.#} dBFS. File `{tracks.File}`.");
        md.AppendLine();
        if (tracks.EngineError is not null)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"**The engine failed** (exit {tracks.EngineExit}): {tracks.EngineError}");
            md.AppendLine();
            return;
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"Verdict: **{tracks.Verdict}**. VLC is scripted through its HTTP interface; DaVinci Resolve needs a person.");
        md.AppendLine();
        md.AppendLine("| Check | Value | Target | Mark |");
        md.AppendLine("|---|---|---|---|");
        foreach (var check in tracks.Checks)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {check.Check} | {Cell(check.Figure)} | {Cell(check.Target)} | {check.Mark} |");
        }

        md.AppendLine();
        if (tracks.Tracks.Count > 0)
        {
            md.AppendLine("| Track | Title | Default | Length | Onsets | Burst level | Peak |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var t in tracks.Tracks)
            {
                var onsets = t.Onsets is { } n ? $"{n} (expected ≥ {t.ExpectedOnsets})" : "";
                var level = t.BurstLevelDbfs is { } l ? string.Create(CultureInfo.InvariantCulture, $"{l:0.##} dBFS") : "";
                md.AppendLine(CultureInfo.InvariantCulture, $"| {t.Index + 1} | {Cell(t.Title)} | {YesNo(t.Default)} | {t.DurationSeconds:0.###} s | {onsets} | {level} | {Num(t.Peak)} |");
            }

            md.AppendLine();
        }

        foreach (var warning in tracks.Warnings)
        {
            md.AppendLine("- Warning: " + warning);
        }

        if (tracks.Warnings.Count > 0)
        {
            md.AppendLine();
        }
    }

    private static string OldBar(double? value, HotkeyReport hotkey) =>
        value is not { } v ? "" : $"{Mark(v, hotkey.TargetMs)}; old bar {(v < hotkey.OldBarMs ? "met" : "not met")}";

    private static T? Load<T>(string folder, string file, string name, List<string> errors, List<string> notRun)
        where T : class
    {
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
        {
            notRun.Add(name);
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), BenchJson.Options)
                ?? throw new JsonException("the file holds null");

            // Valid JSON of the wrong shape (e.g. {}) leaves required members null.
            return MissingMember(value) is { } missing ? throw new JsonException($"it has no {missing}") : value;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"`{file}` is malformed or unreadable and was skipped: {e.Message}");
            notRun.Add(name);
            return null;
        }
    }

    /// <summary>
    /// The first member declared non-nullable that is null, in <paramref name="value"/> or in the Bench records and
    /// lists it holds, as a JSON path (e.g. <c>av.buckets</c>); null when every required member is there.
    /// </summary>
    internal static string? MissingMember(object value, string path = "")
    {
        var nullability = new NullabilityInfoContext();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.PropertyType.IsValueType)
            {
                continue;
            }

            var name = path + JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var item = property.GetValue(value);
            if (item is null)
            {
                if (nullability.Create(property).ReadState == NullabilityState.NotNull)
                {
                    return name;
                }

                continue;
            }

            if (item is string)
            {
                continue;
            }

            if (item is System.Collections.IEnumerable list)
            {
                var i = 0;
                foreach (var element in list)
                {
                    if (element is null)
                    {
                        return $"{name}[{i}]";
                    }

                    if (IsBenchRecord(element.GetType()) && MissingMember(element, $"{name}[{i}].") is { } inner)
                    {
                        return inner;
                    }

                    i++;
                }

                continue;
            }

            if (IsBenchRecord(item.GetType()) && MissingMember(item, name + ".") is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool IsBenchRecord(Type type) => !type.IsValueType && type.Namespace == typeof(Report).Namespace;

    private static void Table(StringBuilder md, IReadOnlyList<string> rows)
    {
        md.AppendLine("| Figure | Value | Target | Mark |");
        md.AppendLine("|---|---|---|---|");
        foreach (var row in rows)
        {
            md.AppendLine(row);
        }

        md.AppendLine();
    }

    private static string Row(string figure, string value, string target, string mark) => $"| {figure} | {value} | {target} | {mark} |";

    private static string Mark(double? value, double below) => value is { } v ? (v < below ? DriftMath.Pass : DriftMath.Miss) : "";

    private static string Num(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Pct(double? value) => value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) + " %" : "n/a";

    private static string Ms(double? value) => value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) + " ms" : "n/a";

    private static string YesNo(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "",
    };

    private static string Cell(string? text) => (text ?? "").Replace("|", "/", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
