using System.Globalization;

namespace ReplayBuffer.Bench;

/// <summary>One audio stream as ffprobe reports it: its title, default flag and the span of its packets.</summary>
internal sealed record TrackProbe(string? Title, bool Default, double StartSeconds, double DurationSeconds);

/// <summary>One decoded audio stream: its burst onsets (file time, s), each burst's level (dBFS) and its peak |sample|.</summary>
internal sealed record TrackSignal(IReadOnlyList<double> OnsetSeconds, IReadOnlyList<double> BurstLevelsDbfs, double Peak);

/// <summary>What VLC's HTTP interface listed. <see cref="Found"/> is false when no vlc.exe was found: the check is then <c>not-checked</c>.</summary>
internal sealed record VlcListing(bool Found, IReadOnlyList<string>? Labels, string? Error);

/// <summary>One audio stream's figures in <c>tracks.json</c>.</summary>
/// <param name="Onsets">Burst onsets found; null for a stream not decoded (Mic).</param>
/// <param name="ExpectedOnsets">At least this many whole bursts fit in the stream: its whole seconds less one.</param>
/// <param name="BurstLevelDbfs">The median burst level, dBFS (RMS of 5 to 45 ms after each onset, × √2).</param>
internal sealed record TrackFigures(
    int Index,
    string? Title,
    bool Default,
    double DurationSeconds,
    int? Onsets,
    int? ExpectedOnsets,
    double? BurstLevelDbfs,
    double? Peak);

/// <summary>Game's and Chat's onsets against Everything's.</summary>
internal sealed record SyncFigures(
    int GameOnsets,
    int GameExpected,
    int ChatOnsets,
    int ChatExpected,
    int Matched,
    double MatchedFraction,
    double? MaxMs,
    double? MedianMs);

/// <summary>One check of <c>tracks.json</c>: a figure beside its target, marked pass, miss or not-checked.</summary>
internal sealed record NamedTrackCheck(string Check, string Figure, string Target, string Mark);

/// <summary>The checks' outcome.</summary>
internal sealed record TracksAnalysis(IReadOnlyList<TrackFigures> Tracks, SyncFigures? Sync, IReadOnlyList<NamedTrackCheck> Checks, string Verdict);

/// <summary>
/// The pure part of <c>bench tracks</c>: the Track names and order, Track 1 as the only default, each Track's
/// length against the video's, Game and Chat in sync with Everything, each single-device Track at its source's
/// level, Everything never over full scale, and VLC's listing of the same names.
/// </summary>
internal static class TrackAnalysis
{
    public const string PassVerdict = "pass";
    public const string FailVerdict = "fail";
    public const string FailedVerdict = "failed";
    public const string NotChecked = "not-checked";

    public static readonly IReadOnlyList<string> ExpectedTitles = ["Everything", "Game", "Chat", "Mic"];

    public const int Everything = 0;
    public const int Game = 1;
    public const int Chat = 2;

    public const double DurationToleranceSeconds = 0.1;
    public const double MatchWindowMs = 5;
    public const double SyncBoundMs = 1;
    public const double MinOnsetFraction = 0.9;
    public const double MinMatchedFraction = 0.95;
    public const double LevelToleranceDb = 0.5;

    /// <summary>AAC coding tolerance over full scale, as story 2.2's test.</summary>
    public const double MaxPeak = 1.05;

