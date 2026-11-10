using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A pool texture held by value, for paths that must not allocate; it must reach a submit or Release exactly once.</summary>
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
