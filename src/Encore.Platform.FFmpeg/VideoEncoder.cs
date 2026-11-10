using System.Globalization;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Settings;
using Encore.Media;

namespace Encore.Platform.FFmpeg;

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
/// A pool texture held by value, for paths that must not allocate per frame. From
/// <see cref="VideoEncoder.TryAcquire(long, out EncoderFrame)"/> (its PTS set from the capture QPC)
/// or <see cref="VideoEncoder.TryAcquireFrame"/> (PTS left to the pacer); it must reach a submit or
/// <see cref="VideoEncoder.Release"/> exactly once.
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

/// <summary>
/// What a <see cref="VideoEncoder"/> encodes with. The defaults are the tracer's: h264_nvenc, p5,
/// 60 fps, CBR 20 Mbps.
/// </summary>
/// <param name="Preset">NVENC preset, <c>p1</c> (fastest) to <c>p7</c>; other encoders ignore it.</param>
/// <param name="Tune">NVENC tune (<c>hq</c>, <c>ll</c>, <c>ull</c>), or null to leave FFmpeg's default; other encoders ignore it.</param>
/// <param name="Encoder">The FFmpeg encoder name, e.g. <c>hevc_nvenc</c> or <c>libx264</c>.</param>
/// <param name="Codec">The codec <paramref name="Encoder"/> must encode.</param>
/// <param name="Fps">The nominal frame rate: the GOP is this many frames (1 s).</param>
/// <param name="BitrateKbps">The target bitrate.</param>
/// <param name="RateControl">CBR, or VBR peaking at 1.5× the target.</param>
public sealed record VideoEncoderOptions(
    string Preset = VideoEncoderOptions.DefaultPreset,
    string? Tune = null,
    string Encoder = VideoEncoderOptions.DefaultEncoder,
    VideoCodec Codec = VideoCodec.H264,
    int Fps = VideoEncoderOptions.DefaultFps,
    int BitrateKbps = VideoEncoderOptions.DefaultBitrateKbps,
    RateControl RateControl = RateControl.Cbr)
{
    public const string DefaultPreset = "p5";
    public const string DefaultEncoder = "h264_nvenc";
    public const int DefaultFps = 60;
    public const int DefaultBitrateKbps = 20_000;

    public static VideoEncoderOptions Default { get; } = new();
}