    public static TracksAnalysis Evaluate(double videoDurationSeconds, IReadOnlyList<TrackProbe> audio, IReadOnlyList<TrackSignal?> signals, double toneDbfs, VlcListing? vlc)
    {
        TrackSignal? Signal(int i) => i < signals.Count ? signals[i] : null;

        var tracks = audio.Select((probe, i) =>
        {
            var signal = Signal(i);
            var level = signal is { BurstLevelsDbfs.Count: > 0 } s ? Math.Round(DriftMath.Median(s.BurstLevelsDbfs), 3) : (double?)null;
            return new TrackFigures(
                i,
                probe.Title,
                probe.Default,
                Math.Round(probe.DurationSeconds, 4),
                signal?.OnsetSeconds.Count,
                signal is null ? null : ExpectedOnsets(probe.DurationSeconds),
                level,
                signal is null ? null : Math.Round(signal.Peak, 4));
        }).ToList();

        var checks = new List<NamedTrackCheck>
        {
            Titles(audio),
            Default(audio),
            Durations(videoDurationSeconds, audio),
        };

        SyncFigures? sync = null;
        if (audio.Count > Chat && Signal(Everything) is { } everything && Signal(Game) is { } game && Signal(Chat) is { } chat)
        {
            sync = Sync(everything.OnsetSeconds, game.OnsetSeconds, chat.OnsetSeconds, ExpectedOnsets(audio[Game].DurationSeconds), ExpectedOnsets(audio[Chat].DurationSeconds));
            checks.Add(SyncCheck(sync));
        }
        else
        {
            checks.Add(new NamedTrackCheck("sync", "Everything, Game and Chat not all decoded", $"≤ {Num(SyncBoundMs)} ms", DriftMath.Miss));
        }

        checks.Add(Level(tracks, toneDbfs));
        checks.Add(FullScale(tracks));
        checks.Add(Vlc(audio, vlc));

        var verdict = checks.Any(c => c.Mark == DriftMath.Miss) ? FailVerdict : PassVerdict;
        return new TracksAnalysis(tracks, sync, checks, verdict);
    }

    /// <summary>Bursts come once a second, so a stream of D seconds holds at least ⌊D⌋ − 1 whole bursts.</summary>
    public static int ExpectedOnsets(double durationSeconds) => Math.Max(0, (int)Math.Floor(durationSeconds) - 1);

    /// <summary>
    /// Each Game and Chat onset matched to the nearest Everything onset within ±<see cref="MatchWindowMs"/>; the
    /// matched fraction and the max and median |Δ| of the matched ones.
    /// </summary>
    public static SyncFigures Sync(IReadOnlyList<double> everything, IReadOnlyList<double> game, IReadOnlyList<double> chat, int gameExpected, int chatExpected)
    {
        var sorted = everything.Order().ToArray();
        var deltas = new List<double>();
        foreach (var onset in game.Concat(chat))
        {
            var j = DriftMath.Nearest(sorted, onset);
            if (j >= 0)
            {
                var ms = Math.Abs(sorted[j] - onset) * 1000;
                if (ms <= MatchWindowMs)
                {
                    deltas.Add(ms);
                }
            }
        }

        var total = game.Count + chat.Count;
        return new SyncFigures(
            game.Count,
            gameExpected,
            chat.Count,
            chatExpected,
            deltas.Count,
            total == 0 ? 0 : Math.Round((double)deltas.Count / total, 4),
            deltas.Count > 0 ? Math.Round(deltas.Max(), 4) : null,
            deltas.Count > 0 ? Math.Round(DriftMath.Median(deltas), 4) : null);
    }

    private static NamedTrackCheck Titles(IReadOnlyList<TrackProbe> audio)
    {
        var titles = audio.Select(a => a.Title ?? "(untitled)").ToList();
        var ok = titles.SequenceEqual(ExpectedTitles, StringComparer.Ordinal);
        return new NamedTrackCheck("titles", Join(titles), Join(ExpectedTitles), ok ? DriftMath.Pass : DriftMath.Miss);
    }

    private static NamedTrackCheck Default(IReadOnlyList<TrackProbe> audio)
    {
        var defaults = audio.Select((a, i) => (a, i)).Where(x => x.a.Default).Select(x => (x.i + 1).ToString(CultureInfo.InvariantCulture)).ToList();
        var ok = audio.Count > 0 && audio[0].Default && audio.Skip(1).All(a => !a.Default);
        var figure = defaults.Count == 0 ? "no Track default" : "default: Track " + string.Join(", ", defaults);
        return new NamedTrackCheck("default", figure, "only Track 1", ok ? DriftMath.Pass : DriftMath.Miss);
    }

