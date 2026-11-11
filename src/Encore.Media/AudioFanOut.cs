namespace Encore.Media;

/// <summary>Writes one stream to several sinks: a device shared by several Tracks feeds each Track's mixer.</summary>
public sealed class AudioFanOut(IReadOnlyList<IAudioSampleSink> sinks) : IAudioSampleSink
{
    private readonly IAudioSampleSink[] _sinks = [.. sinks];

    public void Write(ReadOnlySpan<float> interleaved)
    {
        foreach (var sink in _sinks)
        {
            sink.Write(interleaved);
        }
    }

    public void WriteSilence(long frames)
    {
        foreach (var sink in _sinks)
        {
            sink.WriteSilence(frames);
        }
    }
}
