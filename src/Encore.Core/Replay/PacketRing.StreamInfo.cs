namespace Encore.Core.Replay;

public sealed partial class PacketRing
{
    // One stream of the ring: its time base, and where its newest packet ends.
    private sealed class StreamInfo(int num, int den, bool isVideo)
    {
        public int Num { get; } = num;

        public int Den { get; } = den;

        public bool IsVideo { get; } = isVideo;

        public long NewestEndUs { get; set; } = long.MinValue;
    }
}
