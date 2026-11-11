using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

/// <summary>The catalog's published options, with the per-codec probe replaced.</summary>
public sealed class VideoEncoderCatalogTests
{
    [Fact]
    public void AProbeThatCantLoadFFmpegPublishesProbedOptionsWithNoCodecs()
    {
        var catalog = new VideoEncoderCatalog(
            NullLoggerFactory.Instance,
            _ => throw new ErrorCodeException(ErrorCodes.FFmpegMissing, "FFmpeg is missing"));

        var options = catalog.ProbeAll(TestContext.Current.CancellationToken);

        Assert.Equal(VideoOptions.FromProbe([]), options);
        Assert.True(options.Probed);
    }

    [Fact]
    public void ACodecWhoseProbeThrowsIsNotOfferedNotCachedAndProbedAgain()
    {
        var hevcProbes = 0;
        var catalog = new VideoEncoderCatalog(
            NullLoggerFactory.Instance,
            codec => codec switch
            {
                VideoCodec.Hevc when ++hevcProbes == 1 => throw new InvalidOperationException("no D3D11 device"),
                VideoCodec.Hevc => "hevc_nvenc",
                VideoCodec.H264 => "h264_nvenc",
                _ => "av1_nvenc",
            });

        var options = catalog.ProbeAll(TestContext.Current.CancellationToken);

        Assert.True(options.Probed);
        Assert.Equal(
            [new CodecOffer(VideoCodec.H264, "h264_nvenc", false), new CodecOffer(VideoCodec.Av1, "av1_nvenc", true)],
            options.Codecs);
        Assert.Equal("hevc_nvenc", catalog.EncoderFor(VideoCodec.Hevc));
        Assert.Equal(2, hevcProbes);

        // A result is cached.
        Assert.Equal("hevc_nvenc", catalog.EncoderFor(VideoCodec.Hevc));
        Assert.Equal(2, hevcProbes);
    }

    [Fact]
    public void OnlyCodecsWithAnEncoderAreOfferedAndHevcAndAv1AreMarked()
    {
        var catalog = new VideoEncoderCatalog(
            NullLoggerFactory.Instance,
            codec => codec switch
            {
                VideoCodec.H264 => "libx264",
                VideoCodec.Av1 => "av1_amf",
                _ => null,
            });

        var options = catalog.ProbeAll(TestContext.Current.CancellationToken);

        Assert.Equal(
            [new CodecOffer(VideoCodec.H264, "libx264", false), new CodecOffer(VideoCodec.Av1, "av1_amf", true)],
            options.Codecs);
        Assert.Null(catalog.EncoderFor(VideoCodec.Hevc));
    }
}
