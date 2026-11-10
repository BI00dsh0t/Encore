using FFmpeg.AutoGen.Abstractions;
using Encore.Core.Replay;

namespace Encore.Platform.FFmpeg;

/// <summary>What a Clip is written from: a pinned cut and its streams' parameters. Disposing it unpins and frees them.</summary>
public sealed unsafe class ClipSource : IDisposable
{
    private int _disposed;

    internal ClipSource(RingCut cut, IReadOnlyList<ClipStream> streams)
    {
        Cut = cut;
        Streams = streams;
    }

    public RingCut Cut { get; }

    internal IReadOnlyList<ClipStream> Streams { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var stream in Streams)
        {
            var parameters = stream.Parameters;
            if (parameters is not null)
            {
                ffmpeg.avcodec_parameters_free(&parameters);
            }
        }

        Cut.Dispose();
    }
}
