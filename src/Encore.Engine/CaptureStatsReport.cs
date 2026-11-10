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

/// <summary>
/// The recorders of one <c>debug-capture --stats</c> run (story 1.2). Each recorder has one
/// writer thread (frames, video packets), and is read once those threads have stopped. Audio
/// stamps are recorded by the hub, per device (story 1.12). Nothing here allocates per frame or per packet.
/// </summary>
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

    /// <summary>
    /// One device: its stamps and stream counters from the hub, which run from when the device opened;
    /// <c>writtenFrames</c> is the session timeline's, from the anchor.
    /// </summary>
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

    /// <summary>
    /// Checks, before a run, that <paramref name="path"/> can be written: creates its folder
    /// and opens the file for writing. A file the check created is removed again.
    /// </summary>
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

    /// <summary>
    /// Writes <paramref name="report"/> as indented JSON (source-generated, AOT-safe) to
    /// <c>&lt;path&gt;.tmp</c>, then moves it over <paramref name="path"/>, so a failed write
    /// never leaves a truncated file.
    /// </summary>
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

/// <param name="Hresult">The HRESULT of <c>SetGPUThreadPriority</c>, as 0xXXXXXXXX.</param>
/// <param name="ReadBack">What <c>GetGPUThreadPriority</c> reports afterwards.</param>
public sealed record GpuPriorityReport(int Requested, string Hresult, bool Succeeded, int? ReadBack);

/// <param name="Stages">full, copy or wgc.</param>
/// <param name="Preset">The h264_nvenc preset.</param>
/// <param name="Tune">The h264_nvenc tune, or null for FFmpeg's default.</param>
/// <param name="Color">x1 (BGRA into NVENC) or x2 (VideoProcessor NV12).</param>
/// <param name="EncodeThread">Submit and drain on the encoder thread: always, since story 1.11, when anything is encoded.</param>
public sealed record EncoderReport(string Stages, string Preset, string? Tune, string Color, bool EncodeThread);

/// <param name="FramesArrived">Frames WGC delivered after the anchor, warm-up included.</param>
/// <param name="FramesEncoded">Video packets written to the file: one per slot, new or repeated.</param>
/// <param name="Duplicates">Slots the pacer filled by repeating the last frame (story 1.11).</param>
/// <param name="QueueDrops">Frames not drawn because the encoder thread's hand-off, or the frame pool, was full.</param>
/// <param name="NonIncreasingStamps">Arrived frames whose capture stamp was not after the previous one.</param>
/// <param name="MissedSlots">WGC's cadence: sum of <c>round(Δ × 60) − 1</c> over steps longer than 1.5/60 s.</param>
/// <param name="DuplicatePercent">Repeated slots as a share of all slots encoded.</param>
/// <param name="FreshSlots">Slots encoded with a new frame.</param>
/// <param name="SkippedSlots">Slots the pacer jumped over because the encoder fell more than 4 slots behind.</param>
/// <param name="HeldReplaced">Frames held for the next slot that a newer frame replaced before it.</param>
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

/// <param name="Steady">From warm-up end to stop; null when the run ended inside the warm-up.</param>
/// <param name="Run">From the anchor to stop.</param>
/// <param name="StartupToAnchorMs">Process start to the first captured frame.</param>
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

/// <param name="Role">"output" (loopback) or "input".</param>
/// <param name="SamplesEncoded">Frames the AAC encoder received, silence fill included.</param>
/// <param name="Resampler">The hub's steering of this device to QPC (story 1.12).</param>
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

/// <param name="RatioPpm">The resampler's ratio at stop, (step − 1) × 10⁶; about −drift.</param>
/// <param name="MeanRatioPpm">The mean ratio after the 5 s settle.</param>
/// <param name="StepCorrections">Errors past 5 ms without a discontinuity flag, corrected by silence or a trim.</param>
/// <param name="SettledPackets">Steered packets after the settle, which the error figures cover.</param>
/// <param name="ErrorP99Samples">|content position − stamp| at the 99th percentile, in samples (bucket upper edge).</param>
/// <param name="OutputFrames">Frames the device's stream produced since it opened.</param>
public sealed record ResamplerReport(
    double RatioPpm,
    double MeanRatioPpm,
    long StepCorrections,
    long SettledPackets,
    double ErrorP99Samples,
    double ErrorMaxSamples,
    long OutputFrames);

/// <param name="Jitter">|stamp − (previous stamp + previous frames)|, consecutive good packets.</param>
/// <param name="Age">QPC at <c>GetBuffer</c> minus the stamp.</param>
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
