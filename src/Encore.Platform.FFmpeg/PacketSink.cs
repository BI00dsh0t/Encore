using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>
/// Where encoders put their packets (AD-7's encoded-packet bus): a file
/// (<see cref="FragmentedMp4Muxer"/>) or Rewind's ring (<see cref="ReplayRingSink"/>).
/// </summary>
public abstract unsafe class PacketSink
{
    /// <summary>Encoders must set <c>AV_CODEC_FLAG_GLOBAL_HEADER</c> when this is true.</summary>
    internal abstract bool NeedsGlobalHeader { get; }

    /// <summary>Adds a stream for an opened encoder, before its first packet; returns its index.</summary>
    internal abstract int AddStream(AVCodecContext* encoder, string? title, bool isDefault);

    /// <summary>Takes one packet and its reference. <paramref name="timeBase"/> is the encoder's.</summary>
    internal abstract void Write(AVPacket* packet, int streamIndex, AVRational timeBase);

    /// <summary>
    /// A packet that ends at or before time 0 (AAC's 1024-sample priming): played, it would delay
    /// the stream, so sinks drop it.
    /// </summary>
    protected static bool BeforeStart(AVPacket* packet) =>
        packet->pts != ffmpeg.AV_NOPTS_VALUE && packet->pts + packet->duration <= 0;
}
