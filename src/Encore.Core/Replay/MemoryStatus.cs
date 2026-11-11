namespace Encore.Core.Replay;

/// <summary>The machine's physical memory: what is available now, and what is installed.</summary>
public readonly record struct MemoryStatus(long AvailableBytes, long TotalBytes);
