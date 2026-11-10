using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A pool texture to draw one frame into, then hand to <c>VideoEncoder.Submit</c>.</summary>
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
