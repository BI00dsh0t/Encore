using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Monitors;
using Encore.Core.Sessions;
using Encore.Platform.Capture;
using VideoCodec = Encore.Contracts.Settings.VideoCodec;

namespace Encore.Engine.Sessions;

/// <summary>Plans a Monitor session's canvas: its codec and size replace the snapshot's video. Rules: docs/code-notes.md.</summary>
internal static class CanvasPlanner
{
    /// <summary>The spec to encode with for screens already placed, and the placements (null for one screen).</summary>
    public static (SessionSpec Spec, IReadOnlyList<MonitorPlacement>? Composite) Plan(
        SessionSpec spec,
        IReadOnlyList<DisplayMonitor> screens,
        IReadOnlyList<MonitorPlacement> placements,
        Func<VideoCodec, bool> canEncode,
        Action<string, string>? warn,
        ILogger log)
    {
        if (placements.Count == 0)
        {
            throw new EngineFailure(ErrorCodes.MonitorRecordingNoScreens, "None of the screens chosen to record is connected.");
        }

        if (placements.Count == 1)
        {
            return (PlanOne(spec, screens, placements[0], canEncode, warn, log), null);
        }

        var placed = placements
            .Select(p => new DisplayMonitor(
                p.Monitor.DeviceName, p.Monitor.DeviceName, p.Width, p.Height, p.Monitor.IsPrimary, null, p.X, p.Y, p.RefreshHz))
            .ToList();
        var plan = MonitorCanvasRule.Plan(placed, spec.Snapshot.Video.Codec, canEncode);
        log.LogInformation("Canvas {Native}x{NativeHeight} encoded {Codec} {Width}x{Height} (Output codec {Output})",
            plan.NativeWidth, plan.NativeHeight, plan.Codec, plan.Width, plan.Height, spec.Snapshot.Video.Codec);
        WarnWhenScaled(plan, warn);
        return (WithVideoOf(spec, plan), placements);
    }

    /// <summary>The spec for the one screen still attached: at its physical size when the snapshot matches the source.</summary>
    private static SessionSpec PlanOne(
        SessionSpec spec,
        IReadOnlyList<DisplayMonitor> screens,
        MonitorPlacement placement,
        Func<VideoCodec, bool> canEncode,
        Action<string, string>? warn,
        ILogger log)
    {
        var only = screens.First(s => string.Equals(s.DeviceName, placement.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (!spec.Snapshot.Video.MatchSource)
        {
            return spec with { Screens = [only] };
        }

        // One screen matching its source records at its physical size, with HEVC or AV1 when over H.264's 4096 px.
        var screen = new DisplayMonitor(
            only.DeviceName, only.DeviceName, placement.Width, placement.Height, only.Primary, null, placement.X, placement.Y,
            placement.RefreshHz);
        var single = MonitorCanvasRule.Plan([screen], spec.Snapshot.Video.Codec, canEncode);
        log.LogInformation("One screen at source size: {Native}x{NativeHeight} encoded {Codec} {Width}x{Height}",
            single.NativeWidth, single.NativeHeight, single.Codec, single.Width, single.Height);
        WarnWhenScaled(single, warn);
        return WithVideoOf(spec, single) with { Screens = [only] };
    }

    /// <summary>Raises <c>monitor-recording-scaled</c> when the canvas had to stay H.264 and was fitted to its limit.</summary>
    private static void WarnWhenScaled(CanvasPlan plan, Action<string, string>? warn)
    {
        if (plan.Codec != VideoCodec.H264 || !plan.Scaled)
        {
            return;
        }

        warn?.Invoke(
            ErrorCodes.MonitorRecordingScaled,
            $"No HEVC or AV1 encoder works on this PC, so the {plan.NativeWidth} × {plan.NativeHeight} Monitor Recording "
            + $"is scaled down to {plan.Width} × {plan.Height} for H.264.");
    }

    /// <summary>The spec with the plan's codec and size in place of the snapshot's.</summary>
    private static SessionSpec WithVideoOf(SessionSpec spec, CanvasPlan plan)
    {
        var video = spec.Snapshot.Video with { Codec = plan.Codec, Width = plan.Width, Height = plan.Height };
        return spec with { Snapshot = spec.Snapshot with { Video = video } };
    }
}
