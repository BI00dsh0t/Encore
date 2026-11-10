using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A sink that keeps nothing, so a session with no Rewind and no file can still feed a Recording through a tap.</summary>
public sealed unsafe class NullPacketSink : PacketSink
{
    private int _streams;

    internal override bool NeedsGlobalHeader => true;

    internal override int AddStream(AVCodecContext* encoder, string? title, bool isDefault) => Interlocked.Increment(ref _streams) - 1;

    internal override void Write(AVPacket* packet, int streamIndex, AVRational timeBase) => ffmpeg.av_packet_unref(packet);
}
