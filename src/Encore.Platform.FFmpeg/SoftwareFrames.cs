using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>The software path's reused frames: a pool texture copied to system memory and, for BGRA, converted to yuv420p.</summary>
internal sealed unsafe class SoftwareFrames(int width, int height) : IDisposable
{
    private AVFrame* _download;
    private AVFrame* _converted;
    private SwsContext* _scaler;

    /// <summary>Allocates the frames and the swscale context; what was made before a failure is freed by Dispose.</summary>
    public void Open(bool nv12)
    {
        _download = AllocateFrame(nv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_BGRA);
        if (nv12)
        {
            return;
        }

        _download->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        _converted = AllocateFrame(AVPixelFormat.AV_PIX_FMT_YUV420P);
        _converted->colorspace = AVColorSpace.AVCOL_SPC_SMPTE170M;
        _converted->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        _scaler = ffmpeg.sws_getContext(
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_scaler is null)
        {
            throw new InvalidOperationException("sws_getContext(BGRA → yuv420p) failed.");
        }
    }

    /// <summary>Copies a pool texture to system memory and, for BGRA, converts it; returns the frame to encode.</summary>
    public AVFrame* Download(AVFrame* hardware)
    {
        FFmpegLibrary.Check(ffmpeg.av_frame_make_writable(_download), "av_frame_make_writable");
        FFmpegLibrary.Check(ffmpeg.av_hwframe_transfer_data(_download, hardware, 0), "av_hwframe_transfer_data");
        if (_converted is null)
        {
            return _download;
        }

        FFmpegLibrary.Check(ffmpeg.av_frame_make_writable(_converted), "av_frame_make_writable");
        FFmpegLibrary.Check(ffmpeg.sws_scale_frame(_scaler, _converted, _download), "sws_scale_frame");
        return _converted;
    }

    public void Dispose()
    {
        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
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
    }

    private AVFrame* AllocateFrame(AVPixelFormat format)
    {
        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)format;
        frame->width = width;
        frame->height = height;
        var result = ffmpeg.av_frame_get_buffer(frame, 0);
        if (result < 0)
        {
            ffmpeg.av_frame_free(&frame);
            FFmpegLibrary.Check(result, "av_frame_get_buffer");
        }

        return frame;
    }
}
