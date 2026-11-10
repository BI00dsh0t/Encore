namespace Encore.Core.Library;

/// <summary>The patch's two writes, made in order and each flushed; the file is a valid box tree between them.</summary>
public sealed record FragmentDurationPatch(
    long MehdOffset,
    byte[] MehdAndFree,
    long MvexOffset,
    byte[] MvexSize,
    ulong Duration,
    uint Timescale);
