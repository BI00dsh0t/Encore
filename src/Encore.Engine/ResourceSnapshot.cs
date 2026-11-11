using System.Diagnostics;

namespace Encore.Engine;

/// <summary>Process-wide CPU, allocation and GC counters at one instant.</summary>
public readonly record struct ResourceSnapshot(long Timestamp, TimeSpan Cpu, long AllocatedBytes, int Gen0, int Gen1, int Gen2)
{
    public static ResourceSnapshot Take(Process self) => new(
        Stopwatch.GetTimestamp(),
        self.TotalProcessorTime,
        GC.GetTotalAllocatedBytes(precise: true),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2));
}
