using System.Text.Json;

namespace ReplayBuffer.Bench;

/// <summary>What the A/V analysis needs: the markers found in the file, and their source log.</summary>
/// <param name="FlashFileTimes">Each detected flash's frame time in the file, s, ascending.</param>
/// <param name="OnsetFileTimes">Each detected burst onset's time in the file, s, ascending.</param>
/// <param name="FlashQpcs">The marker window's flash stamps, ascending.</param>
/// <param name="BurstQpcs">The tone player's burst stamps, ascending.</param>
/// <param name="AnchorQpc">An estimate of the QPC at file time 0, good to well under 0.5 s.</param>
/// <param name="VideoSpan">The video's first and last frame time, s.</param>
/// <param name="AudioSpan">The burst track's first and last sample time, s.</param>
/// <param name="FrameMs">One video frame at the output fps: the target.</param>
/// <param name="BucketSeconds">The length of a median bucket; a minute by default.</param>
internal sealed record AvInput(
    IReadOnlyList<double> FlashFileTimes,
    IReadOnlyList<double> OnsetFileTimes,
    IReadOnlyList<long> FlashQpcs,
    IReadOnlyList<long> BurstQpcs,
    long QpcFrequency,
    long AnchorQpc,
    (double Start, double End) VideoSpan,
    (double Start, double End) AudioSpan,
    double FrameMs,
    double BucketSeconds = 60);

/// <summary>One bucket of burst errors.</summary>
internal sealed record DriftBucket(int Index, double StartSeconds, int Count, double MedianMs);

/// <summary>
/// The A/V result. <see cref="DriftMs"/> is the verdict figure: the largest change of a bucket's
/// median from the first bucket's. <see cref="AbsoluteOffsetMs"/> is the first bucket's median, the
/// constant pipeline offset that the drift leaves out.
/// </summary>
internal sealed record AvResult(
    int FlashesLogged,
    int FlashesExpected,
    int FlashesDetected,
    int FlashesMatched,
    double FlashMatchedFraction,
    int BurstsLogged,
    int BurstsExpected,
    int OnsetsDetected,
    int BurstsMatched,
    double BurstMatchedFraction,
    int Pairs,
    double VideoMappingMs,
    double AudioMappingMs,
    double BucketSeconds,
    IReadOnlyList<DriftBucket> Buckets,
    double? AbsoluteOffsetMs,
    double? DriftMs,
    double? SlopeMsPerMinute,
    double? RegressionDriftMs,
    double? SpreadMs,
    double TargetMs,
    string Verdict,
    string? Why);

/// <summary>One device's steering, from the drift run's stats file.</summary>
internal sealed record DeviceSync(
    string Role,
    string Device,
    double ErrorMaxSamples,
    double ErrorP99Samples,
    double RawDriftPpm,
    double MeanRatioPpm,
    long GapFills,
    long Trims,
    long StepCorrections,
    long SettledPackets);

/// <summary>
/// Audio-to-audio, bounded from the stats (no acoustic coupling): |output error| + |input error|,
/// each a device's content position against its own QPC stamps, so it trusts the stamps.
/// </summary>
internal sealed record AudioToAudioResult(
    IReadOnlyList<DeviceSync> Devices,
    double? BoundSamples,
    double? BoundMs,
    double TargetSamples,
    double TargetMs,
    string Verdict,
    string? Why);

/// <summary>The pure part of the drift analysis.</summary>
internal static class DriftMath
{
    public const double MinMatchedFraction = 0.9;
    public const string Pass = "pass";
    public const string Miss = "miss";
    public const string Inconclusive = "inconclusive";

    /// <summary>A detected marker counts as a logged one within this of where the mapping puts it.</summary>
    private const double IdentityToleranceSeconds = 0.25;

