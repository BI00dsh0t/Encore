using FFmpeg.AutoGen.Abstractions;
using ReplayBuffer.Core.Replay;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// The Replay Buffer's <see cref="PacketSink"/> (AD-8): each packet's bytes are copied into a
/// <see cref="PacketRing"/>, and each stream's codec parameters, title, default flag and time base
/// are kept for the Clips cut from it. It owns the ring. Its count is the session's one reference
/// plus one per <see cref="Lease"/>: a save leases it until it has cut, so ending the session never
/// frees the ring under a waiting writer.
/// </summary>
public sealed unsafe class ReplayRingSink : PacketSink, IDisposable
{
    private readonly PacketRing _ring;
    private readonly Lock _lock = new();
    private readonly List<ClipStream> _streams = [];
    private int _references = 1;
    private int _disposed;
    private volatile bool _closed;

    public ReplayRingSink(PacketRing ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        _ring = ring;
    }

    /// <summary>Clips carry their codec headers in the sample entry.</summary>
    internal override bool NeedsGlobalHeader => true;

    /// <summary>The streams added, in index order.</summary>
    public int StreamCount
    {
        get
        {
            lock (_lock)
            {
                return _streams.Count;
            }
        }
    }

    internal override int AddStream(AVCodecContext* encoder, string? title, bool isDefault)
    {
        var parameters = ffmpeg.avcodec_parameters_alloc();
        if (parameters is null)
        {
            throw new InvalidOperationException("avcodec_parameters_alloc failed.");
        }

        try
        {
            FFmpegLibrary.Check(ffmpeg.avcodec_parameters_from_context(parameters, encoder), "avcodec_parameters_from_context");
        }
        catch
        {
            ffmpeg.avcodec_parameters_free(&parameters);
            throw;
        }

        lock (_lock)
        {
            var timeBase = encoder->time_base;
            var index = _ring.AddStream(timeBase.num, timeBase.den, encoder->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO);
            _streams.Add(new ClipStream(parameters, title, isDefault, timeBase));
            return index;
        }
    }

    internal override void Write(AVPacket* packet, int streamIndex, AVRational timeBase)
    {
        try
        {
            if (_closed || BeforeStart(packet))
            {
                return;
            }

            _ring.Append(
                streamIndex,
                new ReadOnlySpan<byte>(packet->data, packet->size),
                packet->pts,
                packet->dts,
                packet->duration,
                (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0);
        }
        finally
        {
            ffmpeg.av_packet_unref(packet);
        }
    }

    /// <summary>Whether every stream's newest packet ends at or after <paramref name="us"/> (µs since the anchor).</summary>
    public bool AllStreamsPast(long us) => _ring.AllStreamsPast(us);

    /// <summary>
    /// The video stream's <paramref name="pts"/> as µs since the anchor, exactly as the ring times
    /// that packet, so a cut can end at it.
    /// </summary>
    public long VideoMicros(long pts) => _ring.ToMicros(_ring.VideoStream, pts);

    /// <summary>
    /// Pins <c>[fromUs, toUs)</c> (µs since the anchor) from the first video keyframe at or after
    /// <paramref name="fromUs"/>, as <see cref="PacketRing.Cut"/> splits it, with its own copies of
    /// the streams' parameters, so it outlives the sink; null with no keyframe before <paramref name="toUs"/>.
    /// </summary>
    public ClipSource? Cut(long fromUs, long toUs)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _references) == 0, this);
            var cut = _ring.Cut(fromUs, toUs);
            if (cut is null)
            {
                return null;
            }

            var streams = new List<ClipStream>(_streams.Count);
            try
            {
                foreach (var stream in _streams)
                {
                    var copy = ffmpeg.avcodec_parameters_alloc();
                    if (copy is null)
                    {
                        throw new InvalidOperationException("avcodec_parameters_alloc failed.");
                    }

                    streams.Add(new ClipStream(copy, stream.Title, stream.IsDefault, stream.TimeBase));
                    FFmpegLibrary.Check(ffmpeg.avcodec_parameters_copy(copy, stream.Parameters), "avcodec_parameters_copy");
                }
            }
            catch
            {
                new ClipSource(cut, streams).Dispose();
                throw;
            }

            return new ClipSource(cut, streams);
        }
    }

    /// <summary>Takes a reference, so the ring outlives <see cref="Dispose"/> until <see cref="Release"/>; false once freed.</summary>
    public bool Lease()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Drops a reference from <see cref="Lease"/>; the last one frees the ring.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0)
        {
            return;
        }

        lock (_lock)
        {
            _ring.Dispose();
            foreach (var stream in _streams)
            {
                var parameters = stream.Parameters;
                ffmpeg.avcodec_parameters_free(&parameters);
            }

            _streams.Clear();
        }
    }

    /// <summary>Takes no more packets and drops the session's reference.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closed = true;
        Release();
    }
}

/// <summary>One stream of a Clip: its codec parameters (owned by whoever holds them), title, default flag and time base.</summary>
internal sealed unsafe class ClipStream(AVCodecParameters* parameters, string? title, bool isDefault, AVRational timeBase)
{
    public AVCodecParameters* Parameters { get; } = parameters;

    public string? Title { get; } = title;

    public bool IsDefault { get; } = isDefault;

    public AVRational TimeBase { get; } = timeBase;
}

/// <summary>
/// What a Clip is written from: a pinned <see cref="RingCut"/> and its streams' own parameters.
/// Disposing it unpins the slabs and frees the parameters.
/// </summary>
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
