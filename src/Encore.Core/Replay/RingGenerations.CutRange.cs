namespace Encore.Core.Replay;

public sealed partial class RingGenerations
{
    // The generation a cut reads and the time range it cuts there; Fallback means it is the one before the newest.
    private readonly record struct CutRange(Generation Generation, long FromUs, long ToUs, bool Fallback);
}
