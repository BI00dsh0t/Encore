namespace Encore.Core.Replay;

public sealed partial class RingGenerations
{
    private sealed class Generation(int number, PacketRing ring, long startUs)
    {
        public int Number { get; } = number;

        public PacketRing Ring { get; } = ring;

        /// <summary>Its boundary in µs since the anchor: MinValue for generation 1, else its first keyframe (MaxValue before).</summary>
        public long StartUs { get; set; } = startUs;

        /// <summary>Cuts reading its ring outside the lock. The lock guards it.</summary>
        public int Users { get; set; }

        /// <summary>The lock guards it.</summary>
        public bool Retired { get; set; }
    }
}
