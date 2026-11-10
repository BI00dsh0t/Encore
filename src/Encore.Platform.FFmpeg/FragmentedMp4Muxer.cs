using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Core.Library;

namespace Encore.Platform.FFmpeg;

/// <summary>Writes fragmented MP4 under one lock; a Recording's durable muxer flushes each fragment. docs/code-notes.md.</summary>
public sealed unsafe class FragmentedMp4Muxer : PacketSink, IDisposable
{
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly bool _durable;
    private readonly bool _flushToDisk;
    private readonly long? _capacity;
    private AVFormatContext* _context;
    private DurableOutput? _output;
    private bool _started;
    private bool _finished;

    // A durable muxer's per stream first DTS, last DTS and last duration, in stream time; for the mehd.
    private long[] _firstDts = [];
    private long[] _lastEnd = [];

    public FragmentedMp4Muxer(string path, ILogger logger)
        : this(path, logger, durable: false)
    {
    }

    /// <summary>A durable muxer cuts fragments only at <see cref="CutFragment"/> and flushes each to the disk before returning.</summary>
    internal FragmentedMp4Muxer(string path, ILogger logger, bool durable, bool flushToDisk = true, long? capacity = null)
    {
        Path = path;
        _logger = logger;
        _durable = durable;
        _flushToDisk = flushToDisk;
        _capacity = capacity;
        AVFormatContext* context = null;
        FFmpegLibrary.Check(ffmpeg.avformat_alloc_output_context2(&context, null, "mp4", path), "avformat_alloc_output_context2");
        _context = context;
    }

    public string Path { get; }

    /// <summary>A durable muxer's file length after its last good flush: the header and every whole fragment.</summary>
    internal long DurableBytes { get; private set; }

    /// <summary>The fragments a durable muxer has cut and flushed, not counting a cut that wrote nothing.</summary>
    internal int Fragments { get; private set; }

