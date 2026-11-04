using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReplayBuffer.Bench;

/// <summary>One sample of a process's resource counters: what <c>bench soak</c> holds to its bounds.</summary>
/// <param name="PrivateBytes">The commit charge, <c>PROCESS_MEMORY_COUNTERS_EX.PrivateUsage</c>.</param>
/// <param name="Gdi">GDI objects (<c>GR_GDIOBJECTS</c>).</param>
/// <param name="User">USER objects (<c>GR_USEROBJECTS</c>).</param>
internal sealed record ProcessCounters(
    DateTimeOffset Time,
    int Pid,
    string Name,
    long PrivateBytes,
    long WorkingSet,
    int Handles,
    int Gdi,
    int User)
{
    public const string CsvHeader = "time,elapsedSeconds,process,pid,privateBytes,workingSet,handles,gdi,user";

    public string CsvRow(DateTimeOffset start) => string.Join(
        ',',
        Time.ToString("O", CultureInfo.InvariantCulture),
        (Time - start).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        Name,
        Pid.ToString(CultureInfo.InvariantCulture),
        PrivateBytes.ToString(CultureInfo.InvariantCulture),
        WorkingSet.ToString(CultureInfo.InvariantCulture),
        Handles.ToString(CultureInfo.InvariantCulture),
        Gdi.ToString(CultureInfo.InvariantCulture),
        User.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Reads a process's private bytes, working set, handles and GDI and USER objects.</summary>
internal static class SoakSampler
{
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    /// <param name="name">The name the sample carries; the process's own name when null.</param>
    /// <exception cref="Win32Exception">The counters couldn't be read (the process has exited).</exception>
    public static ProcessCounters Sample(Process process, string? name = null, TimeProvider? time = null)
    {
        var handle = process.SafeHandle;
        var memory = new ProcessMemoryCountersEx { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        if (!GetProcessMemoryInfo(handle, ref memory, memory.Cb))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetProcessMemoryInfo failed for process {process.Id}.");
        }

        if (!GetProcessHandleCount(handle, out var handles))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetProcessHandleCount failed for process {process.Id}.");
        }

        return new ProcessCounters(
            (time ?? TimeProvider.System).GetUtcNow(),
            process.Id,
            name ?? process.ProcessName,
            (long)memory.PrivateUsage,
            (long)memory.WorkingSetSize,
            (int)handles,
            (int)GetGuiResources(handle, GrGdiObjects),
            (int)GetGuiResources(handle, GrUserObjects));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCountersEx counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(SafeProcessHandle process, out uint count);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(SafeProcessHandle process, uint flags);
}
