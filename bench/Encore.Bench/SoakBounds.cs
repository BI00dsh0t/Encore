using System.Globalization;

namespace Encore.Bench;

/// <summary>One bound of one process's counter: the figure, its limit, and whether it is met; an unjudged figure passes.</summary>
internal sealed record SoakBound(string Process, string Counter, double Value, double Limit, bool Pass, string Detail, bool Judged = true);

/// <summary>The resource bounds per process: memory growth and slope, and handle, GDI and USER growth. docs/code-notes.md.</summary>
internal static class SoakBounds
{
    public const double MaxSlopeMbPerHour = 2;
    public const double MinPrivateGrowthMb = 20;
    public const double PrivateGrowthFraction = 0.05;
    public const int MaxObjectGrowth = 50;
    public static readonly TimeSpan MinSlopeSpan = TimeSpan.FromHours(1);
    private const double Mb = 1024 * 1024;

    public static List<SoakBound> Evaluate(IReadOnlyDictionary<string, IReadOnlyList<ProcessCounters>> samples, TimeSpan warmup)
    {
        var bounds = new List<SoakBound>();
        foreach (var (name, series) in samples.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var ordered = series.OrderBy(s => s.Time).ToList();
            if (ordered.Count == 0)
            {
                bounds.Add(new SoakBound(name, "samples", 0, 1, false, "no samples"));
                continue;
            }

            var after = ordered.Where(s => s.Time - ordered[0].Time >= warmup).ToList();
            if (after.Count < 2)
            {
                bounds.Add(new SoakBound(name, "samples", after.Count, 2, false, $"{after.Count} sample(s) after the {Minutes(warmup)}-minute warm-up; the run is too short"));
                continue;
            }

            var start = after[0];
            var end = after[^1];

            var startMb = start.PrivateBytes / Mb;
            var growthMb = (end.PrivateBytes - start.PrivateBytes) / Mb;
            var limitMb = Math.Max(PrivateGrowthFraction * startMb, MinPrivateGrowthMb);
            var growthPass = growthMb <= limitMb;
            bounds.Add(new SoakBound(name, "privateBytes", Round(growthMb), Round(limitMb), growthPass,
                Text($"private bytes {Signed(growthMb)} MB {(growthPass ? "≤" : ">")} {limitMb:0.##} MB since warm-up ({startMb:0.#} MB)")));

            var slope = Slope(after.Select(s => ((s.Time - start.Time).TotalHours, s.PrivateBytes / Mb)).ToList());
            if (end.Time - start.Time >= MinSlopeSpan)
            {
                var slopePass = slope < MaxSlopeMbPerHour;
                bounds.Add(new SoakBound(name, "privateBytesSlope", Round(slope), MaxSlopeMbPerHour, slopePass,
                    Text($"slope {slope:0.##} MB/h {(slopePass ? "<" : "≥")} {MaxSlopeMbPerHour:0.##} MB/h")));
            }
            else
            {
                bounds.Add(new SoakBound(name, "privateBytesSlope", Round(slope), MaxSlopeMbPerHour, true,
                    Text($"slope {slope:0.##} MB/h, information only: judged over {MinSlopeSpan.TotalHours:0.#} h or more after warm-up"), Judged: false));
            }

            foreach (var (counter, read) in new (string, Func<ProcessCounters, int>)[] { ("handles", s => s.Handles), ("gdi", s => s.Gdi), ("user", s => s.User) })
            {
                var growth = read(end) - read(start);
                var pass = growth <= MaxObjectGrowth;
                bounds.Add(new SoakBound(name, counter, growth, MaxObjectGrowth, pass,
                    Text($"{counter} {Signed(growth)} {(pass ? "≤" : ">")} {MaxObjectGrowth}")));
            }
        }

        return bounds;
    }

    /// <summary>The least-squares slope of y on x; 0 when x doesn't vary.</summary>
    internal static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var sxx = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return sxx == 0 ? 0 : points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / sxx;
    }

    private static string Signed(double value) => (value >= 0 ? "+" : "") + value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Minutes(TimeSpan span) => span.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture);

    private static double Round(double value) => Math.Round(value, 3);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
