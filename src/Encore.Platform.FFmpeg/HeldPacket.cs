using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A Recording's own packet reference waiting on its writer thread, with its start and centre in µs since the anchor.</summary>
internal readonly unsafe record struct HeldPacket(nint Packet, int Stream, long TimeUs, long AtUs)
{
    /// <summary>Whether the packet is a keyframe.</summary>
    public bool IsKey => (((AVPacket*)Packet)->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

    /// <summary>Frees a packet reference the writer owns.</summary>
    public static void Free(nint packet)
    {
        var pointer = (AVPacket*)packet;
        ffmpeg.av_packet_free(&pointer);
    }
}