    /// <summary>
    /// Per burst k: <c>error_k = (onset − flash frame time) in the file − (burst QPC − flash QPC) at source</c>,
    /// pairing each burst with the logged flash nearest it (within ±0.5 s). Logged markers are found in the
    /// file through the anchor, refined per stream by the median residual (which takes the stream's constant
    /// pipeline offset out of the mapping, so it only identifies markers and never enters an error).
    /// </summary>
    public static AvResult AnalyzeAv(AvInput input)
    {
        var f = (double)input.QpcFrequency;
        double FileTime(long qpc) => (qpc - input.AnchorQpc) / f;

        var flashTimes = input.FlashQpcs.Select(FileTime).ToArray();
        var burstTimes = input.BurstQpcs.Select(FileTime).ToArray();

        var videoShift = MappingShift(input.FlashFileTimes, flashTimes);
        var audioShift = MappingShift(input.OnsetFileTimes, burstTimes);

        // Logged flash j → its frame time in the file, when found.
        var flashInFile = Identify(flashTimes, videoShift, input.FlashFileTimes, input.VideoSpan, out var flashesExpected);
        var burstInFile = Identify(burstTimes, audioShift, input.OnsetFileTimes, input.AudioSpan, out var burstsExpected);

        var flashesMatched = flashInFile.Count(t => t is not null);
        var burstsMatched = burstInFile.Count(t => t is not null);
        var flashFraction = flashesExpected == 0 ? 0 : (double)flashesMatched / flashesExpected;
        var burstFraction = burstsExpected == 0 ? 0 : (double)burstsMatched / burstsExpected;

        var sortedFlashQpcs = input.FlashQpcs.ToArray();
        var errors = new List<(double Time, double Ms)>();
        for (var k = 0; k < input.BurstQpcs.Count; k++)
        {
            if (burstInFile[k] is not { } onset)
            {
                continue;
            }

            var j = Nearest(sortedFlashQpcs, input.BurstQpcs[k]);
            if (j < 0 || Math.Abs(input.BurstQpcs[k] - sortedFlashQpcs[j]) > f / 2 || flashInFile[j] is not { } flash)
            {
                continue;
            }

            var source = (input.BurstQpcs[k] - sortedFlashQpcs[j]) / f;
            errors.Add((burstTimes[k], ((onset - flash) - source) * 1000));
        }

        var buckets = Buckets(errors, input.BucketSeconds);
        double? absolute = buckets.Count > 0 ? buckets[0].MedianMs : null;
        double? drift = buckets.Count > 1 ? buckets.Max(b => Math.Abs(b.MedianMs - buckets[0].MedianMs)) : null;
        var (slope, span) = errors.Count >= 2 ? Slope(errors) : (null, 0);
        double? spread = errors.Count > 0 ? errors.Max(e => e.Ms) - errors.Min(e => e.Ms) : null;

        string verdict;
        string? why = null;
        if (flashFraction < MinMatchedFraction || burstFraction < MinMatchedFraction)
        {
            verdict = Inconclusive;
            why = $"matched {flashFraction:P0} of flashes and {burstFraction:P0} of bursts; needs {MinMatchedFraction:P0} of each";
        }
        else if (drift is null)
        {
            verdict = Inconclusive;
            why = $"fewer than two {input.BucketSeconds:0} s buckets of matched bursts";
        }
        else
        {
            verdict = drift < input.FrameMs ? Pass : Miss;
        }

        return new AvResult(
            input.FlashQpcs.Count,
            flashesExpected,
            input.FlashFileTimes.Count,
            flashesMatched,
            Math.Round(flashFraction, 4),
            input.BurstQpcs.Count,
            burstsExpected,
            input.OnsetFileTimes.Count,
            burstsMatched,
            Math.Round(burstFraction, 4),
            errors.Count,
            Math.Round(videoShift * 1000, 3),
            Math.Round(audioShift * 1000, 3),
            input.BucketSeconds,
            buckets,
            Round(absolute),
            Round(drift),
            Round(slope),
            slope is { } s ? Round(s * span / 60) : null,
            Round(spread),
            Math.Round(input.FrameMs, 3),
            verdict,
            why);
    }

