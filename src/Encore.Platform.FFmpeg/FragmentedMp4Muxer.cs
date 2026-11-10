using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Core.Library;

namespace Encore.Platform.FFmpeg;

/// <summary>
/// Fragmented MP4 (AD-9): an empty moov, then a fragment at every keyframe and at least
/// every second. Encoders hand it packets in their own time base; it converts them to
/// stream time and writes them under one lock. A durable muxer (a Recording's, R3) instead
/// writes through a <see cref="DurableOutput"/>, cuts a fragment only at
/// <see cref="CutFragment"/>, and flushes the header and every fragment to the disk; at
/// <see cref="Finish"/> it gives the file its length in an <c>mehd</c> (R5, AD-27).
/// </summary>
public sealed unsafe class FragmentedMp4Muxer : PacketSink, IDisposable
{
    // MKTAG('h', 'v', 'c', '1').
    private const uint HevcTag = 'h' | ('v' << 8) | ('c' << 16) | ((uint)'1' << 24);

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

    /// <param name="path">The file to write.</param>
    /// <param name="logger">The muxer's log.</param>
    /// <param name="durable">
    /// Write through a <see cref="DurableOutput"/>: packets go straight in (<c>av_write_frame</c>),
    /// a fragment ends only at <see cref="CutFragment"/>, and the header and each fragment are
    /// flushed to the disk before the call returns. A write failure is rethrown as it was thrown.
    /// </param>
    /// <param name="flushToDisk">A durable muxer's flushes reach the disk, not just the OS; false only for tests.</param>
    /// <param name="capacity">A durable muxer's file fails as a full disk past this many bytes; a test seam, null for none.</param>
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

    /// <summary>A durable muxer's file length after its last successful flush: the header and every whole fragment; 0 before.</summary>
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
        return Describe(stream, encoder->time_base, title, isDefault);
    }

    /// <summary>
    /// Adds a stream from copied codec parameters (a Recording's, from a <see cref="RecordingTap"/>)
    /// in <paramref name="timeBase"/>. Call before <see cref="Start"/>.
    /// </summary>
    internal int AddStream(AVCodecParameters* parameters, AVRational timeBase, string? title, bool isDefault)
    {
        var stream = NewStream();
        FFmpegLibrary.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, parameters), "avcodec_parameters_copy");
        return Describe(stream, timeBase, title, isDefault);
    }

    private AVStream* NewStream()
    {
        if (_started)
        {
            throw new InvalidOperationException("Streams must be added before the header is written.");
        }

        var stream = ffmpeg.avformat_new_stream(_context, null);
        if (stream is null)
        {
            throw new InvalidOperationException("avformat_new_stream failed.");
        }

        return stream;
    }

    // The tag, time base, disposition and title every stream gets, whatever its parameters came from.
    private static int Describe(AVStream* stream, AVRational timeBase, string? title, bool isDefault)
    {
        if (stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
        {
            // hvc1 (parameter sets in the sample entry) rather than mp4's default hev1: Apple players need it.
            stream->codecpar->codec_tag = HevcTag;
        }

        stream->time_base = timeBase;
        stream->disposition = isDefault ? ffmpeg.AV_DISPOSITION_DEFAULT : 0;
        if (title is not null)
        {
            ffmpeg.av_dict_set(&stream->metadata, "title", title, 0);
            ffmpeg.av_dict_set(&stream->metadata, "handler_name", title, 0);
        }

        return stream->index;
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
            // fMP4 with an empty moov has no edit list, so a packet that ends at or before
            // time 0 (AAC's 1024-sample priming) would be played and delay the stream.
            if (!_started || _finished || BeforeStart(packet))
            {
                ffmpeg.av_packet_unref(packet);
                return;
            }

            packet->stream_index = streamIndex;
            ffmpeg.av_packet_rescale_ts(packet, timeBase, _context->streams[streamIndex]->time_base);
            if (_durable)
            {
                // av_write_frame keeps its own reference; this one is the caller's to drop.
                try
                {
                    var (dts, duration) = (packet->dts, packet->duration);
                    Check(ffmpeg.av_write_frame(_context, packet), "av_write_frame");
                    if (dts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        if (_firstDts[streamIndex] == ffmpeg.AV_NOPTS_VALUE)
                        {
                            _firstDts[streamIndex] = dts;
                        }

                        _lastEnd[streamIndex] = dts + duration;
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }
            else
            {
                FFmpegLibrary.Check(ffmpeg.av_interleaved_write_frame(_context, packet), "av_interleaved_write_frame");
            }
        }
    }

    /// <summary>
    /// A durable muxer's fragment ends here: every packet written since the last cut goes out as one
    /// <c>moof</c> + <c>mdat</c>, flushed to the disk before this returns.
    /// </summary>
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

    /// <summary>
    /// After the trailer: patches the moov in place with an <c>mehd</c> of the file's length (the
    /// longest stream's, as movenc's <c>track_duration</c>), so Media Foundation plays it to its end
    /// and not to its last <c>tfra</c> entry. Two ordered writes, each flushed to the disk, with a
    /// valid file between them (<see cref="Mp4File.PlanFragmentDuration"/>). A layout it doesn't
    /// expect, or a failed write, leaves the file as written (it plays to its last fragment) with a
    /// warning; it never fails the Recording.
    /// </summary>
    private void WriteFragmentDuration()
    {
        long durationUs = 0;
        for (var i = 0; i < _firstDts.Length; i++)
        {
            if (_firstDts[i] != ffmpeg.AV_NOPTS_VALUE)
            {
                var timeBase = _context->streams[i]->time_base;
                durationUs = Math.Max(durationUs, ffmpeg.av_rescale_rnd(_lastEnd[i] - _firstDts[i], 1_000_000L * timeBase.num, timeBase.den, AVRounding.AV_ROUND_UP));
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

    /// <summary>
    /// <see cref="FFmpegLibrary.Check"/>, except that a durable muxer whose write failed rethrows that
    /// failure as it was thrown (a full disk's <see cref="IOException"/>, for 6.7).
    /// </summary>
    private int Check(int result, string operation)
    {
        if (result < 0)
        {
            _output?.ThrowIfFailed();
        }

        return FFmpegLibrary.Check(result, operation);
    }
}
