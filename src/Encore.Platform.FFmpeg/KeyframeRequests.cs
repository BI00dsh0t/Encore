namespace Encore.Platform.FFmpeg;

/// <summary>An encoder's keyframe requests and the frame that served them; its lock is never held across an encode.</summary>
internal sealed class KeyframeRequests
{
    private readonly Lock _lock = new();
    private long _requests;
    private long _afterQpc = long.MinValue;
    private long _servedTicket;
    private long _servedPts;

    /// <summary>Asks for a keyframe on the first frame captured after a QPC time; returns the request's ticket.</summary>
    public long Request(long afterQpc)
    {
        lock (_lock)
        {
            _afterQpc = Math.Max(_afterQpc, afterQpc);
            return ++_requests;
        }
    }

    /// <summary>The PTS of the keyframe that served a ticket; false until one has.</summary>
    public bool TryGetServed(long ticket, out long pts)
    {
        lock (_lock)
        {
            pts = _servedPts;
            return _servedTicket >= ticket;
        }
    }

    /// <summary>Whether a frame captured at this time serves the outstanding requests; requested is the newest ticket.</summary>
    public bool IsDue(long frameQpc, out long requested)
    {
        lock (_lock)
        {
            requested = _requests;
            return requested > _servedTicket && frameQpc > _afterQpc;
        }
    }

    /// <summary>Records that the keyframe at this PTS served every request up to the ticket.</summary>
    public void MarkServed(long pts, long requested)
    {
        lock (_lock)
        {
            _servedPts = pts;
            _servedTicket = requested;
        }
    }
}
