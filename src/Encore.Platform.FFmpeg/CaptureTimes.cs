namespace Encore.Platform.FFmpeg;

/// <summary>The capture QPC of the last few frames, by PTS, so a packet's latency can be read when it comes out.</summary>
internal sealed class CaptureTimes(int capacity)
{
    private readonly long[] _pts = new long[capacity];
    private readonly long[] _captureQpc = new long[capacity];
    private int _next;

    /// <summary>Remembers a frame's capture time; a repeat's is 0, and records no latency.</summary>
    public void Remember(long pts, long captureQpc)
    {
        _pts[_next] = pts;
        _captureQpc[_next] = captureQpc;
        _next = (_next + 1) % _pts.Length;
    }

    /// <summary>The capture QPC remembered for a PTS; 0 for a repeat, or a PTS no longer remembered.</summary>
    public long Find(long pts)
    {
        for (var i = 0; i < _pts.Length; i++)
        {
            if (_pts[i] == pts && _captureQpc[i] != 0)
            {
                return _captureQpc[i];
            }
        }

        return 0;
    }
}
