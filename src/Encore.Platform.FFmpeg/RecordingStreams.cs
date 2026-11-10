using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A Recording's streams: its own copies of the tap's, in index order. Disposing it frees them.</summary>
public sealed unsafe class RecordingStreams : IDisposable
{
    private readonly List<ClipStream> _streams = [];
    private int _disposed;

    internal RecordingStreams()
    {
    }

    internal IReadOnlyList<ClipStream> Streams => _streams;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var stream in _streams)
        {
            var parameters = stream.Parameters;
            ffmpeg.avcodec_parameters_free(&parameters);
        }

        _streams.Clear();
    }

    internal void Add(ClipStream source)
    {
        var copy = ffmpeg.avcodec_parameters_alloc();
        if (copy is null)
        {
            throw new InvalidOperationException("avcodec_parameters_alloc failed.");
        }

        try
        {
            FFmpegLibrary.Check(ffmpeg.avcodec_parameters_copy(copy, source.Parameters), "avcodec_parameters_copy");
        }
        catch
        {
            ffmpeg.avcodec_parameters_free(&copy);
            throw;
        }

        _streams.Add(new ClipStream(copy, source.Title, source.IsDefault, source.TimeBase));
    }
}
