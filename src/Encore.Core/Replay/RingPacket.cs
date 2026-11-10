namespace Encore.Core.Replay;

/// <summary>One packet of a <see cref="RingCut"/>; its bytes are valid until the cut is disposed.</summary>
public readonly struct RingPacket
{
    private readonly PacketRing.Slab _slab;
    private readonly int _offset;

    internal RingPacket(PacketRing.Slab slab, int offset, int length, int stream, long pts, long dts, long duration, bool isKey, long timeUs)
    {
        _slab = slab;
        _offset = offset;
        Length = length;
        Stream = stream;
        Pts = pts;
        Dts = dts;
        Duration = duration;
        IsKey = isKey;
        TimeUs = timeUs;
    }

    public int Stream { get; }

    /// <summary>In the stream's time base, as the encoder stamped it.</summary>
    public long Pts { get; }

    public long Dts { get; }

    public long Duration { get; }

    public bool IsKey { get; }

    /// <summary>Its start, in µs since the anchor.</summary>
    public long TimeUs { get; }

    public int Length { get; }

    /// <summary>Copies its bytes into the start of <paramref name="destination"/>, which must hold <see cref="Length"/>.</summary>
    public void CopyTo(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Length, nameof(destination));
        _slab.Read(_offset, destination[..Length]);
    }

    /// <summary>Its bytes, as a new array (for tests).</summary>
    internal byte[] ToArray()
    {
        var bytes = new byte[Length];
        CopyTo(bytes);
        return bytes;
    }
}
