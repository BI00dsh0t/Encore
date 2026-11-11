using System.Diagnostics;
using Encore.Media;

namespace Encore.Engine;

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
        if (AtWarmUpEnd is { } warm && AtStop is { } stop && stop.Timestamp > warm.Timestamp)
        {
            var seconds = (double)(stop.Timestamp - warm.Timestamp) / QpcFrequency;
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
}
