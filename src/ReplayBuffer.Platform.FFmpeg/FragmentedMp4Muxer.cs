using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// Fragmented MP4 (AD-9): an empty moov, then a fragment at every keyframe and at least
/// every second. Encoders hand it packets in their own time base; it converts them to
/// stream time and writes them under one lock.
/// </summary>
public sealed unsafe class FragmentedMp4Muxer : IDisposable
{
    // MKTAG('h', 'v', 'c', '1').
    private const uint HevcTag = 'h' | ('v' << 8) | ('c' << 16) | ((uint)'1' << 24);

    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private AVFormatContext* _context;
    private bool _started;
    private bool _finished;

    public FragmentedMp4Muxer(string path, ILogger logger)
    {
        Path = path;
        _logger = logger;
        AVFormatContext* context = null;
        FFmpegLibrary.Check(ffmpeg.avformat_alloc_output_context2(&context, null, "mp4", path), "avformat_alloc_output_context2");
        _context = context;
    }

    public string Path { get; }

    /// <summary>Encoders must set <c>AV_CODEC_FLAG_GLOBAL_HEADER</c> when this is true.</summary>
    internal bool NeedsGlobalHeader => (_context->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

    /// <summary>Adds a stream for an opened encoder. Call before <see cref="Start"/>.</summary>
    internal int AddStream(AVCodecContext* encoder, string? title, bool isDefault)
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

        FFmpegLibrary.Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, encoder), "avcodec_parameters_from_context");
        if (encoder->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
        {
            // hvc1 (parameter sets in the sample entry) rather than mp4's default hev1: Apple players need it.
            stream->codecpar->codec_tag = HevcTag;
        }

        stream->time_base = encoder->time_base;
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
        FFmpegLibrary.Check(ffmpeg.avio_open(&_context->pb, Path, ffmpeg.AVIO_FLAG_WRITE), $"avio_open({Path})");
        _context->max_interleave_delta = 500_000; // µs

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "movflags", "frag_keyframe+empty_moov+default_base_moof", 0);
        ffmpeg.av_dict_set(&options, "frag_duration", "1000000", 0); // µs
        try
        {
            FFmpegLibrary.Check(ffmpeg.avformat_write_header(_context, &options), "avformat_write_header");
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

        _started = true;
        _logger.LogInformation("Writing {Path}", Path);
    }

    /// <summary>Writes one packet and takes its reference. <paramref name="timeBase"/> is the encoder's.</summary>
    internal void Write(AVPacket* packet, int streamIndex, AVRational timeBase)
    {
        lock (_lock)
        {
            // fMP4 with an empty moov has no edit list, so a packet that ends at or before
            // time 0 (AAC's 1024-sample priming) would be played and delay the stream.
            var beforeStart = packet->pts != ffmpeg.AV_NOPTS_VALUE && packet->pts + packet->duration <= 0;
            if (!_started || _finished || beforeStart)
            {
                ffmpeg.av_packet_unref(packet);
                return;
            }

            packet->stream_index = streamIndex;
            ffmpeg.av_packet_rescale_ts(packet, timeBase, _context->streams[streamIndex]->time_base);
            FFmpegLibrary.Check(ffmpeg.av_interleaved_write_frame(_context, packet), "av_interleaved_write_frame");
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
            FFmpegLibrary.Check(ffmpeg.av_interleaved_write_frame(_context, null), "av_interleaved_write_frame(flush)");
            FFmpegLibrary.Check(ffmpeg.av_write_trailer(_context), "av_write_trailer");
            ffmpeg.avio_closep(&_context->pb);
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

            if (_context->pb is not null)
            {
                ffmpeg.avio_closep(&_context->pb);
            }

            ffmpeg.avformat_free_context(_context);
            _context = null;
        }
    }
}
