namespace Encore.Core.Replay;

/// <summary>The machine's physical memory: what is available now, and what is installed.</summary>
public readonly record struct MemoryStatus(long AvailableBytes, long TotalBytes);

/// <summary>Reads the machine's physical memory status.</summary>
public interface IMemoryStatus
{
    MemoryStatus Read();
}

/// <summary>What a <see cref="MemoryMargin"/> raises.</summary>
public enum MemoryMarginEvent
{
    /// <summary>A new slab would have left less than the margin available; the ring stops growing.</summary>
    Entered,

    /// <summary>Inside the margin, a ring's trim left it holding under 1 s.</summary>
    ShortRing,
}

/// <summary>The RAM margin: a ring asks before each new native-memory slab and is refused when too little would be left.</summary>
public sealed class MemoryMargin(IMemoryStatus status)
{
    /// <summary>The least the margin is, whatever is installed.</summary>
    public const long FloorBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The share of installed memory the margin is, when that is above <see cref="FloorBytes"/>.</summary>
    public const double InstalledShare = 0.15;

    /// <summary>The hysteresis: how far past the margin a slab must leave available memory before the margin is left.</summary>
    public const long LeaveSlackBytes = 256L * 1024 * 1024;

    private readonly IMemoryStatus _status = status ?? throw new ArgumentNullException(nameof(status));
    private readonly Lock _lock = new();
    private bool _inside;
    private bool _shortReported;

    /// <summary>Raised outside the margin's lock, on the thread of the ring that caused it.</summary>
    public event Action<MemoryMarginEvent>? Raised;

    /// <summary>Whether the last <see cref="Allows"/> call was refused.</summary>
    public bool Inside
    {
        get
        {
            lock (_lock)
            {
                return _inside;
            }
        }
    }

    /// <summary>The margin for <paramref name="totalBytes"/> installed: <c>max(4 GiB, 15% of installed)</c>.</summary>
    public static long MarginBytes(long totalBytes) => Math.Max(FloorBytes, (long)(totalBytes * InstalledShare));

    /// <summary>Whether a new slab leaves the margin available; a refusal enters the margin and an allowance leaves it.</summary>
    public bool Allows(long bytes)
    {
        var current = _status.Read();
        bool allowed;
        var entered = false;
        lock (_lock)
        {
            var needed = MarginBytes(current.TotalBytes) + (_inside ? LeaveSlackBytes : 0);
            allowed = current.AvailableBytes - bytes >= needed;
            if (!allowed && !_inside)
            {
                _inside = true;
                _shortReported = false;
                entered = true;
            }
            else if (allowed)
            {
                _inside = false;
            }
        }

        if (entered)
        {
            Raised?.Invoke(MemoryMarginEvent.Entered);
        }

        return allowed;
    }

    /// <summary>A ring's margin trim left it under 1 s: raises the short-ring event once per entry, and only while inside.</summary>
    public void ReportShortRing()
    {
        lock (_lock)
        {
            if (!_inside || _shortReported)
            {
                return;
            }

            _shortReported = true;
        }

        Raised?.Invoke(MemoryMarginEvent.ShortRing);
    }
}
