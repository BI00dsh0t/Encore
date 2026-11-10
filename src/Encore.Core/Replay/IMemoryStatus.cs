namespace Encore.Core.Replay;

/// <summary>Reads the machine's physical memory status.</summary>
public interface IMemoryStatus
{
    MemoryStatus Read();
}
