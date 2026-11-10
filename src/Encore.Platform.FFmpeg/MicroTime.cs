using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>Converts between a stream's time base and microseconds, exactly, through 128-bit products.</summary>
internal static class MicroTime
{
    public static long ToMicros(long ticks, AVRational timeBase) =>
        (long)((Int128)ticks * 1_000_000 * timeBase.num / timeBase.den);

    public static long FromMicros(long us, AVRational timeBase) =>
        (long)((Int128)us * timeBase.den / ((Int128)1_000_000 * timeBase.num));
}
