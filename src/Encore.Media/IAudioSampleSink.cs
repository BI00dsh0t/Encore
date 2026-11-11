namespace Encore.Media;

/// <summary>Receives the contiguous output of an <see cref="AudioTimeline"/>.</summary>
public interface IAudioSampleSink
{
    /// <summary>Appends interleaved 48 kHz stereo float frames.</summary>
    void Write(ReadOnlySpan<float> interleaved);

    /// <summary>Appends <paramref name="frames"/> frames of silence.</summary>
    void WriteSilence(long frames);
}
