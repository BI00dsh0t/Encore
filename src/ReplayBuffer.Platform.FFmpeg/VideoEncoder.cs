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
/// A pool texture held by value, for paths that must not allocate per frame (the encoder
/// thread). From <see cref="VideoEncoder.TryAcquire(long, out EncoderFrame)"/>; it must reach
/// <see cref="VideoEncoder.Submit(EncoderFrame)"/> or <see cref="VideoEncoder.Release"/> exactly once.
/// </summary>
public readonly unsafe struct EncoderFrame
{
    internal EncoderFrame(AVFrame* frame, long pts)
    {
        Frame = frame;
        Pts = pts;
    }

    /// <summary>The <c>ID3D11Texture2D*</c> texture array the frame lives in.</summary>
    public nint Texture => (nint)Frame->data[0];

    /// <summary>The array slice, which is also the subresource index.</summary>
    public uint Subresource => (uint)(nint)Frame->data[1];

    internal AVFrame* Frame { get; }

    internal long Pts { get; }
}

/// <summary>The h264_nvenc options a story 1.2 experiment may change; the defaults are the tracer's.</summary>
/// <param name="Preset">NVENC preset, <c>p1</c> (fastest) to <c>p7</c>.</param>
/// <param name="Tune">NVENC tune (<c>hq</c>, <c>ll</c>, <c>ull</c>), or null to leave FFmpeg's default.</param>
public sealed record VideoEncoderOptions(string Preset = VideoEncoderOptions.DefaultPreset, string? Tune = null)
{
    public const string DefaultPreset = "p5";

    public static VideoEncoderOptions Default { get; } = new();
}

/// <summary>
/// h264_nvenc over D3D11 frames: p5 by default, CBR 20 Mbps, 1 s GOP, no B-frames. PTS are capture
/// QPC relative to the session anchor, in 1/90000. Capture is variable-rate, so besides
/// NVENC's 60-frame GOP an IDR is forced once a second of PTS has passed since the last one.
/// BGRA frames go in as-is (X1); NV12 frames (X2) are tagged BT.709 limited range.
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

    // Guards only the PTS order, so acquiring on the capture thread never waits on an encode.
    private readonly Lock _ptsLock = new();
    private readonly int _streamIndex;
    private readonly LatencyHistogram? _latency;
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private long _lastPts = long.MinValue;
    private long _lastKeyPts = long.MinValue;
    private long _dropped;
    private long _encoded;
    private long _flushed;

    /// <param name="latency">
    /// When given, each packet's capture→encoded latency is recorded into it: QPC as the packet
    /// leaves <c>avcodec_receive_packet</c> minus its frame's capture QPC (anchor + PTS).
    /// Packets drained by <see cref="Flush"/> are counted in <see cref="FlushedPackets"/> instead,
    /// since their latency includes shutdown.
    /// </param>
    public VideoEncoder(
        D3D11VAFrames frames,
        FragmentedMp4Muxer muxer,
        long qpcFrequency,
        long anchorQpc,
        ILogger logger,
        LatencyHistogram? latency = null,
        VideoEncoderOptions? settings = null)
    {
        settings ??= VideoEncoderOptions.Default;
        _latency = latency;
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
        _context->sw_pix_fmt = frames.IsNv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA;
        if (frames.IsNv12)
        {
            // What Nv12Converter writes, so the file decodes with the right colours.
            _context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            _context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            _context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        }

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
        ffmpeg.av_dict_set(&options, "preset", settings.Preset, 0);
        if (settings.Tune is not null)
        {
            ffmpeg.av_dict_set(&options, "tune", settings.Tune, 0);
        }

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
        logger.LogInformation("h264_nvenc {Width}x{Height} {Format}, preset {Preset}, tune {Tune}, CBR 20 Mbps, GOP 60",
            frames.Width, frames.Height, frames.IsNv12 ? "NV12" : "BGRA", settings.Preset, settings.Tune ?? "default");
    }

    /// <summary>Frames dropped because their PTS was not above the previous frame's.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Video packets written to the muxer.</summary>
    public long EncodedFrames => Interlocked.Read(ref _encoded);

    /// <summary>Video packets drained by <see cref="Flush"/>; their latency is not recorded.</summary>
    public long FlushedPackets => Interlocked.Read(ref _flushed);

    /// <summary>
    /// Takes a pool texture for a frame captured at <paramref name="qpc"/>, or returns null
    /// when that frame's PTS isn't above the last one's (it is dropped).
    /// </summary>
    public VideoSurface? TryAcquire(long qpc) =>
        TryAcquire(qpc, out var frame) ? new VideoSurface(frame.Frame, frame.Pts) : null;

    /// <summary>
    /// The allocation-free form of <see cref="TryAcquire(long)"/>: false when the frame's PTS
    /// isn't above the last one's (it is dropped).
    /// </summary>
    public bool TryAcquire(long qpc, out EncoderFrame frame)
    {
        var pts = QpcClock.ToUnits(qpc - _anchorQpc, _qpcFrequency, TimeBase);
        lock (_ptsLock)
        {
            if (pts <= _lastPts || _context is null)
            {
                Interlocked.Increment(ref _dropped);
                frame = default;
                return false;
            }

            _lastPts = pts;
        }

        frame = new EncoderFrame(_frames.Acquire(), pts);
        return true;
    }

    /// <summary>Encodes a surface from <see cref="TryAcquire(long)"/> and writes its packets.</summary>
    public void Submit(VideoSurface surface) => Encode(surface.Detach(), surface.Pts);

    /// <summary>Encodes a frame from <see cref="TryAcquire(long, out EncoderFrame)"/> and writes its packets.</summary>
    public void Submit(EncoderFrame frame) => Encode(frame.Frame, frame.Pts);

    /// <summary>Hands a frame from <see cref="TryAcquire(long, out EncoderFrame)"/> back to the pool unencoded.</summary>
    public static void Release(EncoderFrame frame)
    {
        var pointer = frame.Frame;
        if (pointer is not null)
        {
            ffmpeg.av_frame_free(&pointer);
        }
    }

    private void Encode(AVFrame* frame, long pts)
    {
        try
        {
            frame->pts = pts;
            lock (_lock)
            {
                if (_lastKeyPts == long.MinValue || pts - _lastKeyPts >= TimeBase)
                {
                    frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                    frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
                    _lastKeyPts = pts;
                }

                FFmpegLibrary.Check(ffmpeg.avcodec_send_frame(_context, frame), "avcodec_send_frame(video)");
                Drain(flushing: false);
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
            Drain(flushing: true);
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

    private void Drain(bool flushing)
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            FFmpegLibrary.Check(result, "avcodec_receive_packet(video)");
            if (flushing)
            {
                Interlocked.Increment(ref _flushed);
            }
            else if (_latency is not null)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var captured = _anchorQpc + QpcClock.ToUnits(_packet->pts, TimeBase, _qpcFrequency);
                _latency.Record(now - captured);
            }

            // Frames are variable-rate; the nominal 1/60 s only sizes the last sample of a fragment.
            if (_packet->duration == 0)
            {
                _packet->duration = TimeBase / 60;
            }

            _muxer.Write(_packet, _streamIndex, _context->time_base);
            Interlocked.Increment(ref _encoded);
        }
    }
}
