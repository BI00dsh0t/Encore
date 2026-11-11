namespace Encore.Engine.Sessions;

/// <summary>How far a session's Clips have claimed its ring, in µs since the anchor. Any thread; see docs/code-notes.md.</summary>
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

    /// <summary>The write that claimed this span succeeded and ended at endUs, which stays claimed.</summary>
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
