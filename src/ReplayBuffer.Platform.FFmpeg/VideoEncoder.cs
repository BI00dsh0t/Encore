using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Media;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>A pool texture to draw one frame into, then hand to <see cref="VideoEncoder.Submit"/>.</summary>
public sealed unsafe class VideoSurface : IDisposable
{
    private AVFrame* _frame;

    internal VideoSurface(AVFrame* frame, long pts)
    {
        _frame = frame;
        Pts = pts;
    }

    /// <summary>The <c>ID3D11Texture2D*</c> texture array the frame lives in.</summary>
    public nint Texture => (nint)_frame->data[0];

    /// <summary>The array slice, which is also the subresource index.</summary>
    public uint Subresource => (uint)(nint)_frame->data[1];

    internal long Pts { get; }

    internal AVFrame* Detach()
    {
        var frame = _frame;
        _frame = null;
        return frame;
    }

    public void Dispose()
    {
        if (_frame is not null)
        {
            fixed (AVFrame** frame = &_frame)
            {
                ffmpeg.av_frame_free(frame);
            }
        }
    }
}

/// <summary>
/// h264_nvenc over D3D11 frames: p5, CBR 20 Mbps, 1 s GOP, no B-frames. PTS are capture
/// QPC relative to the session anchor, in 1/90000. Capture is variable-rate, so besides
/// NVENC's 60-frame GOP an IDR is forced once a second of PTS has passed since the last one.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    public const int TimeBase = 90_000;
    public const int NvencSurfaces = 8;

    /// <summary>The frame pool NVENC needs: its surfaces plus headroom for the frame being drawn.</summary>
    public const int PoolSize = NvencSurfaces + 4;

    private readonly D3D11VAFrames _frames;
    private readonly FragmentedMp4Muxer _muxer;
    private readonly long _qpcFrequency;
    private readonly long _anchorQpc;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly int _streamIndex;
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private long _lastPts = long.MinValue;
    private long _lastKeyPts = long.MinValue;
    private long _dropped;

    public VideoEncoder(D3D11VAFrames frames, FragmentedMp4Muxer muxer, long qpcFrequency, long anchorQpc, ILogger logger)
    {
        _frames = frames;
        _muxer = muxer;
        _qpcFrequency = qpcFrequency;
        _anchorQpc = anchorQpc;
        _logger = logger;

        var codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
        if (codec is null)
        {
            throw new InvalidOperationException("This FFmpeg build has no h264_nvenc encoder.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->width = frames.Width;
        _context->height = frames.Height;
        _context->time_base = new AVRational { num = 1, den = TimeBase };
        _context->framerate = new AVRational { num = 60, den = 1 };
        _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        _context->sw_pix_fmt = AVPixelFormat.AV_PIX_FMT_BGRA;
        _context->hw_frames_ctx = ffmpeg.av_buffer_ref(frames.FramesRef);
        _context->gop_size = 60;
        _context->max_b_frames = 0;
        _context->bit_rate = 20_000_000;
        _context->rc_max_rate = 20_000_000;
        _context->rc_buffer_size = 20_000_000;
        if (muxer.NeedsGlobalHeader)
        {
            _context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "preset", "p5", 0);
        ffmpeg.av_dict_set(&options, "rc", "cbr", 0);
        ffmpeg.av_dict_set(&options, "surfaces", NvencSurfaces.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
        ffmpeg.av_dict_set(&options, "forced-idr", "1", 0); // a forced I frame is an IDR, so a keyframe
        try
        {
            FFmpegLibrary.Check(ffmpeg.avcodec_open2(_context, codec, &options), "avcodec_open2(h264_nvenc)");
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        _packet = ffmpeg.av_packet_alloc();
        _streamIndex = muxer.AddStream(_context, null, isDefault: true);
        logger.LogInformation("h264_nvenc {Width}x{Height}, CBR 20 Mbps, GOP 60", frames.Width, frames.Height);
    }

    /// <summary>Frames dropped because their PTS was not above the previous frame's.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Takes a pool texture for a frame captured at <paramref name="qpc"/>, or returns null
    /// when that frame's PTS isn't above the last one's (it is dropped).
    /// </summary>
    public VideoSurface? TryAcquire(long qpc)
    {
        var pts = QpcClock.ToUnits(qpc - _anchorQpc, _qpcFrequency, TimeBase);
        lock (_lock)
        {
            if (pts <= _lastPts || _context is null)
            {
                Interlocked.Increment(ref _dropped);
                return null;
            }

            _lastPts = pts;
        }

        return new VideoSurface(_frames.Acquire(), pts);
    }

    /// <summary>Encodes a surface from <see cref="TryAcquire"/> and writes its packets.</summary>
    public void Submit(VideoSurface surface)
    {
        var frame = surface.Detach();
        try
        {
            frame->pts = surface.Pts;
            lock (_lock)
            {
                if (_lastKeyPts == long.MinValue || surface.Pts - _lastKeyPts >= TimeBase)
                {
                    frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                    frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
                    _lastKeyPts = surface.Pts;
                }

                FFmpegLibrary.Check(ffmpeg.avcodec_send_frame(_context, frame), "avcodec_send_frame(video)");
                Drain();
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>Drains the encoder into the muxer.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_context is null)
            {
                return;
            }

            FFmpegLibrary.Check(ffmpeg.avcodec_send_frame(_context, null), "avcodec_send_frame(video flush)");
            Drain();
            if (_dropped > 0)
            {
                _logger.LogInformation("Dropped {Count} video frames with non-increasing PTS", _dropped);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_packet is not null)
            {
                fixed (AVPacket** packet = &_packet)
                {
                    ffmpeg.av_packet_free(packet);
                }
            }

            if (_context is not null)
            {
                fixed (AVCodecContext** context = &_context)
                {
                    ffmpeg.avcodec_free_context(context);
                }
            }
        }
    }

    private void Drain()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            FFmpegLibrary.Check(result, "avcodec_receive_packet(video)");

            // Frames are variable-rate; the nominal 1/60 s only sizes the last sample of a fragment.
            if (_packet->duration == 0)
            {
                _packet->duration = TimeBase / 60;
            }

            _muxer.Write(_packet, _streamIndex, _context->time_base);
        }
    }
}
