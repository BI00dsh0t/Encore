using FFmpeg.AutoGen.Abstractions;
using Encore.Core.Replay;

namespace Encore.Platform.FFmpeg;

/// <summary>Rewind's packet sink: copies each packet into a ring and keeps stream parameters for Clips. docs/code-notes.md.</summary>
public sealed unsafe class ReplayRingSink : PacketSink, IDisposable
{
    private readonly RingGenerations _generations;
    private readonly Lock _lock = new();

    // Each generation's streams; a resize's generation shares its predecessor's list.
    private readonly Dictionary<int, List<ClipStream>> _streams = [];
    private int _references = 1;
    private int _disposed;
    private volatile bool _closed;
    private long _firstKeyUs = long.MaxValue;

    /// <summary>The ring given here is generation 1's.</summary>
    public ReplayRingSink(PacketRing ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        _generations = new RingGenerations(ring);
        _streams[1] = [];
    }

    /// <summary>Clips carry their codec headers in the sample entry.</summary>
    internal override bool NeedsGlobalHeader => true;

    /// <summary>The current generation's streams.</summary>
    public int StreamCount
    {
        get
        {
            lock (_lock)
            {
                return _streams[_generations.Current].Count;
            }
        }
    }

    /// <summary>The current ring generation, counting from 1.</summary>
    public int Generation => _generations.Current;

    /// <summary>Whether <see cref="Dispose"/> has run: it takes no more packets and can't open a generation.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The first video keyframe written, in µs since the anchor (MaxValue until then); a mid-session chain starts there.</summary>
    public long FirstKeyUs => Volatile.Read(ref _firstKeyUs);

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
            var index = _generations.AddStream(timeBase.num, timeBase.den, encoder->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO);
            _streams[_generations.Current].Add(new ClipStream(parameters, title, isDefault, timeBase));
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

            var isKey = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            _generations.Append(
                streamIndex,
                new ReadOnlySpan<byte>(packet->data, packet->size),
                packet->pts,
                packet->dts,
                packet->duration,
                isKey);

            // Set once: the steady state costs this one field read.
            if (_firstKeyUs == long.MaxValue && isKey && streamIndex == _generations.Ring.VideoStream)
            {
                Volatile.Write(ref _firstKeyUs, _generations.ToMicros(streamIndex, packet->pts));
            }
        }
        finally
        {
            ffmpeg.av_packet_unref(packet);
        }
    }

    /// <summary>Whether every stream's newest packet ends at or after <paramref name="us"/> (µs since the anchor).</summary>
    public bool AllStreamsPast(long us) => _generations.AllStreamsPast(us);

    /// <summary>The video stream's PTS as µs since the anchor, exactly as the ring times that packet, so a cut can end at it.</summary>
    public long VideoMicros(long pts) => _generations.ToMicros(_generations.Ring.VideoStream, pts);

    /// <summary>Opens a ring generation for a restarted encoder chain; no save may be waiting. See docs/code-notes.md.</summary>
    public int Restart(PacketRing next)
    {
        lock (_lock)
        {
            var number = _generations.Restart(next);
            _streams[number] = [];
            return number;
        }
    }

    /// <summary>Moves Rewind to a ring of a new Buffer Window, seeded from the keyframe at fromUs; the old ring is retired.</summary>
    public int Resize(PacketRing next, long fromUs)
    {
        lock (_lock)
        {
            var previous = _generations.Current;
            var number = _generations.Resize(next, fromUs);
            _streams[number] = _streams[previous];
            return number;
        }
    }

    /// <summary>Pins a time range from one ring generation with its own copies of the stream parameters; null when empty.</summary>
    public ClipSource? Cut(long fromUs, long toUs)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _references) == 0, this);
        if (_generations.Cut(fromUs, toUs) is not { } picked)
        {
            return null;
        }

        var (cut, generation) = picked;
        lock (_lock)
        {
            // Released between the cut and here: the parameters are gone.
            if (Volatile.Read(ref _references) == 0 || !_streams.TryGetValue(generation, out var source))
            {
                cut.Dispose();
                throw new ObjectDisposedException(nameof(ReplayRingSink));
            }

            var streams = new List<ClipStream>(source.Count);
            try
            {
                foreach (var stream in source)
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

    /// <summary>Takes a reference, so the ring outlives <see cref="Dispose"/> until released; false once freed.</summary>
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
            _generations.Dispose();
            foreach (var list in _streams.Values.Distinct())
            {
                foreach (var stream in list)
                {
                    var parameters = stream.Parameters;
                    ffmpeg.avcodec_parameters_free(&parameters);
                }

                list.Clear();
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

/// <summary>One stream of a Clip: its codec parameters, title, default flag and time base.</summary>
internal sealed unsafe class ClipStream(AVCodecParameters* parameters, string? title, bool isDefault, AVRational timeBase)
{
    public AVCodecParameters* Parameters { get; } = parameters;

    public string? Title { get; } = title;

    public bool IsDefault { get; } = isDefault;

    public AVRational TimeBase { get; } = timeBase;
}

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
