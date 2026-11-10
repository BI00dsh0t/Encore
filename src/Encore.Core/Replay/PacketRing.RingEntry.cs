namespace Encore.Core.Replay;

public sealed partial class PacketRing
{
    // One packet in the ring: where its bytes are, and its times as the encoder stamped them.
    private readonly record struct RingEntry(
        Slab Slab,
        int Offset,
        int Length,
        int Stream,
        long Pts,
        long Dts,
        long Duration,
        bool IsKey,
        long TimeUs);
}
