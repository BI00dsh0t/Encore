using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Monitors;

namespace Encore.Core.Tests;

/// <summary>Story 11.2: Core's screen resolver and the shared canvas rule (AD-25).</summary>
public sealed class MonitorScreensTests
{
    // The builder's PC: a 2560x1440 primary, and a 3840x2160 to its right (physical pixels).
    private static readonly DisplayMonitor Qhd = new(@"\\.\DISPLAY1", "Odyssey", 2560, 1440, true, "path-qhd", 0, 0, 240);
    private static readonly DisplayMonitor Uhd = new(@"\\.\DISPLAY2", "U32", 3840, 2160, false, "path-uhd", 2560, 0, 144);

    [Fact]
    public void WithTheNewKeyNullTheOldKeyResolvesAsBefore()
    {
        Assert.Equal([Uhd], MonitorScreens.Resolve(null, @"\\.\display2", [Qhd, Uhd]).Screens);
        Assert.Equal([Qhd], MonitorScreens.Resolve(null, null, [Qhd, Uhd]).Screens);
        Assert.Equal([Qhd, Uhd], MonitorScreens.Resolve(MonitorRecordingScreens.AllScreens, @"\\.\DISPLAY2", [Qhd, Uhd]).Screens);
    }

    [Fact]
    public void ASelectionFollowsItsMonitorsByIdWhenTheirDeviceNamesSwap()
    {
        var swapped = new[] { Qhd with { DeviceName = @"\\.\DISPLAY2" }, Uhd with { DeviceName = @"\\.\DISPLAY1" } };

        var resolved = MonitorScreens.Resolve(MonitorRecordingScreens.Selected("path-uhd"), null, swapped);

        Assert.Equal(["path-uhd"], resolved.Screens.Select(s => s.MonitorId));
        Assert.Empty(resolved.Missing);
    }

    [Fact]
    public void AMissingSelectedMonitorIsReportedAndAnAllMissingSelectionResolvesToNone()
    {
        var some = MonitorScreens.Resolve(MonitorRecordingScreens.Selected("path-gone", @"\\.\DISPLAY1"), null, [Qhd, Uhd]);
        Assert.Equal([Qhd], some.Screens);
        Assert.Equal(["path-gone"], some.Missing);

        var none = MonitorScreens.Resolve(MonitorRecordingScreens.Selected("path-gone"), null, [Qhd, Uhd]);
        Assert.Empty(none.Screens);
        Assert.Equal(["path-gone"], none.Missing);
    }

    [Fact]
    public void TheCanvasRuleKeepsFullWidthWithHevcAndASingleScreenOnH264()
    {
        bool All(VideoCodec _) => true;

        var both = MonitorCanvasRule.Plan([Qhd, Uhd], VideoCodec.H264, All);
        Assert.Equal((VideoCodec.Hevc, 6400, 2160), (both.Codec, both.Width, both.Height));
        Assert.Equal([(0, 0), (2560, 0)], both.Areas.Select(a => (a.X, a.Y)));

        var noHevc = MonitorCanvasRule.Plan([Qhd, Uhd], VideoCodec.H264, c => c != VideoCodec.Hevc);
        Assert.Equal(VideoCodec.Av1, noHevc.Codec);

        var h264Only = MonitorCanvasRule.Plan([Qhd, Uhd], VideoCodec.H264, c => c == VideoCodec.H264);
        Assert.Equal((VideoCodec.H264, 4096, 1382, true), (h264Only.Codec, h264Only.Width, h264Only.Height, h264Only.Scaled));

        var one = MonitorCanvasRule.Plan([Uhd], VideoCodec.H264, All);
        Assert.Equal((VideoCodec.H264, 3840, 2160), (one.Codec, one.Width, one.Height));

        var twoQhd = MonitorCanvasRule.Plan([Qhd, Qhd with { X = 2560 }], VideoCodec.Hevc, All);
        Assert.Equal((5120, 1440), (twoQhd.Width, twoQhd.Height));

        Assert.Equal((4096, 768), MonitorCanvasRule.Fit(7680, 1440, MonitorCanvasRule.H264MaxSide));
    }
}