    /// <summary>Audio-to-audio from the stats file's <c>audio[]</c>: the output (loopback) and input devices.</summary>
    public static AudioToAudioResult AudioToAudio(JsonElement stats, int fps)
    {
        var targetSamples = (double)OnsetDetector.SampleRate / fps;
        var targetMs = 1000.0 / fps;
        var devices = new List<DeviceSync>();
        if (stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in audio.EnumerateArray())
            {
                if (!entry.TryGetProperty("resampler", out var resampler) || resampler.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var drift = entry.TryGetProperty("stamps", out var stamps) && stamps.TryGetProperty("drift", out var d) ? Number(d, "ppm") : 0;
                var timeline = entry.TryGetProperty("timeline", out var t) ? t : default;
                devices.Add(new DeviceSync(
                    Text(entry, "role"),
                    Text(entry, "device"),
                    Number(resampler, "errorMaxSamples"),
                    Number(resampler, "errorP99Samples"),
                    Math.Round(drift, 3),
                    Math.Round(Number(resampler, "meanRatioPpm"), 3),
                    Whole(timeline, "gapFills"),
                    Whole(timeline, "trims"),
                    Whole(resampler, "stepCorrections"),
                    Whole(resampler, "settledPackets")));
            }
        }

        var output = devices.FirstOrDefault(d => d.Role == "output");
        var input = devices.FirstOrDefault(d => d.Role == "input");
        if (output is null || input is null)
        {
            var missing = string.Join(" and ", new[] { output is null ? "output" : null, input is null ? "input" : null }.OfType<string>());
            return new AudioToAudioResult(devices, null, null, targetSamples, Math.Round(targetMs, 3), Inconclusive, $"no {missing} device with resampler stats in the stats file");
        }

        var bound = Math.Abs(output.ErrorMaxSamples) + Math.Abs(input.ErrorMaxSamples);
        return new AudioToAudioResult(
            devices,
            Math.Round(bound, 2),
            Math.Round(bound * 1000 / OnsetDetector.SampleRate, 3),
            targetSamples,
            Math.Round(targetMs, 3),
            bound < targetSamples ? Pass : Miss,
            null);
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("No values.", nameof(values));
        }

        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The nearest-rank percentile.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("No values.", nameof(values));
        }

        var sorted = values.Order().ToArray();
        var rank = Math.Max(1, (int)Math.Ceiling(p / 100 * sorted.Length));
        return sorted[Math.Min(rank, sorted.Length) - 1];
    }

    /// <summary>The least-squares slope of ms against time, per minute, and the time span covered (s).</summary>
    public static (double? PerMinute, double SpanSeconds) Slope(IReadOnlyList<(double Time, double Ms)> points)
    {
        var meanT = points.Average(p => p.Time);
        var meanY = points.Average(p => p.Ms);
        double sxx = 0, sxy = 0;
        foreach (var (t, y) in points)
        {
            sxx += (t - meanT) * (t - meanT);
            sxy += (t - meanT) * (y - meanY);
        }

        var span = points.Max(p => p.Time) - points.Min(p => p.Time);
        return sxx <= 0 ? (null, span) : (sxy / sxx * 60, span);
    }

    /// <summary>
    /// Buckets of <paramref name="bucketSeconds"/> from the first error's time; a bucket needs a quarter of
    /// its seconds in errors (at least 3), so a short last bucket doesn't count.
    /// </summary>
    public static List<DriftBucket> Buckets(IReadOnlyList<(double Time, double Ms)> errors, double bucketSeconds)
    {
        var buckets = new List<DriftBucket>();
        if (errors.Count == 0)
        {
            return buckets;
        }

        var start = errors.Min(e => e.Time);
        var minCount = Math.Max(3, (int)(bucketSeconds / 4));
        foreach (var group in errors.GroupBy(e => (int)Math.Floor((e.Time - start) / bucketSeconds)).OrderBy(g => g.Key))
        {
            var values = group.Select(e => e.Ms).ToList();
            if (values.Count >= minCount)
            {
                buckets.Add(new DriftBucket(group.Key, Math.Round(group.Key * bucketSeconds, 3), values.Count, Math.Round(Median(values), 3)));
            }
        }

        return buckets;
    }

    /// <summary>
    /// The median of (detected − nearest logged) times, s: the stream's constant offset from the anchor
    /// mapping. Zero when nothing was detected.
    /// </summary>
    private static double MappingShift(IReadOnlyList<double> detected, double[] logged)
    {
        if (detected.Count == 0 || logged.Length == 0)
        {
            return 0;
        }

        var residuals = new List<double>(detected.Count);
        foreach (var time in detected)
        {
            var j = Nearest(logged, time);
            residuals.Add(time - logged[j]);
        }

        return Median(residuals);
    }

    /// <summary>
    /// For each logged marker, the detected time within <see cref="IdentityToleranceSeconds"/> of where the
    /// shifted mapping puts it, or null. <paramref name="expected"/> counts the logged markers inside the span.
    /// </summary>
    private static double?[] Identify(double[] logged, double shift, IReadOnlyList<double> detected, (double Start, double End) span, out int expected)
    {
        var found = new double?[logged.Length];
        var detectedArray = detected as double[] ?? [.. detected];
        expected = 0;
        for (var j = 0; j < logged.Length; j++)
        {
            var at = logged[j] + shift;
            if (at < span.Start + 0.1 || at > span.End - 0.2)
            {
                continue;
            }

            expected++;
            if (detectedArray.Length == 0)
            {
                continue;
            }

            var i = Nearest(detectedArray, at);
            if (Math.Abs(detectedArray[i] - at) <= IdentityToleranceSeconds)
            {
                found[j] = detectedArray[i];
            }
        }

        return found;
    }

    /// <summary>The index of the element of ascending <paramref name="sorted"/> nearest <paramref name="value"/>; −1 when empty.</summary>
    internal static int Nearest(double[] sorted, double value)
    {
        if (sorted.Length == 0)
        {
            return -1;
        }

        var i = Array.BinarySearch(sorted, value);
        if (i >= 0)
        {
            return i;
        }

        i = ~i;
        if (i == 0)
        {
            return 0;
        }

        if (i == sorted.Length)
        {
            return sorted.Length - 1;
        }

        return value - sorted[i - 1] <= sorted[i] - value ? i - 1 : i;
    }

    internal static int Nearest(long[] sorted, long value)
    {
        if (sorted.Length == 0)
        {
            return -1;
        }

        var i = Array.BinarySearch(sorted, value);
        if (i >= 0)
        {
            return i;
        }

        i = ~i;
        if (i == 0)
        {
            return 0;
        }

        if (i == sorted.Length)
        {
            return sorted.Length - 1;
        }

        return value - sorted[i - 1] <= sorted[i] - value ? i - 1 : i;
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 3) : null;

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static double Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static long Whole(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
}