    /// <inheritdoc/>
    internal override bool NeedsGlobalHeader => (_context->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

    /// <summary>Adds a stream for an opened encoder. Call before <see cref="Start"/>.</summary>
    internal override int AddStream(AVCodecContext* encoder, string? title, bool isDefault)
    {
        var stream = NewStream();
        FFmpegLibrary.Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, encoder), "avcodec_parameters_from_context");
        return Mp4Streams.Describe(stream, encoder->time_base, title, isDefault);
    }

    /// <summary>Adds a stream from copied codec parameters, as a Recording does. Call before <see cref="Start"/>.</summary>
    internal int AddStream(AVCodecParameters* parameters, AVRational timeBase, string? title, bool isDefault)
    {
        var stream = NewStream();
        FFmpegLibrary.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, parameters), "avcodec_parameters_copy");
        return Mp4Streams.Describe(stream, timeBase, title, isDefault);
    }

    private AVStream* NewStream()
    {
        if (_started)
        {
            throw new InvalidOperationException("Streams must be added before the header is written.");
        }

        return Mp4Streams.New(_context);
    }

    /// <summary>Opens the file and writes the header.</summary>
    public void Start()
    {
        AVDictionary* options = null;
        if (_durable)
        {
            _output = new DurableOutput(Path, _flushToDisk, _capacity);
            _context->pb = _output.Context;
            _context->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            ffmpeg.av_dict_set(&options, "movflags", "frag_custom+empty_moov+default_base_moof", 0);
        }
        else
        {
            FFmpegLibrary.Check(ffmpeg.avio_open(&_context->pb, Path, ffmpeg.AVIO_FLAG_WRITE), $"avio_open({Path})");
            _context->max_interleave_delta = 500_000; // µs
            ffmpeg.av_dict_set(&options, "movflags", "frag_keyframe+empty_moov+default_base_moof", 0);
            ffmpeg.av_dict_set(&options, "frag_duration", "1000000", 0); // µs
        }

        try
        {
            Check(ffmpeg.avformat_write_header(_context, &options), "avformat_write_header");
            AVDictionaryEntry* unused = null;
            while ((unused = ffmpeg.av_dict_get(options, "", unused, ffmpeg.AV_DICT_IGNORE_SUFFIX)) is not null)
            {
                _logger.LogWarning("mp4 muxer ignored option {Key}", new string((sbyte*)unused->key));
            }
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        // The empty moov is on the disk before the first fragment.
        if (_output is not null)
        {
            _firstDts = new long[_context->nb_streams];
            _lastEnd = new long[_context->nb_streams];
            Array.Fill(_firstDts, ffmpeg.AV_NOPTS_VALUE);
            _output.Flush();
            DurableBytes = _output.Length;
        }

        _started = true;
        _logger.LogInformation("Writing {Path}", Path);
    }

    /// <summary>Writes one packet and takes its reference. <paramref name="timeBase"/> is the encoder's.</summary>
    internal override void Write(AVPacket* packet, int streamIndex, AVRational timeBase)
    {
        lock (_lock)
        {
            // With no edit list, a packet ending at or before time 0 (AAC's priming) would be played and delay the stream.
            if (!_started || _finished || BeforeStart(packet))
            {
                ffmpeg.av_packet_unref(packet);
                return;
            }

            packet->stream_index = streamIndex;
            ffmpeg.av_packet_rescale_ts(packet, timeBase, _context->streams[streamIndex]->time_base);
            if (_durable)
            {
                WriteDurable(packet, streamIndex);
            }
            else
            {
                FFmpegLibrary.Check(ffmpeg.av_interleaved_write_frame(_context, packet), "av_interleaved_write_frame");
            }
        }
    }

    /// <summary>Writes a packet into the fragment being built and notes its stream's span, for the mehd. Under the lock.</summary>
    private void WriteDurable(AVPacket* packet, int streamIndex)
    {
        // av_write_frame keeps its own reference; this one is the caller's to drop.
        try
        {
            var (dts, duration) = (packet->dts, packet->duration);
            Check(ffmpeg.av_write_frame(_context, packet), "av_write_frame");
            if (dts == ffmpeg.AV_NOPTS_VALUE)
            {
                return;
            }

            if (_firstDts[streamIndex] == ffmpeg.AV_NOPTS_VALUE)
            {
                _firstDts[streamIndex] = dts;
            }

            _lastEnd[streamIndex] = dts + duration;
        }
        finally
        {
            ffmpeg.av_packet_unref(packet);
        }
    }

    /// <summary>Ends a durable muxer's fragment: every packet since the last cut goes out together, flushed to the disk.</summary>
    internal void CutFragment()
    {
        lock (_lock)
        {
            if (!_durable)
            {
                throw new InvalidOperationException("Only a durable muxer cuts its own fragments.");
            }

            if (!_started || _finished)
            {
                return;
            }

            Check(ffmpeg.av_write_frame(_context, null), "av_write_frame(flush)");
            _output!.Flush();
            if (_output.Length > DurableBytes)
            {
                DurableBytes = _output.Length;
                Fragments++;
            }
        }
    }

    /// <summary>Flushes interleaving, writes the trailer and closes the file. Flush the encoders first.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (!_started || _finished)
            {
                return;
            }

            _finished = true;
            if (_durable)
            {
                // The trailer writes the last fragment and the mfra.
                Check(ffmpeg.av_write_trailer(_context), "av_write_trailer");
                _output!.Flush();
                WriteFragmentDuration();
                CloseOutput();
            }
            else
            {
                FFmpegLibrary.Check(ffmpeg.av_interleaved_write_frame(_context, null), "av_interleaved_write_frame(flush)");
                FFmpegLibrary.Check(ffmpeg.av_write_trailer(_context), "av_write_trailer");
                ffmpeg.avio_closep(&_context->pb);
            }

            _logger.LogInformation("Finished {Path}", Path);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_context is null)
            {
                return;
            }

            if (_output is not null)
            {
                // A custom pb is the DurableOutput's to free, never avio_closep's.
                CloseOutput();
            }
            else if (_context->pb is not null)
            {
                ffmpeg.avio_closep(&_context->pb);
            }

            ffmpeg.avformat_free_context(_context);
            _context = null;
        }
    }

    /// <summary>After the trailer: writes the file's length into the moov so players reach the end. Never fails the Recording.</summary>
    private void WriteFragmentDuration()
    {
        long durationUs = 0;
        for (var i = 0; i < _firstDts.Length; i++)
        {
            if (_firstDts[i] != ffmpeg.AV_NOPTS_VALUE)
            {
                var timeBase = _context->streams[i]->time_base;
                var streamUs = ffmpeg.av_rescale_rnd(
                    _lastEnd[i] - _firstDts[i], 1_000_000L * timeBase.num, timeBase.den, AVRounding.AV_ROUND_UP);
                durationUs = Math.Max(durationUs, streamUs);
            }
        }

        if (durationUs <= 0)
        {
            _logger.LogWarning("{Path} has no length to write into its mehd; it plays to its last fragment", Path);
            return;
        }

        if (Mp4File.PlanFragmentDuration(_output!.Header, durationUs) is not { } patch)
        {
            _logger.LogWarning("{Path}'s moov isn't laid out for an mehd; it plays to its last fragment", Path);
            return;
        }

        try
        {
            _output.Overwrite(patch.MehdOffset, patch.MehdAndFree);
            _output.Overwrite(patch.MvexOffset, patch.MvexSize);
            // A first write across a 512-byte sector is the only one a power cut can tear; FFmpeg fixes its offset.
            var straddles = patch.MehdOffset / 512 != (patch.MehdOffset + patch.MehdAndFree.Length - 1) / 512;
            _logger.LogInformation(
                "Wrote {Path}'s mehd: {Duration} at {Timescale} Hz ({Seconds:0.000} s); straddles a sector: {Straddles}",
                Path,
                patch.Duration,
                patch.Timescale,
                (double)patch.Duration / patch.Timescale,
                straddles);
        }
        catch (IOException e)
        {
            _logger.LogWarning("Could not write {Path}'s mehd; it plays to its last fragment: {Message}", Path, e.Message);
        }
    }

    private void CloseOutput()
    {
        _context->pb = null;
        _output?.Dispose();
        _output = null;
    }

    /// <summary>Like <see cref="FFmpegLibrary.Check"/>, but a durable muxer's write failure is rethrown as it was thrown.</summary>
    private int Check(int result, string operation)
    {
        if (result < 0)
        {
            _output?.ThrowIfFailed();
        }

        return FFmpegLibrary.Check(result, operation);
    }
}