    private static NamedTrackCheck Durations(double video, IReadOnlyList<TrackProbe> audio)
    {
        if (audio.Count == 0)
        {
            return new NamedTrackCheck("durations", "no audio streams", $"each within {Num(DurationToleranceSeconds)} s of the video", DriftMath.Miss);
        }

        var worst = audio.Max(a => Math.Abs(a.DurationSeconds - video));
        var figure = string.Create(CultureInfo.InvariantCulture, $"video {video:0.###} s; Tracks ")
            + string.Join(", ", audio.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.DurationSeconds:0.###} s")))
            + string.Create(CultureInfo.InvariantCulture, $"; worst |Δ| {worst:0.###} s");
        return new NamedTrackCheck("durations", figure, $"each within {Num(DurationToleranceSeconds)} s of the video",
            worst <= DurationToleranceSeconds ? DriftMath.Pass : DriftMath.Miss);
    }

    private static NamedTrackCheck SyncCheck(SyncFigures sync)
    {
        var gameShort = sync.GameOnsets < sync.GameExpected * MinOnsetFraction;
        var chatShort = sync.ChatOnsets < sync.ChatExpected * MinOnsetFraction;
        var ok = !gameShort && !chatShort && sync.MatchedFraction >= MinMatchedFraction && sync.MaxMs is { } max && max <= SyncBoundMs;
        var figure = string.Create(
            CultureInfo.InvariantCulture,
            $"max |Δ| {Ms(sync.MaxMs)}, median {Ms(sync.MedianMs)}; matched {sync.Matched} of {sync.GameOnsets + sync.ChatOnsets} ({sync.MatchedFraction:P1}); onsets Game {sync.GameOnsets}/{sync.GameExpected}, Chat {sync.ChatOnsets}/{sync.ChatExpected}");
        var target = string.Create(
            CultureInfo.InvariantCulture,
            $"≤ {Num(SyncBoundMs)} ms; ≥ {MinMatchedFraction:P0} matched within ±{Num(MatchWindowMs)} ms; ≥ {MinOnsetFraction:P0} of expected onsets");
        return new NamedTrackCheck("sync", figure, target, ok ? DriftMath.Pass : DriftMath.Miss);
    }

    private static NamedTrackCheck Level(IReadOnlyList<TrackFigures> tracks, double toneDbfs)
    {
        var target = string.Create(CultureInfo.InvariantCulture, $"{toneDbfs:0.#} dBFS ± {LevelToleranceDb:0.#} dB");
        var parts = new List<string>();
        var ok = true;
        foreach (var i in new[] { Game, Chat })
        {
            var name = ExpectedTitles[i];
            var level = i < tracks.Count ? tracks[i].BurstLevelDbfs : null;
            parts.Add(level is { } l ? string.Create(CultureInfo.InvariantCulture, $"{name} {l:0.##} dBFS") : $"{name} n/a");
            ok &= level is { } v && Math.Abs(v - toneDbfs) <= LevelToleranceDb;
        }

        return new NamedTrackCheck("level", string.Join(", ", parts), target, ok ? DriftMath.Pass : DriftMath.Miss);
    }

    private static NamedTrackCheck FullScale(IReadOnlyList<TrackFigures> tracks)
    {
        var target = string.Create(CultureInfo.InvariantCulture, $"Everything's peak ≤ {MaxPeak}");
        if (tracks.Count == 0 || tracks[Everything].Peak is not { } peak)
        {
            return new NamedTrackCheck("fullScale", "Everything not decoded", target, DriftMath.Miss);
        }

        var level = tracks[Everything].BurstLevelDbfs is { } l ? string.Create(CultureInfo.InvariantCulture, $"; median burst {l:0.##} dBFS") : "";
        return new NamedTrackCheck("fullScale", string.Create(CultureInfo.InvariantCulture, $"peak {peak:0.####}{level}"), target,
            peak <= MaxPeak ? DriftMath.Pass : DriftMath.Miss);
    }

    private static NamedTrackCheck Vlc(IReadOnlyList<TrackProbe> audio, VlcListing? vlc)
    {
        var titles = audio.Select(a => a.Title ?? "").ToList();
        var target = "the file's titles, in order";
        if (vlc is null || !vlc.Found)
        {
            return new NamedTrackCheck("vlc", vlc?.Error ?? "VLC not found", target, NotChecked);
        }

        if (vlc.Labels is not { Count: > 0 } labels)
        {
            return new NamedTrackCheck("vlc", "VLC listed no audio streams" + (vlc.Error is null ? "" : $": {vlc.Error}"), target, DriftMath.Miss);
        }

        var figure = Join(labels) + (vlc.Error is null ? "" : $" ({vlc.Error})");
        return new NamedTrackCheck("vlc", figure, target, labels.SequenceEqual(titles, StringComparer.Ordinal) ? DriftMath.Pass : DriftMath.Miss);
    }

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Ms(double? value) => value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) + " ms" : "n/a";
}

