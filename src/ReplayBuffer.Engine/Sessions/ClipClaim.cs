namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// How far a session's Clips have claimed its Replay Buffer, in µs since the anchor (R6): the
/// highest end of a write that succeeded or is still in flight. It is ring time, not a Reset (a Reset
/// moves only once the orchestrator takes a write's result), so a write that starts while an earlier
/// one is still writing cuts from that earlier write's end rather than back over it. A failed write
/// only drops its own claim, so failures in any order leave the start at the last succeeded end, or
/// at a write still in flight. Any thread may call it.
/// </summary>
internal sealed class ClipClaim
{
    private readonly Lock _gate = new();
    private readonly List<long> _inFlight = [];
    private long _succeededUs = long.MinValue;

    /// <summary>Where the next write may start at the earliest; <see cref="long.MinValue"/> before any claim.</summary>
    public long Start
    {
        get
        {
            lock (_gate)
            {
                return StartLocked();
            }
        }
    }

    /// <summary>Records an in-flight write ending at <paramref name="toUs"/> and returns where that write may start.</summary>
    public long Claim(long toUs)
    {
        lock (_gate)
        {
            var start = StartLocked();
            _inFlight.Add(toUs);
            return start;
        }
    }

    /// <summary>
    /// The write that claimed <paramref name="claimedUs"/> was written, ending at
    /// <paramref name="endUs"/> (earlier when its cut ended at a ring generation's boundary): that
    /// end stays claimed.
    /// </summary>
    public void Succeeded(long claimedUs, long endUs)
    {
        lock (_gate)
        {
            _inFlight.Remove(claimedUs);
            _succeededUs = Math.Max(_succeededUs, endUs);
        }
    }

    /// <summary>The write ending at <paramref name="toUs"/> failed: its claim is dropped.</summary>
    public void Failed(long toUs)
    {
        lock (_gate)
        {
            _inFlight.Remove(toUs);
        }
    }

    private long StartLocked()
    {
        var start = _succeededUs;
        foreach (var end in _inFlight)
        {
            start = Math.Max(start, end);
        }

        return start;
    }
}
