using Encore.Core.Replay;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace Encore.Platform.Windows;

/// <summary>Windows' physical memory, by <c>GlobalMemoryStatusEx</c>, for Rewind's RAM margin.</summary>
public sealed unsafe class SystemMemoryStatus : IMemoryStatus
{
    /// <summary>The available and installed physical memory; on failure, unlimited available, so never at the margin.</summary>
    public MemoryStatus Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        return PInvoke.GlobalMemoryStatusEx(&status)
            ? new MemoryStatus((long)status.ullAvailPhys, (long)status.ullTotalPhys)
            : new MemoryStatus(long.MaxValue, 0);
    }
}