/// <summary>
/// Streams one decoded audio stream (48 kHz stereo f32, in any chunk sizes) through the onset detector, and
/// measures each burst's level (the RMS of the mono samples 5 to 45 ms after its onset, × √2, so a sine reads
/// its peak) and the stream's peak |sample|.
/// </summary>
internal sealed class TrackSignalScanner
{
    public const int LevelFromSamples = OnsetDetector.SampleRate * 5 / 1000;
    public const int LevelToSamples = OnsetDetector.SampleRate * 45 / 1000;

    private readonly OnsetDetector _detector;
    private readonly List<(long From, long To, double Sum)> _windows = [];
    private readonly List<double> _levels = [];
    private float[] _mono = new float[8192];
    private long _sample;
    private double _peak;

    public TrackSignalScanner(double burstDbfs) => _detector = new OnsetDetector(burstDbfs);

    public long Samples => _sample;

    public void FeedStereo(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        if (_mono.Length < frames)
        {
            _mono = new float[frames];
        }

        for (var n = 0; n < frames; n++)
        {
            var left = interleaved[2 * n];
            var right = interleaved[(2 * n) + 1];
            _peak = Math.Max(_peak, Math.Max(Math.Abs(left), Math.Abs(right)));
            _mono[n] = 0.5f * (left + right);
        }

        var mono = _mono.AsSpan(0, frames);
        var before = _detector.Onsets.Count;
        _detector.Process(mono);

        // A window starts after its onset, which is in this chunk or an earlier one, so no sample of it is missed.
        for (var k = before; k < _detector.Onsets.Count; k++)
        {
            var onset = _detector.Onsets[k];
            _windows.Add((onset + LevelFromSamples, onset + LevelToSamples, 0));
        }

        var end = _sample + frames;
        for (var w = 0; w < _windows.Count; w++)
        {
            var (from, to, sum) = _windows[w];
            var a = Math.Max(from, _sample);
            var b = Math.Min(to, end);
            for (var i = a; i < b; i++)
            {
                double x = mono[(int)(i - _sample)];
                sum += x * x;
            }

            _windows[w] = (from, to, sum);
        }

        for (var w = _windows.Count - 1; w >= 0; w--)
        {
            var (from, to, sum) = _windows[w];
            if (to <= end)
            {
                _levels.Add(Dbfs(Math.Sqrt(sum / (to - from)) * Math.Sqrt(2)));
                _windows.RemoveAt(w);
            }
        }

        _sample = end;
    }

    /// <summary>The onsets as file times from <paramref name="startSeconds"/>; bursts cut off by the end have no level.</summary>
    public TrackSignal Result(double startSeconds)
    {
        _levels.Sort();
        return new TrackSignal(
            [.. _detector.Onsets.Select(n => startSeconds + ((double)n / OnsetDetector.SampleRate))],
            [.. _levels],
            _peak);
    }

    internal static double Dbfs(double amplitude) => amplitude > 0 ? Math.Max(-200, 20 * Math.Log10(amplitude)) : -200;
}