/// <summary>
/// One video encoder over D3D11 frames: NVENC, AMF or QSV read the pool textures directly; libx264
/// gets each frame copied back through a staging texture (FFmpeg's D3D11 transfer) and converted
/// from BGRA to yuv420p by swscale into preallocated native frames. No B-frames; the GOP is the fps
/// (1 s). PTS are in 1/90000: a session's pacer gives each frame its slot's PTS
/// (<see cref="PacedVideoEncoder"/>); the variable-rate API (<see cref="TryAcquire(long)"/>, for the
/// probe and tests) takes capture QPC relative to the anchor. An IDR is also forced once a second
/// of PTS has passed since the last one, and on the first frame captured after a save's press
/// (<see cref="RequestKeyframe"/>, the save's cut, AD-10). NVENC and QSV return each packet as soon as its frame is
/// encoded. BGRA frames go in as-is (X1); NV12 frames (X2) are tagged BT.709 limited range.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    public const int TimeBase = 90_000;
    public const int NvencSurfaces = 8;

    /// <summary>
    /// The frame pool: NVENC's surfaces, plus the frame being drawn, the pacer's hand-off, its held
    /// frame and the last frame it keeps for repeats.
    /// </summary>
    public const int PoolSize = NvencSurfaces + 6;

    // Frames whose capture QPC is remembered for the latency of their packet.
    private const int LatencyRing = 32;

    private const string SoftwarePreset = "veryfast";

    // AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX: the encoder takes frames from a hw_frames_ctx.
    private const int HwFramesContextMethod = 0x02;

    private readonly D3D11VAFrames _frames;
    private readonly PacketSink? _muxer;
    private readonly long _qpcFrequency;
    private readonly long _anchorQpc;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    // Guards only the PTS order, so acquiring on the capture thread never waits on an encode.
    private readonly Lock _ptsLock = new();
    private readonly int _streamIndex = -1;
    private readonly long _fallbackDuration;
    private readonly LatencyHistogram? _latency;
    private AVCodecContext* _context;
    private AVPacket* _packet;

    // The software path: the downloaded frame, and (BGRA only) its yuv420p conversion.
    private AVFrame* _download;
    private AVFrame* _converted;
    private SwsContext* _sws;
    private readonly long[] _ringPts = new long[LatencyRing];
    private readonly long[] _ringQpc = new long[LatencyRing];
    private int _ringNext;
    private long _lastPts = long.MinValue;
    private long _lastKeyPts = long.MinValue;

    // Keyframe requests (AD-10): the latest ticket handed out and the latest press QPC asked for,
    // and the latest ticket an encoded frame served, with that frame's PTS. _keyLock guards them
    // all, and is only ever held for these reads and writes, never across an encode.
    private readonly Lock _keyLock = new();
    private long _keyRequests;
    private long _keyAfterQpc = long.MinValue;
    private long _servedTicket;
    private long _servedPts;
    private long _dropped;
    private long _encoded;
    private long _flushed;
    private long _poolMisses;

    /// <param name="muxer">Where packets go (a file or Rewind); null counts and discards them (the encoder probe).</param>
    /// <param name="latency">
    /// When given, each new frame's capture→encoded latency is recorded into it: QPC as its packet
    /// leaves <c>avcodec_receive_packet</c> minus the frame's capture QPC. Repeated frames have no
    /// capture QPC and record nothing. Packets drained by <see cref="Flush"/> are counted in
    /// <see cref="FlushedPackets"/> instead, since their latency includes shutdown.
    /// </param>
    public VideoEncoder(
        D3D11VAFrames frames,
        PacketSink? muxer,
        long qpcFrequency,
        long anchorQpc,
        ILogger logger,
        LatencyHistogram? latency = null,
        VideoEncoderOptions? settings = null)
    {
        settings ??= VideoEncoderOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.Fps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.BitrateKbps, 1);
        _latency = latency;
        _frames = frames;
        _muxer = muxer;
        _qpcFrequency = qpcFrequency;
        _anchorQpc = anchorQpc;
        _logger = logger;
        _fallbackDuration = Math.Max(1, TimeBase / settings.Fps);

        var codec = ffmpeg.avcodec_find_encoder_by_name(settings.Encoder);
        if (codec is null)
        {
            throw new InvalidOperationException($"This FFmpeg build has no {settings.Encoder} encoder.");
        }

        if (EncoderProbe.CodecOf(codec->id) != settings.Codec)
        {
            throw new ArgumentException($"{settings.Encoder} does not encode {settings.Codec}.", nameof(settings));
        }

        var family = EncoderProbe.FamilyOf(settings.Encoder);
        var software = !TakesD3D11Frames(codec);
        try
        {
            _context = ffmpeg.avcodec_alloc_context3(codec);
            _context->width = frames.Width;
            _context->height = frames.Height;
            _context->time_base = new AVRational { num = 1, den = TimeBase };
            _context->framerate = new AVRational { num = settings.Fps, den = 1 };
            if (software)
            {
                // NV12 goes in as it is; BGRA is converted with swscale's default (BT.601, limited).
                _context->pix_fmt = frames.IsNv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;
                if (!frames.IsNv12)
                {
                    _context->colorspace = AVColorSpace.AVCOL_SPC_SMPTE170M;
                    _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                }
            }
            else
            {
                _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
                _context->sw_pix_fmt = frames.IsNv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA;
                _context->hw_frames_ctx = ffmpeg.av_buffer_ref(frames.FramesRef);
            }

            if (frames.IsNv12)
            {
                // What FrameScaler writes in NV12, so the file decodes with the right colours.
                _context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
                _context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
                _context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
                _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            }

            _context->gop_size = settings.Fps;
            _context->max_b_frames = 0;
            var bitrate = settings.BitrateKbps * 1000L;
            var maxRate = settings.RateControl == RateControl.Cbr ? bitrate : bitrate * 3 / 2;
            _context->bit_rate = bitrate;
            _context->rc_max_rate = maxRate;
            _context->rc_buffer_size = (int)Math.Min(maxRate, int.MaxValue);
            if (muxer?.NeedsGlobalHeader ?? true)
            {
                _context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            AVDictionary* options = null;
            try
            {
                SetOptions(&options, family, settings);
                FFmpegLibrary.Check(ffmpeg.avcodec_open2(_context, codec, &options), $"avcodec_open2({settings.Encoder})");
            }
            finally
            {
                ffmpeg.av_dict_free(&options);
            }

            _packet = ffmpeg.av_packet_alloc();
            if (software)
            {
                OpenSoftwarePath();
            }

            if (muxer is not null)
            {
                _streamIndex = muxer.AddStream(_context, null, isDefault: true);
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        logger.LogInformation(
            "{Encoder} {Width}x{Height} {Format}{Path}, {Fps} fps, {RateControl} {Kbps} kbps, GOP {Gop}, preset {Preset}, tune {Tune}",
            settings.Encoder, frames.Width, frames.Height, frames.IsNv12 ? "NV12" : "BGRA", software ? " via system memory" : "",
            settings.Fps, settings.RateControl.ToString().ToUpperInvariant(), settings.BitrateKbps, settings.Fps,
            family == EncoderFamily.Nvenc ? settings.Preset : software ? SoftwarePreset : "default",
            family == EncoderFamily.Nvenc ? settings.Tune ?? "default" : "default");
    }

    /// <summary>Frames dropped because their PTS was not above the previous frame's.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Video packets written to the muxer.</summary>
    public long EncodedFrames => Interlocked.Read(ref _encoded);

    /// <summary>New frames' packets drained by <see cref="Flush"/>; their latency is not recorded. Repeats aren't counted.</summary>
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

    /// <summary>
    /// Takes a pool texture to draw a frame into, its PTS left to the pacer: false, counted in
    /// <see cref="PoolMisses"/>, when the pool is exhausted.
    /// </summary>
    public bool TryAcquireFrame(out EncoderFrame frame)
    {
        var pointer = _context is null ? null : _frames.TryAcquire();
        if (pointer is null)
        {
            Interlocked.Increment(ref _poolMisses);
            frame = default;
            return false;
        }

        frame = new EncoderFrame(pointer, 0);
        return true;
    }

    /// <summary>Frames <see cref="TryAcquireFrame"/> could not take because the pool was exhausted.</summary>
    public long PoolMisses => Interlocked.Read(ref _poolMisses);

    /// <summary>
    /// Asks for an IDR on the first frame encoded whose capture QPC is after
    /// <paramref name="afterQpc"/> (the press, AD-10), and returns the request's ticket for
    /// <see cref="TryGetForcedKey"/>. A frame captured at or before the press, still in the pacer's
    /// hand-off or held for its slot, is not it; a repeat counts as captured at its slot's QPC. Safe
    /// from any thread; it never waits on an encode. Requests outstanding together share one
    /// keyframe, after the latest of their QPCs, which also restarts the once-a-second count.
    /// </summary>
    public long RequestKeyframe(long afterQpc)
    {
        lock (_keyLock)
        {
            _keyAfterQpc = Math.Max(_keyAfterQpc, afterQpc);
            return ++_keyRequests;
        }
    }

    /// <summary>
    /// The PTS of the keyframe that served <paramref name="ticket"/> from <see cref="RequestKeyframe"/>;
    /// false until a frame captured after its press has been encoded. Requests are seconds apart, so the
    /// answer is the ticket's own keyframe.
    /// </summary>
    public bool TryGetForcedKey(long ticket, out long pts)
    {
        lock (_keyLock)
        {
            pts = _servedPts;
            return _servedTicket >= ticket;
        }
    }

    /// <summary>
    /// Encodes <paramref name="frame"/> (consumed) at slot PTS <paramref name="pts"/>.
    /// <paramref name="captureQpc"/> is the new frame's capture QPC, or 0 for a repeat, which
    /// records no latency.
    /// </summary>
    internal void SubmitSlot(AVFrame* frame, long pts, long captureQpc) => Encode(frame, pts, captureQpc);

    /// <summary>A new reference to <paramref name="frame"/>'s texture, for a repeat; native, never managed.</summary>
    internal static AVFrame* Reference(AVFrame* frame)
    {
        var clone = ffmpeg.av_frame_clone(frame);
        if (clone is null)
        {
            throw new InvalidOperationException("av_frame_clone failed.");
        }

        return clone;
    }

    /// <summary>Frees a frame from <see cref="Reference"/> or the pool unencoded.</summary>
    internal static void Free(AVFrame* frame)
    {
        if (frame is not null)
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>Encodes a surface from <see cref="TryAcquire(long)"/> and writes its packets.</summary>
    public void Submit(VideoSurface surface) => Encode(surface.Detach(), surface.Pts, CaptureQpcOf(surface.Pts));

    /// <summary>Encodes a frame from <see cref="TryAcquire(long, out EncoderFrame)"/> and writes its packets.</summary>
    public void Submit(EncoderFrame frame) => Encode(frame.Frame, frame.Pts, CaptureQpcOf(frame.Pts));

    /// <summary>Hands a frame from <see cref="TryAcquire(long, out EncoderFrame)"/> back to the pool unencoded.</summary>
    public static void Release(EncoderFrame frame)
    {
        var pointer = frame.Frame;
        if (pointer is not null)
        {
            ffmpeg.av_frame_free(&pointer);
        }
    }

    /// <summary>The variable-rate API's capture QPC: the anchor plus the PTS.</summary>
    private long CaptureQpcOf(long pts) => _anchorQpc + QpcClock.ToUnits(pts, TimeBase, _qpcFrequency);

    private void Encode(AVFrame* frame, long pts, long captureQpc)
    {
        try
        {
            lock (_lock)
            {
                _ringPts[_ringNext] = pts;
                _ringQpc[_ringNext] = captureQpc;
                _ringNext = (_ringNext + 1) % LatencyRing;

                var input = _download is null ? frame : Download(frame);
                input->pts = pts;

                // The software path reuses its frames: clear what the last keyframe set.
                input->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                input->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;

                // Every request outstanding when this frame is taken is served by it, if it was
                // captured after the requests' press; a repeat (0) is taken at its slot's QPC.
                var frameQpc = captureQpc != 0 ? captureQpc : CaptureQpcOf(pts);
                long requested;
                bool forced;
                lock (_keyLock)
                {
                    requested = _keyRequests;
                    forced = requested > _servedTicket && frameQpc > _keyAfterQpc;
                }

                if (forced || _lastKeyPts == long.MinValue || pts - _lastKeyPts >= TimeBase)
                {
                    input->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                    input->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
                    _lastKeyPts = pts;
                }

                FFmpegLibrary.Check(ffmpeg.avcodec_send_frame(_context, input), "avcodec_send_frame(video)");
                if (forced)
                {
                    lock (_keyLock)
                    {
                        _servedPts = pts;
                        _servedTicket = requested;
                    }
                }

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
            if (_sws is not null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }

            if (_converted is not null)
            {
                fixed (AVFrame** converted = &_converted)
                {
                    ffmpeg.av_frame_free(converted);
                }
            }

            if (_download is not null)
            {
                fixed (AVFrame** download = &_download)
                {
                    ffmpeg.av_frame_free(download);
                }
            }

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

    /// <summary>Whether the encoder reads D3D11 frames itself; one that doesn't gets system-memory frames.</summary>
    private static bool TakesD3D11Frames(AVCodec* codec)
    {
        for (var i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config is null)
            {
                return false;
            }

            if (config->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11
                && (config->methods & HwFramesContextMethod) != 0)
            {
                return true;
            }
        }
    }

    /// <summary>Rate control and the family's own options; preset and tune apply only to NVENC.</summary>
    private static void SetOptions(AVDictionary** options, EncoderFamily family, VideoEncoderOptions settings)
    {
        var cbr = settings.RateControl == RateControl.Cbr;
        switch (family)
        {
            case EncoderFamily.Nvenc:
                ffmpeg.av_dict_set(options, "preset", settings.Preset, 0);
                if (settings.Tune is not null)
                {
                    ffmpeg.av_dict_set(options, "tune", settings.Tune, 0);
                }

                ffmpeg.av_dict_set(options, "rc", cbr ? "cbr" : "vbr", 0);
                ffmpeg.av_dict_set(options, "surfaces", NvencSurfaces.ToString(CultureInfo.InvariantCulture), 0);
                ffmpeg.av_dict_set(options, "forced-idr", "1", 0); // a forced I frame is an IDR, so a keyframe
                ffmpeg.av_dict_set(options, "delay", "0", 0); // each packet out as soon as its frame is encoded
                break;
            case EncoderFamily.Amf:
                ffmpeg.av_dict_set(options, "rc", cbr ? "cbr" : "vbr_peak", 0);
                ffmpeg.av_dict_set(options, "forced_idr", "1", 0);
                break;
            case EncoderFamily.Qsv:
                // QSV picks CBR when bit_rate equals maxrate, and VBR when maxrate is above it.
                ffmpeg.av_dict_set(options, "forced_idr", "1", 0);
                ffmpeg.av_dict_set(options, "async_depth", "1", 0); // likewise
                break;
            case EncoderFamily.X264:
                ffmpeg.av_dict_set(options, "preset", SoftwarePreset, 0);
                ffmpeg.av_dict_set(options, "forced-idr", "1", 0);
                if (cbr)
                {
                    ffmpeg.av_dict_set(options, "nal-hrd", "cbr", 0);
                }

                break;
        }
    }

    /// <summary>Allocates the native frames and the swscale context the software path reuses for every frame.</summary>
    private void OpenSoftwarePath()
    {
        _download = AllocateFrame(_frames.IsNv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA);
        if (_frames.IsNv12)
        {
            return;
        }

        _download->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        _converted = AllocateFrame(AVPixelFormat.AV_PIX_FMT_YUV420P);
        _converted->colorspace = AVColorSpace.AVCOL_SPC_SMPTE170M;
        _converted->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        _sws = ffmpeg.sws_getContext(
            _frames.Width, _frames.Height, AVPixelFormat.AV_PIX_FMT_BGRA,
            _frames.Width, _frames.Height, AVPixelFormat.AV_PIX_FMT_YUV420P,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws is null)
        {
            throw new InvalidOperationException("sws_getContext(BGRA → yuv420p) failed.");
        }
    }

    private AVFrame* AllocateFrame(AVPixelFormat format)
    {
        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)format;
        frame->width = _frames.Width;
        frame->height = _frames.Height;
        var result = ffmpeg.av_frame_get_buffer(frame, 0);
        if (result < 0)
        {
            ffmpeg.av_frame_free(&frame);
            FFmpegLibrary.Check(result, "av_frame_get_buffer");
        }

        return frame;
    }

    /// <summary>
    /// Copies a pool texture to system memory through FFmpeg's staging texture and, for BGRA,
    /// converts it to yuv420p. Both frames are reused; one the encoder still holds is reallocated
    /// natively by <c>av_frame_make_writable</c>.
    /// </summary>
    private AVFrame* Download(AVFrame* hardware)
    {
        FFmpegLibrary.Check(ffmpeg.av_frame_make_writable(_download), "av_frame_make_writable");
        FFmpegLibrary.Check(ffmpeg.av_hwframe_transfer_data(_download, hardware, 0), "av_hwframe_transfer_data");
        if (_converted is null)
        {
            return _download;
        }

        FFmpegLibrary.Check(ffmpeg.av_frame_make_writable(_converted), "av_frame_make_writable");
        FFmpegLibrary.Check(ffmpeg.sws_scale_frame(_sws, _converted, _download), "sws_scale_frame");
        return _converted;
    }

    /// <summary>The capture QPC remembered for a packet's PTS; 0 for a repeat, or a PTS no longer remembered.</summary>
    private long CaptureQpcOfPacket(long pts)
    {
        for (var i = 0; i < LatencyRing; i++)
        {
            if (_ringPts[i] == pts && _ringQpc[i] != 0)
            {
                return _ringQpc[i];
            }
        }

        return 0;
    }

    private void Drain(bool flushing)
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == FFmpegLibrary.ErrorAgain || result == FFmpegLibrary.ErrorEof)
            {
                return;
            }

            FFmpegLibrary.Check(result, "avcodec_receive_packet(video)");
            if (flushing)
            {
                if (CaptureQpcOfPacket(_packet->pts) != 0)
                {
                    Interlocked.Increment(ref _flushed);
                }
            }
            else if (_latency is not null && CaptureQpcOfPacket(_packet->pts) is var captured and not 0)
            {
                _latency.Record(System.Diagnostics.Stopwatch.GetTimestamp() - captured);
            }

            // Packets come without a duration; the nominal 1/fps sizes the last sample of a fragment.
            if (_packet->duration == 0)
            {
                _packet->duration = _fallbackDuration;
            }

            if (_muxer is null)
            {
                ffmpeg.av_packet_unref(_packet);
            }
            else
            {
                _muxer.Write(_packet, _streamIndex, _context->time_base);
            }

            Interlocked.Increment(ref _encoded);
        }
    }
}
