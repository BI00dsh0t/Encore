using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Media;

namespace Encore.Engine;

/// <summary>Process-wide CPU, allocation and GC counters at one instant.</summary>
public readonly record struct ResourceSnapshot(long Qpc, TimeSpan Cpu, long AllocatedBytes, int Gen0, int Gen1, int Gen2)
{
    public static ResourceSnapshot Take(Process self) => new(
        Stopwatch.GetTimestamp(),
        self.TotalProcessorTime,
        GC.GetTotalAllocatedBytes(precise: true),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2));
}

/// <summary>The recorders of one <c>debug-capture --stats</c> run; each has one writer thread and is read after it stops.</summary>
public sealed class CaptureStatsCollector
{
    /// <summary>The first 10 s after the anchor are left out of the CPU, GC and allocation rates.</summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(10);

    private readonly Process _self = Process.GetCurrentProcess();

    public CaptureStatsCollector(long qpcFrequency)
    {
        QpcFrequency = qpcFrequency;
        Cadence = new FrameCadence(qpcFrequency);
        Latency = new LatencyHistogram(qpcFrequency);
    }

    public long QpcFrequency { get; }

    public FrameCadence Cadence { get; }

    public LatencyHistogram Latency { get; }

    public ResourceSnapshot? AtAnchor { get; private set; }

    /// <summary>Process start to the first frame (the anchor): device, window and encoder setup.</summary>
    public double? StartupToAnchorMs { get; private set; }

    public ResourceSnapshot? AtWarmUpEnd { get; private set; }

    public ResourceSnapshot? AtStop { get; private set; }

    public GpuPriorityReport? GpuPriority { get; set; }

    public void SnapshotAnchor()
    {
        AtAnchor = ResourceSnapshot.Take(_self);
        StartupToAnchorMs = (DateTime.Now - _self.StartTime).TotalMilliseconds;
    }

    public void SnapshotWarmUpEnd() => AtWarmUpEnd = ResourceSnapshot.Take(_self);

    public void SnapshotStop() => AtStop = ResourceSnapshot.Take(_self);

    /// <summary>The CPU, GC and allocation block: the steady part from warm-up end to stop, and the whole run.</summary>
    public ResourceReport Resources()
    {
        var processors = Environment.ProcessorCount;
        SteadyReport? steady = null;
        if (AtWarmUpEnd is { } warm && AtStop is { } stop && stop.Qpc > warm.Qpc)
        {
            var seconds = (double)(stop.Qpc - warm.Qpc) / QpcFrequency;
            var cpu = (stop.Cpu - warm.Cpu).TotalSeconds;
            var allocated = stop.AllocatedBytes - warm.AllocatedBytes;
            steady = new SteadyReport(
                seconds,
                cpu,
                cpu / seconds * 100,
                cpu / seconds * 100 / processors,
                allocated,
                allocated / seconds,
                new GcReport(stop.Gen0 - warm.Gen0, stop.Gen1 - warm.Gen1, stop.Gen2 - warm.Gen2));
        }

        RunReport? run = null;
        if (AtAnchor is { } anchor && AtStop is { } end)
        {
            run = new RunReport(
                (end.Cpu - anchor.Cpu).TotalSeconds,
                end.AllocatedBytes - anchor.AllocatedBytes,
                new GcReport(end.Gen0 - anchor.Gen0, end.Gen1 - anchor.Gen1, end.Gen2 - anchor.Gen2));
        }

        return new ResourceReport(processors, WarmUp.TotalSeconds, StartupToAnchorMs, steady, run);
    }

    public static LatencyReport Report(LatencyHistogram h, long? flushed = null) => new(
        h.Count, h.Percentile(50), h.Percentile(99), h.MaxMilliseconds, h.MeanMilliseconds, h.BucketMilliseconds, h.Overflow, h.Negative, flushed);

    /// <summary>One device's report: the hub's counters run from when the device opened, writtenFrames from the anchor.</summary>
    public static AudioReport Report(string role, string device, AudioStampStats stamps, DeviceStream stream, AudioTimeline timeline, long samplesEncoded) => new(
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
            Report(stamps.Jitter),
            Report(stamps.StampAge),
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

    /// <summary>Checks before a run that the path can be written; a file the check created is removed again.</summary>
    public static void EnsureWritable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
        }

        if (!existed)
        {
            File.Delete(path);
        }
    }

    /// <summary>Writes the report as JSON to a temp file, then moves it into place, so a failed write leaves no cut-off file.</summary>
    public static void Write(string path, CaptureStatsReport report)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, report, CaptureStatsJson.Default.CaptureStatsReport);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

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
public sealed record ResourceReport(int LogicalProcessors, double WarmUpSeconds, double? StartupToAnchorMs, SteadyReport? Steady, RunReport? Run);

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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(CaptureStatsReport))]
public sealed partial class CaptureStatsJson : JsonSerializerContext;
