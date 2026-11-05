using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.Capture;

namespace ReplayBuffer.Engine.Tests;

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
