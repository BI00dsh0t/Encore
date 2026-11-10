namespace Encore.Core.Replay;

/// <summary>A pinned slice of a ring for one Clip; its slabs stay allocated until it is disposed.</summary>
public sealed class RingCut : IDisposable
{
    private readonly RingPacket[] _packets;
    private readonly List<PacketRing.Slab> _slabs;
    private readonly long[] _origins;
    private int _disposed;

    internal RingCut(RingPacket[] packets, List<PacketRing.Slab> slabs, long[] origins, long startUs, long endUs)
    {
        _packets = packets;
        _slabs = slabs;
        _origins = origins;
        StartUs = startUs;
        EndUs = endUs;
    }

    /// <summary>The packets, in time order.</summary>
    public IReadOnlyList<RingPacket> Packets => _packets;

    /// <summary>The keyframe the Clip starts at, in µs since the anchor.</summary>
    public long StartUs { get; }

    /// <summary>Where it was cut, in µs since the anchor: the first instant it does not hold.</summary>
    public long EndUs { get; }

    /// <summary>Stream <paramref name="stream"/>'s time 0 in the Clip, in its own time base.</summary>
    public long Origin(int stream) => _origins[stream];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var slab in _slabs)
        {
            slab.Release();
        }
    }
}
