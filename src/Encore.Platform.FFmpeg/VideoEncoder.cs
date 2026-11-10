using System.Globalization;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Settings;
using Encore.Media;

namespace Encore.Platform.FFmpeg;

/// <summary>One video encoder over D3D11 frames; hardware encoders read the pool textures directly. docs/code-notes.md.</summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    public const int TimeBase = 90_000;
    public const int NvencSurfaces = 8;

    /// <summary>The frame pool: NVENC's surfaces, plus the frames the capture thread and the pacer hold.</summary>
    public const int PoolSize = NvencSurfaces + 6;

    // Frames whose capture QPC is remembered for the latency of their packet.
    private const int LatencyRing = 32;

    private const string SoftwarePreset = "veryfast";

    // AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX: the encoder takes frames from a hw_frames_ctx.
    private const int HardwareFramesContextMethod = 0x02;

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
    private readonly KeyframeRequests _keyframes = new();
    private readonly CaptureTimes _captureTimes = new(LatencyRing);
    private AVCodecContext* _context;
    private AVPacket* _packet;

    // The software path's reused frames; null when the encoder reads the pool textures itself.
    private SoftwareFrames? _software;
    private long _lastPts = long.MinValue;
    private long _lastKeyPts = long.MinValue;
    private long _dropped;
    private long _encoded;
    private long _flushed;
    private long _poolMisses;

    /// <summary>A null muxer counts and discards packets; latency, when given, records each new frame's capture-to-encoded time.</summary>
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

        var codec = FindEncoder(settings);
        var family = EncoderProbe.FamilyOf(settings.Encoder);
        var software = !TakesD3D11Frames(codec);
        try
        {
            _context = ffmpeg.avcodec_alloc_context3(codec);
            ConfigureFormat(settings.Fps, software);
            ConfigureRateControl(settings);
            OpenCodec(codec, family, settings);
            _packet = ffmpeg.av_packet_alloc();
            if (software)
            {
                _software = new SoftwareFrames(frames.Width, frames.Height);
                _software.Open(frames.IsNv12);
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

        LogOpened(settings, family, software);
    }

    /// <summary>Frames dropped because their PTS was not above the previous frame's.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Video packets written to the muxer.</summary>
    public long EncodedFrames => Interlocked.Read(ref _encoded);

    /// <summary>New frames' packets drained by <see cref="Flush"/>; their latency is not recorded.</summary>
    public long FlushedPackets => Interlocked.Read(ref _flushed);

    /// <summary>Frames <see cref="TryAcquireFrame"/> could not take because the pool was exhausted.</summary>
    public long PoolMisses => Interlocked.Read(ref _poolMisses);

    /// <summary>Takes a pool texture for a frame captured at a QPC time; null when its PTS isn't above the last one's.</summary>
    public VideoSurface? TryAcquire(long qpc) =>
        TryAcquire(qpc, out var frame) ? new VideoSurface(frame.Frame, frame.Pts) : null;

    /// <summary>The allocation-free form of <see cref="TryAcquire(long)"/>: false when the PTS isn't above the last one's.</summary>
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

    /// <summary>Takes a pool texture with its PTS left to the pacer; false, counted in PoolMisses, when the pool is exhausted.</summary>
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

    /// <summary>Asks for an IDR on the first frame captured after a QPC time and returns a ticket. Safe from any thread.</summary>
    public long RequestKeyframe(long afterQpc) => _keyframes.Request(afterQpc);

    /// <summary>The PTS of the keyframe that served a ticket; false until a frame captured after its press has been encoded.</summary>
    public bool TryGetForcedKey(long ticket, out long pts) => _keyframes.TryGetServed(ticket, out pts);

    /// <summary>Encodes a frame (consumed) at a slot's PTS; a captureQpc of 0 marks a repeat, which records no latency.</summary>
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
            _software?.Dispose();
            _software = null;
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

    private static AVCodec* FindEncoder(VideoEncoderOptions settings)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(settings.Encoder);
        if (codec is null)
        {
            throw new InvalidOperationException($"This FFmpeg build has no {settings.Encoder} encoder.");
        }

        if (EncoderProbe.CodecOf(codec->id) != settings.Codec)
        {
            throw new ArgumentException($"{settings.Encoder} does not encode {settings.Codec}.", nameof(settings));
        }

        return codec;
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
                && (config->methods & HardwareFramesContextMethod) != 0)
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

    /// <summary>Size, time base, frame rate, pixel formats and colour: what the encoder reads and how the file says to show it.</summary>
    private void ConfigureFormat(int fps, bool software)
    {
        _context->width = _frames.Width;
        _context->height = _frames.Height;
        _context->time_base = new AVRational { num = 1, den = TimeBase };
        _context->framerate = new AVRational { num = fps, den = 1 };
        if (software)
        {
            // NV12 goes in as it is; BGRA is converted with swscale's default (BT.601, limited).
            _context->pix_fmt = _frames.IsNv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;
            if (!_frames.IsNv12)
            {
                _context->colorspace = AVColorSpace.AVCOL_SPC_SMPTE170M;
                _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            }
        }
        else
        {
            _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            _context->sw_pix_fmt = _frames.IsNv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA;
            _context->hw_frames_ctx = ffmpeg.av_buffer_ref(_frames.FramesRef);
        }

        if (_frames.IsNv12)
        {
            // What FrameScaler writes in NV12, so the file decodes with the right colours.
            _context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            _context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            _context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            _context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        }
    }

    /// <summary>A one-second GOP, no B-frames, and the bit rate: CBR caps at it, VBR peaks at one and a half times it.</summary>
    private void ConfigureRateControl(VideoEncoderOptions settings)
    {
        _context->gop_size = settings.Fps;
        _context->max_b_frames = 0;
        var bitrate = settings.BitrateKbps * 1000L;
        var maxRate = settings.RateControl == RateControl.Cbr ? bitrate : bitrate * 3 / 2;
        _context->bit_rate = bitrate;
        _context->rc_max_rate = maxRate;
        _context->rc_buffer_size = (int)Math.Min(maxRate, int.MaxValue);
        if (_muxer?.NeedsGlobalHeader ?? true)
        {
            _context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }
    }

    private void OpenCodec(AVCodec* codec, EncoderFamily family, VideoEncoderOptions settings)
    {
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
    }

    private void LogOpened(VideoEncoderOptions settings, EncoderFamily family, bool software)
    {
        var nvenc = family == EncoderFamily.Nvenc;
        _logger.LogInformation(
            "{Encoder} {Width}x{Height} {Format}{Path}, {Fps} fps, {RateControl} {Kbps} kbps, GOP {Gop}, preset {Preset}, tune {Tune}",
            settings.Encoder,
            _frames.Width,
            _frames.Height,
            _frames.IsNv12 ? "NV12" : "BGRA",
            software ? " via system memory" : "",
            settings.Fps,
            settings.RateControl.ToString().ToUpperInvariant(),
            settings.BitrateKbps,
            settings.Fps,
            nvenc ? settings.Preset : software ? SoftwarePreset : "default",
            nvenc ? settings.Tune ?? "default" : "default");
    }

    /// <summary>The variable-rate API's capture QPC: the anchor plus the PTS.</summary>
    private long CaptureQpcOf(long pts) => _anchorQpc + QpcClock.ToUnits(pts, TimeBase, _qpcFrequency);

    private void Encode(AVFrame* frame, long pts, long captureQpc)
    {
        try
        {
            lock (_lock)
            {
                _captureTimes.Remember(pts, captureQpc);
                var input = _software is null ? frame : _software.Download(frame);
                input->pts = pts;

                // The software path reuses its frames: clear what the last keyframe set.
                input->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                input->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;

                // This frame serves every outstanding request when it was captured after the press; a repeat counts at its slot.
                var frameQpc = captureQpc != 0 ? captureQpc : CaptureQpcOf(pts);
                var forced = _keyframes.IsDue(frameQpc, out var requested);
                if (forced || _lastKeyPts == long.MinValue || pts - _lastKeyPts >= TimeBase)
                {
                    MarkKeyframe(input, pts);
                }

                FFmpegLibrary.Check(ffmpeg.avcodec_send_frame(_context, input), "avcodec_send_frame(video)");
                if (forced)
                {
                    _keyframes.MarkServed(pts, requested);
                }

                Drain(flushing: false);
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    private void MarkKeyframe(AVFrame* input, long pts)
    {
        input->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
        input->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
        _lastKeyPts = pts;
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
            CountPacket(flushing);

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

    /// <summary>A new frame's packet: counted when flushed, else its capture-to-encoded latency recorded.</summary>
    private void CountPacket(bool flushing)
    {
        if (flushing)
        {
            if (_captureTimes.Find(_packet->pts) != 0)
            {
                Interlocked.Increment(ref _flushed);
            }
        }
        else if (_latency is not null && _captureTimes.Find(_packet->pts) is var captured and not 0)
        {
            _latency.Record(System.Diagnostics.Stopwatch.GetTimestamp() - captured);
        }
    }
}
