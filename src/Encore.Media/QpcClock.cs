namespace Encore.Media;

/// <summary>Conversions into QPC ticks, the engine's only timebase.</summary>
public static class QpcClock
{
    private const long HundredNanosecondsPerSecond = 10_000_000;

    /// <summary>Converts a time in 100 ns units, as WGC and WASAPI report it, back into QPC ticks.</summary>
    public static long From100ns(long value, long qpcFrequency) =>
        (long)((Int128)value * qpcFrequency / HundredNanosecondsPerSecond);

    /// <summary>Converts a QPC interval into a count of <paramref name="units"/> per second, rounded to nearest.</summary>
    public static long ToUnits(long qpcTicks, long qpcFrequency, long units)
    {
        var scaled = (Int128)qpcTicks * units;
        var half = (Int128)qpcFrequency / 2;
        return (long)(scaled >= 0 ? (scaled + half) / qpcFrequency : (scaled - half) / qpcFrequency);
    }
}
