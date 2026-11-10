using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Engine.Sessions;
using Encore.Platform.Capture;

namespace Encore.Engine.Tests;

/// <summary>Story 11.3: the Monitor session's canvas plan, with fake placements and a fake encoder probe (no displays, no GPU).</summary>
public sealed class CanvasPlanTests
{
    private static readonly DisplayMonitor[] Screens =
    [
        new(@"\\.\DISPLAY1", "Odyssey", 2560, 1440, true, "path-1", 0, 0, 240),
        new(@"\\.\DISPLAY2", "U32", 3840, 2160, false, "path-2", 2560, 0, 144),
    ];

    private static readonly MonitorPlacement[] Placed =
    [
        new(new MonitorInfo(1, @"\\.\DISPLAY1", true), 0, 0, 2560, 1440, 240),
        new(new MonitorInfo(2, @"\\.\DISPLAY2", false), 2560, 0, 3840, 2160, 144),
    ];

    [Fact]
    public void UnderMatchSourceOneScreenRecordsAtItsModeSizeSwitchingCodecOnlyOver4096()
    {
        // Story 12.2: a fake 3840x2160 mode stays H.264 (it fits 4096); a fake 5120x1440 one takes HEVC at full size.
        var uhd = new DisplayMonitor(@"\\.\DISPLAY2", "U32", 0, 0, false);
        var wide = new DisplayMonitor(@"\\.\DISPLAY3", "Wide", 0, 0, false);
        MonitorPlacement[] uhdPlaced = [new(new MonitorInfo(2, uhd.DeviceName, false), 2560, 0, 3840, 2160, 144)];
        MonitorPlacement[] widePlaced = [new(new MonitorInfo(3, wide.DeviceName, false), 0, 0, 5120, 1440, 120)];

        var (onUhd, composite) = CaptureSession.PlanCanvas(OneScreenSpec(uhd, matchSource: true), [uhd], uhdPlaced, _ => true, null, NullLogger.Instance);
        var (onWide, _) = CaptureSession.PlanCanvas(OneScreenSpec(wide, matchSource: true), [wide], widePlaced, _ => true, null, NullLogger.Instance);
        var (off, _) = CaptureSession.PlanCanvas(OneScreenSpec(uhd, matchSource: false), [uhd], uhdPlaced, _ => true, null, NullLogger.Instance);

        Assert.Null(composite);
        Assert.Equal((VideoCodec.H264, 3840, 2160), (onUhd.Snapshot.Video.Codec, onUhd.Snapshot.Video.Width, onUhd.Snapshot.Video.Height));
        Assert.Equal((VideoCodec.Hevc, 5120, 1440), (onWide.Snapshot.Video.Codec, onWide.Snapshot.Video.Width, onWide.Snapshot.Video.Height));
        Assert.Equal((VideoCodec.H264, 1920, 1080), (off.Snapshot.Video.Codec, off.Snapshot.Video.Width, off.Snapshot.Video.Height));
    }

    private static SessionSpec OneScreenSpec(DisplayMonitor screen, bool matchSource)
    {
        var video = new VideoSettings(VideoCodec.H264, 1920, 1080, 60, 20_000, RateControl.Cbr, matchSource);
        var snapshot = Core.Settings.SettingsResolver.Resolve(SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) }, null);
        return new SessionSpec(SessionKind.Monitor, new CaptureTarget(0, 0, null), snapshot, SessionAudio.None) { Screens = [screen] };
    }

    private static SessionSpec MonitorSpec => new(
        SessionKind.Monitor, new CaptureTarget(0, 0, null), Core.Settings.SettingsResolver.Resolve(SettingsDocument.Empty, null), SessionAudio.None) { Screens = Screens };

    [Fact]
    public void ACanvasOver4096OnH264OutputIsEncodedWithHevcAtFullSize()
    {
        var warnings = new List<string>();

        var (spec, composite) = CaptureSession.PlanCanvas(MonitorSpec, Screens, Placed, _ => true, (code, _) => warnings.Add(code), NullLogger.Instance);

        Assert.Equal((VideoCodec.Hevc, 6400, 2160), (spec.Snapshot.Video.Codec, spec.Snapshot.Video.Width, spec.Snapshot.Video.Height));
        Assert.Equal(2, composite!.Count);
        Assert.Empty(warnings);
    }

    [Fact]
    public void WithNoHevcOrAv1EncoderTheCanvasStaysH264FittedTo4096WithOneWarning()
    {
        var warnings = new List<string>();

        var (spec, composite) = CaptureSession.PlanCanvas(MonitorSpec, Screens, Placed, c => c == VideoCodec.H264, (code, _) => warnings.Add(code), NullLogger.Instance);

        Assert.Equal((VideoCodec.H264, 4096, 1382), (spec.Snapshot.Video.Codec, spec.Snapshot.Video.Width, spec.Snapshot.Video.Height));
        Assert.NotNull(composite);
        Assert.Equal([ErrorCodes.MonitorRecordingScaled], warnings);
    }
}
