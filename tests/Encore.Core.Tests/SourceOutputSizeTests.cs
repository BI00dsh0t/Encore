using Encore.Contracts.Settings;
using Encore.Core.Output;

namespace Encore.Core.Tests;

/// <summary>Story 12.1: a game chain's output size under matchSource.</summary>
public sealed class SourceOutputSizeTests
{
    [Fact]
    public void TheGamesOwnSizeFittedOnlyToTheCodecAndNothingUnder640By360()
    {
        Assert.Equal((2560, 1440), SourceOutputSize.For(2560, 1440, VideoCodec.H264));
        Assert.Equal((4096, 1152), SourceOutputSize.For(5120, 1440, VideoCodec.H264));
        Assert.Equal((5120, 1440), SourceOutputSize.For(5120, 1440, VideoCodec.Hevc));
        Assert.Equal((1918, 1078), SourceOutputSize.For(1919, 1079, VideoCodec.H264));
        Assert.Null(SourceOutputSize.For(600, 300, VideoCodec.H264));
    }
}
