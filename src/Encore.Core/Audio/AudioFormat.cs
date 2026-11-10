namespace Encore.Core.Audio;

/// <summary>The engine's one internal audio format: 48 kHz, stereo, 32-bit float, interleaved.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
}
