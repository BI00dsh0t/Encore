using System.Text.Json.Serialization;

namespace Encore.Engine;

/// <summary>The stats file of one capture run.</summary>
public sealed record CaptureStatsReport(
    int Schema,
    DateTimeOffset Created,
    string Output,
    string Window,
    string? Process,
    string Adapter,
    int RequestedSeconds,
    string StopReason,
    double DurationSeconds,
    GpuPriorityReport? GpuPriority,
    EncoderReport Encoder,
    VideoReport Video,
    LatencyReport Latency,
    ResourceReport Resources,
    IReadOnlyList<AudioReport> Audio)
{
    public const int CurrentSchema = 1;
}

/// <summary>Hresult is <c>SetGPUThreadPriority</c>'s result as 0xXXXXXXXX; ReadBack is what the device reports afterwards.</summary>
public sealed record GpuPriorityReport(int Requested, string Hresult, bool Succeeded, int? ReadBack);

/// <summary>Stages is full, copy or wgc; Color is x1 (BGRA into NVENC) or x2 (VideoProcessor NV12).</summary>
public sealed record EncoderReport(string Stages, string Preset, string? Tune, string Color, bool EncodeThread);

/// <summary>One run's video counts; each field is explained in docs/code-notes.md.</summary>
public sealed record VideoReport(
    int Width,
    int Height,
    long FramesArrived,
    long FramesEncoded,
    long Duplicates,
    long QueueDrops,
    long NonIncreasingStamps,
    long MissedSlots,
    long Gaps,
    double MaxStepMs,
    double CaptureFps,
    double DuplicatePercent,
    double MissedSlotPercent,
    long FreshSlots = 0,
    long SkippedSlots = 0,
    long HeldReplaced = 0);

/// <param name="Flushed">Capture→encoded only: packets drained at flush, left out of the latency figures.</param>
public sealed record LatencyReport(
    long Count,
    double P50Ms,
    double P99Ms,
    double MaxMs,
    double MeanMs,
    double BucketMs,
    long Overflow,
    long Negative,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Flushed = null);

/// <summary>Steady runs from warm-up end to stop, and is null when the run ended inside the warm-up; Run is from the anchor.</summary>
public sealed record ResourceReport(
    int LogicalProcessors,
    double WarmUpSeconds,
    double? StartupToAnchorMs,
    SteadyReport? Steady,
    RunReport? Run);

public sealed record SteadyReport(
    double Seconds,
    double CpuSeconds,
    double CpuPercentOfOneCore,
    double CpuPercentOfMachine,
    long AllocatedBytes,
    double AllocatedBytesPerSecond,
    GcReport Gc);

public sealed record RunReport(double CpuSeconds, long AllocatedBytes, GcReport Gc);

public sealed record GcReport(int Gen0, int Gen1, int Gen2);

/// <summary>Role is "output" (loopback) or "input"; SamplesEncoded counts the frames the AAC encoder got, fill included.</summary>
public sealed record AudioReport(
    string Role,
    string Device,
    long Packets,
    long Frames,
    long SilentPackets,
    StampReport Stamps,
    TimelineReport Timeline,
    long SamplesEncoded,
    ResamplerReport? Resampler = null);

/// <summary>How the hub steered this device to QPC; each field is explained in docs/code-notes.md.</summary>
public sealed record ResamplerReport(
    double RatioPpm,
    double MeanRatioPpm,
    long StepCorrections,
    long SettledPackets,
    double ErrorP99Samples,
    double ErrorMaxSamples,
    long OutputFrames);

/// <summary>Jitter is each stamp against the previous stamp plus its frames; Age is the read time minus the stamp.</summary>
public sealed record StampReport(
    long TimestampErrors,
    long Zero,
    long Backwards,
    long Discontinuities,
    LatencyReport Jitter,
    LatencyReport Age,
    DriftReport Drift);

/// <param name="Ppm">Stamp time against frame time over the longest unbroken segment.</param>
public sealed record DriftReport(double Ppm, double SegmentSeconds, long Segments);

/// <param name="WrittenFrames">The session timeline's frames from the anchor; the other counts are the hub stream's.</param>
public sealed record TimelineReport(
    long WrittenFrames,
    long GapFills,
    long GapFillFrames,
    long Trims,
    long TrimmedFrames,
    long IdleFillFrames,
    long ResolvedStamps);
