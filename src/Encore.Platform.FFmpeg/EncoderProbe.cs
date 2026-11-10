using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Platform.FFmpeg;

/// <summary>The kind of an FFmpeg video encoder, which decides its rate-control options.</summary>
public enum EncoderFamily
{
    Nvenc,
    Amf,
    Qsv,
    X264,
    Other,
}

/// <summary>
/// Finds which codecs this PC can encode (R2, AD-14). Each candidate is opened on D3D11 frames of
/// the given device and fed one frame; it works when that frame comes out as a packet. Per codec
/// the candidates go NVENC, AMF, QSV, and the first that works is the codec's encoder. H.264 falls
/// back to libx264 only when no hardware H.264 encoder works; HEVC and AV1 have no software fallback.
/// </summary>
public static unsafe class EncoderProbe
{
    /// <summary>The software H.264 fallback.</summary>
    public const string SoftwareH264 = "libx264";

    /// <summary>The size of the probe frame.</summary>
    public const int ProbeWidth = 1280;

    public const int ProbeHeight = 720;

    private static readonly Lock LogGate = new();

    /// <summary>The codecs probed, in offer order.</summary>
    public static IReadOnlyList<VideoCodec> Codecs { get; } = [VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1];

    /// <summary>Every hardware candidate, in probe order: NVENC, AMF, QSV for each codec.</summary>
    public static IReadOnlyList<string> HardwareCandidates { get; } = [.. Codecs.SelectMany(Candidates)];

    /// <summary>The hardware encoders tried for <paramref name="codec"/>, in order.</summary>
    public static IReadOnlyList<string> Candidates(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => ["h264_nvenc", "h264_amf", "h264_qsv"],
        VideoCodec.Hevc => ["hevc_nvenc", "hevc_amf", "hevc_qsv"],
        VideoCodec.Av1 => ["av1_nvenc", "av1_amf", "av1_qsv"],
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
    };

    /// <summary>The family an encoder name belongs to, from its suffix.</summary>
    public static EncoderFamily FamilyOf(string encoder) =>
        encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? EncoderFamily.Nvenc
        : encoder.EndsWith("_amf", StringComparison.Ordinal) ? EncoderFamily.Amf
        : encoder.EndsWith("_qsv", StringComparison.Ordinal) ? EncoderFamily.Qsv
        : encoder == SoftwareH264 ? EncoderFamily.X264
        : EncoderFamily.Other;

    /// <summary>
    /// The codec of the FFmpeg encoder named <paramref name="encoder"/>, or null when this build has
    /// no such encoder or it encodes none of <see cref="Codecs"/>. FFmpeg must be initialized.
    /// </summary>
    public static VideoCodec? CodecOf(string encoder)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(encoder);
        return codec is null ? null : CodecOf(codec->id);
    }

    internal static VideoCodec? CodecOf(AVCodecID id) => id switch
    {
        AVCodecID.AV_CODEC_ID_H264 => VideoCodec.H264,
        AVCodecID.AV_CODEC_ID_HEVC => VideoCodec.Hevc,
        AVCodecID.AV_CODEC_ID_AV1 => VideoCodec.Av1,
        _ => null,
    };

    /// <summary>Probes every codec: the offers, in <see cref="Codecs"/> order, of those with a working encoder.</summary>
    /// <param name="d3d11Device">An <c>ID3D11Device*</c>; FFmpeg takes its own references.</param>
    public static IReadOnlyList<CodecOffer> Probe(nint d3d11Device, ILogger logger)
    {
        var offers = new List<CodecOffer>();
        foreach (var codec in Codecs)
        {
            if (ProbeCodec(d3d11Device, codec, logger) is { } offer)
            {
                offers.Add(offer);
            }
        }

        return offers;
    }

    /// <summary>The offer for <paramref name="codec"/>: its first working candidate, or null when none works.</summary>
    public static CodecOffer? ProbeCodec(nint d3d11Device, VideoCodec codec, ILogger logger)
    {
        var encoder = Choose(codec, name => Works(d3d11Device, name, logger));
        if (encoder is null)
        {
            logger.LogInformation("No working {Codec} encoder", codec);
            return null;
        }

        logger.LogInformation("{Codec} encodes with {Encoder}", codec, encoder);
        return new CodecOffer(codec, encoder, VideoOptions.MayNotPlayEverywhereFor(codec));
    }

    /// <summary>
    /// The encoder for <paramref name="codec"/>: its first candidate that <paramref name="works"/>,
    /// else libx264 for H.264 only, else null. Candidates are tried in order and no further once one works.
    /// </summary>
    public static string? Choose(VideoCodec codec, Func<string, bool> works) =>
        Candidates(codec).FirstOrDefault(works)
        ?? (codec == VideoCodec.H264 && works(SoftwareH264) ? SoftwareH264 : null);

    /// <summary>
    /// Whether <paramref name="encoder"/> opens on D3D11 BGRA frames of <paramref name="d3d11Device"/>
    /// and turns one frame into a packet. FFmpeg's own log is silenced meanwhile, since a candidate
    /// without its hardware fails loudly.
    /// </summary>
    public static bool Works(nint d3d11Device, string encoder, ILogger logger)
    {
        if (CodecOf(encoder) is not { } codec)
        {
            logger.LogInformation("{Encoder}: not in this FFmpeg build", encoder);
            return false;
        }

        lock (LogGate)
        {
            var level = ffmpeg.av_log_get_level();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var frames = new D3D11VAFrames(d3d11Device, ProbeWidth, ProbeHeight, VideoEncoder.PoolSize);
                var anchor = Stopwatch.GetTimestamp();
                using var video = new VideoEncoder(
                    frames, null, Stopwatch.Frequency, anchor, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    settings: new VideoEncoderOptions(Encoder: encoder, Codec: codec));
                if (!video.TryAcquire(anchor, out var frame))
                {
                    return false;
                }

                video.Submit(frame);
                video.Flush();
                var works = video.EncodedFrames > 0;
                logger.LogInformation("{Encoder}: {Result} in {Ms:0} ms", encoder, works ? "works" : "gave no packet",
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return works;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                logger.LogInformation("{Encoder}: fails ({Message})", encoder, e.Message);
                return false;
            }
            finally
            {
                ffmpeg.av_log_set_level(level);
            }
        }
    }
}
