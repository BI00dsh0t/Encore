using Encore.Media;

namespace Encore.Engine;

/// <summary>Builds the parts of a stats report that come straight from one recorder: a latency histogram, an audio device.</summary>
internal static class CaptureStatsReports
{
    /// <summary>A latency histogram's report; flushed is the packets drained at the flush, which it leaves out.</summary>
    public static LatencyReport Latency(LatencyHistogram histogram, long? flushed = null) => new(
        histogram.Count,
        histogram.Percentile(50),
        histogram.Percentile(99),
        histogram.MaxMilliseconds,
        histogram.MeanMilliseconds,
        histogram.BucketMilliseconds,
        histogram.Overflow,
        histogram.Negative,
        flushed);

    /// <summary>One device's report: the hub's counters run from when the device opened, writtenFrames from the anchor.</summary>
    public static AudioReport Audio(
        string role,
        string device,
        AudioStampStats stamps,
        DeviceStream stream,
        AudioTimeline timeline,
        long samplesEncoded) => new(
        role,
        device,
        stamps.Packets,
        stamps.Frames,
        stamps.SilentPackets,
        new StampReport(
            stamps.TimestampErrors,
            stamps.ZeroStamps,
            stamps.BackwardStamps,
            stamps.Discontinuities,
            Latency(stamps.Jitter),
            Latency(stamps.StampAge),
            new DriftReport(stamps.DriftPpm, stamps.DriftSegmentSeconds, stamps.Segments)),
        new TimelineReport(
            timeline.WrittenFrames,
            stream.GapFills,
            stream.GapFillFrames,
            stream.Trims,
            stream.TrimmedFrames,
            stream.IdleFillFrames,
            stream.ResolvedStamps),
        samplesEncoded,
        new ResamplerReport(
            stream.RatioPpm,
            stream.MeanRatioPpm,
            stream.StepCorrections,
            stream.SteeringError.Count,
            stream.SteeringError.Percentile(99) * AudioTimeline.SampleRate / 1000,
            stream.MaxSettledErrorSamples,
            stream.OutputFrames));
}
