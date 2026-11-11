using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;
using Encore.Platform.FFmpeg;
using VideoCodec = Encore.Contracts.Settings.VideoCodec;

namespace Encore.Engine.Sessions;

/// <summary>Chooses the encoder a spec is encoded with: the forced one, else the probe's for its codec, else H.264's.</summary>
internal sealed class EncoderResolver(string? forced, IVideoEncoderCatalog encoders, ILogger log)
{
    /// <summary>The encoder and codec for a spec; throws when not even H.264 has a working encoder.</summary>
    public (string Encoder, VideoCodec Codec) Resolve(SessionSpec spec)
    {
        if (forced is not null)
        {
            var forcedCodec = EncoderProbe.CodecOf(forced)
                ?? throw new EngineFailure(
                    ErrorCodes.EncodeFailed, $"'{forced}' is not an H.264, HEVC or AV1 encoder in this FFmpeg build.");
            log.LogInformation("Encoder forced to {Encoder} ({Codec})", forced, forcedCodec);
            return (forced, forcedCodec);
        }

        var codec = spec.Snapshot.Video.Codec;
        string? encoder;
        try
        {
            encoder = encoders.EncoderFor(codec);
        }
        catch (Exception e) when (codec != VideoCodec.H264)
        {
            // Only H.264's own probe failing fails the session; any other codec's falls back.
            log.LogWarning("The {Codec} encoder probe failed: {Message}", codec, e.Message);
            encoder = null;
        }

        if (encoder is not null)
        {
            return (encoder, codec);
        }

        log.LogWarning("No {Codec} encoder works on this PC; the session falls back to H.264", codec);
        return encoders.EncoderFor(VideoCodec.H264) is { } fallback
            ? (fallback, VideoCodec.H264)
            : throw new EngineFailure(ErrorCodes.EncodeFailed, "No H.264 encoder works on this PC.");
    }

    /// <summary>Whether this PC has a working encoder for the codec, by the probe; false when the probe fails.</summary>
    public bool CanEncode(VideoCodec codec)
    {
        try
        {
            return encoders.EncoderFor(codec) is not null;
        }
        catch (Exception e)
        {
            log.LogWarning("The {Codec} encoder probe failed: {Message}", codec, e.Message);
            return false;
        }
    }
}
