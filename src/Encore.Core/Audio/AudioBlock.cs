namespace Encore.Core.Audio;

/// <summary>One block of a device's stream; blocks are contiguous, and a silent block carries no samples.</summary>
public readonly ref struct AudioBlock
{
    public AudioBlock(long startQpc, int frames, ReadOnlySpan<float> interleaved)
    {
        StartQpc = startQpc;
        Frames = frames;
        Interleaved = interleaved;
    }

    /// <summary>The QPC of the block's first frame.</summary>
    public long StartQpc { get; }

    public int Frames { get; }

    /// <summary><see cref="Frames"/> × 2 samples, or empty for a silent block.</summary>
    public ReadOnlySpan<float> Interleaved { get; }

    public bool IsSilent => Interleaved.IsEmpty;
}
