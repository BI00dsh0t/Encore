using FFmpeg.AutoGen.Abstractions;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// The Replay Buffer's encoders write through this (epic-recordings): every packet goes on to the
/// inner sink (the ring) unchanged, and while a <see cref="RecordingWriter"/> is attached, a new
/// reference to each is handed to it first. A packet costs the Recording one
/// <c>av_packet_clone</c> (a native reference, no byte copy) and one enqueue; with no writer, nothing.
/// It keeps its own copy of each stream's codec parameters, title, default flag and time base, so a
/// Recording can open its file without reaching into the ring.
/// </summary>
public sealed unsafe class RecordingTap : PacketSink, IDisposable
{
    private readonly PacketSink _inner;
    private readonly Lock _lock = new();
    private readonly List<ClipStream> _streams = [];
    private RecordingWriter? _writer;
    private bool _closed;
    private bool _disposed;

    public RecordingTap(PacketSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Whether a Recording is attached now.</summary>
    public bool HasWriter => Volatile.Read(ref _writer) is not null;

    internal override bool NeedsGlobalHeader => _inner.NeedsGlobalHeader;

    internal override int AddStream(AVCodecContext* encoder, string? title, bool isDefault)
    {
        var index = _inner.AddStream(encoder, title, isDefault);
        var parameters = ffmpeg.avcodec_parameters_alloc();
        if (parameters is null)
        {
            throw new InvalidOperationException("avcodec_parameters_alloc failed.");
        }

        try
        {
            FFmpegLibrary.Check(ffmpeg.avcodec_parameters_from_context(parameters, encoder), "avcodec_parameters_from_context");
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (index != _streams.Count)
                {
                    throw new InvalidOperationException($"The inner sink gave stream {index}; the tap holds {_streams.Count}.");
                }

                _streams.Add(new ClipStream(parameters, title, isDefault, encoder->time_base));
            }
        }
        catch
        {
            ffmpeg.avcodec_parameters_free(&parameters);
            throw;
        }

        return index;
    }

    internal override void Write(AVPacket* packet, int streamIndex, AVRational timeBase)
    {
        // The inner sink drops the priming packets too; a Recording never sees them.
        if (Volatile.Read(ref _writer) is { } writer && !BeforeStart(packet))
        {
            writer.Offer(packet, streamIndex);
        }

        _inner.Write(packet, streamIndex, timeBase);
    }

    /// <summary>
    /// Copies of every stream's parameters, title, default flag and time base, in index order, which
    /// outlive the tap; the caller owns them.
    /// </summary>
    public RecordingStreams CopyStreams()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var streams = new RecordingStreams();
            try
            {
                foreach (var stream in _streams)
                {
                    streams.Add(stream);
                }
            }
            catch
            {
                streams.Dispose();
                throw;
            }

            return streams;
        }
    }

    /// <summary>Hands every later packet to <paramref name="writer"/>; one writer at a time, never after <see cref="Close"/>.</summary>
    /// <exception cref="InvalidOperationException">A writer is attached, or the tap is closed.</exception>
    public void Attach(RecordingWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (_lock)
        {
            if (_closed)
            {
                throw new InvalidOperationException("The session's output has ended; there is nothing to record.");
            }

            if (_writer is not null)
            {
                throw new InvalidOperationException("A Recording is already attached.");
            }

            writer.Tap = this;
            Volatile.Write(ref _writer, writer);
        }
    }

    /// <summary>Stops handing packets to <paramref name="writer"/>, when it is the one attached.</summary>
    internal void Detach(RecordingWriter writer)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_writer, writer))
            {
                Volatile.Write(ref _writer, null);
            }
        }
    }

    /// <summary>
    /// The session's output has ended: no packet follows. The attached writer, if any, is detached
    /// and told, so it finishes with what it has.
    /// </summary>
    public void Close()
    {
        RecordingWriter? writer;
        lock (_lock)
        {
            _closed = true;
            writer = _writer;
            Volatile.Write(ref _writer, null);
        }

        writer?.OutputEnded();
    }

    /// <summary>Closes the tap and frees its own parameters; a writer's copies are its own.</summary>
    public void Dispose()
    {
        Close();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var stream in _streams)
            {
                var parameters = stream.Parameters;
                ffmpeg.avcodec_parameters_free(&parameters);
            }

            _streams.Clear();
        }
    }
}

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
