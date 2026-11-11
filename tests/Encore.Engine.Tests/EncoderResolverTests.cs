using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

public sealed class EncoderResolverTests
{
    [Fact]
    public void ACodecWithNoWorkingEncoderFallsBackToH264()
    {
        var video = new VideoSettings(VideoCodec.Hevc, 1920, 1080, 60, 20_000, RateControl.Cbr);
        var document = SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) };
        var snapshot = Core.Settings.SettingsResolver.Resolve(document, null);
        var spec = new SessionSpec(SessionKind.Game, new CaptureTarget(0, 0, null), snapshot, SessionAudio.None);
        var resolver = new EncoderResolver(null, new OnlyH264(), NullLogger.Instance);

        Assert.Equal(("h264_nvenc", VideoCodec.H264), resolver.Resolve(spec));
        Assert.False(resolver.CanEncode(VideoCodec.Hevc));
    }

    private sealed class OnlyH264 : IVideoEncoderCatalog
    {
        public string? EncoderFor(VideoCodec codec) => codec == VideoCodec.H264 ? "h264_nvenc" : null;
    }
}
