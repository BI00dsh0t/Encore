namespace Encore.Core.Replay;

/// <summary>What a <see cref="MemoryMargin"/> raises.</summary>
public enum MemoryMarginEvent
{
    /// <summary>A new slab would have left less than the margin available; the ring stops growing.</summary>
    Entered,

    /// <summary>Inside the margin, a ring's trim left it holding under 1 s.</summary>
    ShortRing,
}
