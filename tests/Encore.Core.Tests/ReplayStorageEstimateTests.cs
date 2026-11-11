using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Replay;
using Encore.Core.Settings;

namespace Encore.Core.Tests;

/// <summary>The storage estimate against a real ring, and the flags <see cref="ReplayStorageEstimate.For"/> sets.</summary>
public sealed class ReplayStorageEstimateTests
{
    private const long Second = 1_000_000;

    [Fact]
    public void AFiveMinuteMemoryRingOfCbrIsWithinTwentyPercentOfTheEstimate()
    {
        // 4 Mbps at 30 fps; the helper's audio is one Track.
        const int kbps = 4000;
        using var ring = new PacketRing(300 * Second, capacity: 40_000);
        var (video, audio) = PacketRingTests.Streams(ring);
        PacketRingTests.Feed(ring, video, audio, seconds: 320, videoBytes: kbps * 1000 / 8 / 30);

        var measured = (long)ring.LiveSlabs * PacketRing.DefaultSlabBytes;
        var estimate = ReplayStorageEstimate.Bytes(new VideoSettings(VideoCodec.H264, 1920, 1080, 30, kbps, RateControl.Cbr), 1, 300);

        Assert.InRange(measured, estimate * 0.8, estimate * 1.2);
    }

    [Fact]
    public void ForMarksVbrAsWorstCaseAndWarnsOnlyWhenTheFreeBytesAreKnownAndShort()
    {
        var cbr = Global(RateControl.Cbr);
        var vbr = Global(RateControl.Vbr);
        var bytes = ReplayStorageEstimate.Bytes(cbr.Video, 1, 300);

        Assert.Equal(new StorageEstimate(ReplayStorage.Memory, bytes, false, 32L << 30, false), ReplayStorageEstimate.For(cbr, 32L << 30));

        var worst = ReplayStorageEstimate.For(vbr, 1000);
        Assert.True(worst.WorstCase);
        Assert.True(worst.ExceedsFree);
        Assert.Equal(ReplayStorageEstimate.Bytes(vbr.Video, 1, 300), worst.Bytes);
        Assert.True(worst.Bytes > bytes);

        var unknown = ReplayStorageEstimate.For(cbr, null);
        Assert.Null(unknown.FreeBytes);
        Assert.False(unknown.ExceedsFree);
    }

    private static EffectiveSettings Global(RateControl rateControl) => SettingsResolver.Resolve(
        SettingsDocument.Empty with
        {
            Global = SettingsMaps.EmptyValues.Add(Keys.Video.Name, Keys.Video.ToJson(Keys.Video.Default with { RateControl = rateControl })),
        },
        null);
}
