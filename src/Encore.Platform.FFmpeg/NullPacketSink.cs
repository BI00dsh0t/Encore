using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>
/// A sink that keeps nothing (6.9): behind a <see cref="RecordingTap"/>, it lets a session with no
/// Rewind and no file of its own, the Monitor session, feed only its Recording. Its streams
/// carry their codec headers out of band, as the ring's do, so the Recording's file can open from
/// the tap's copies.
/// </summary>
public sealed unsafe class NullPacketSink : PacketSink
{
    private int _streams;

    internal override bool NeedsGlobalHeader => true;

    internal override int AddStream(AVCodecContext* encoder, string? title, bool isDefault) => Interlocked.Increment(ref _streams) - 1;

    internal override void Write(AVPacket* packet, int streamIndex, AVRational timeBase) => ffmpeg.av_packet_unref(packet);
}
