using Encore.Media;
using Encore.Platform.Audio;
using Encore.Platform.FFmpeg;
using SessionFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Sessions;

/// <summary>Builds the stats report of a finished run from its recorders and its flushed encoder chain.</summary>
internal static class SessionStatsReport
{
    /// <summary>The report, or null when the chain never opened every encoder and timeline.</summary>
    public static CaptureStatsReport? For(CaptureStatsCollector stats, EncoderChain chain, CaptureSessionOptions options, SessionRun run)
    {
        var encoder = chain.Video;
        if (encoder is null
            || chain.AudioEncoders.Count != chain.Spec.Audio.Tracks.Count || chain.AudioTimelines.Count != chain.AudioTracks.Count)
        {
            return null;
        }

        var video = Video(stats.Cadence, chain, encoder);
        var audio = Audio(chain);
        return new CaptureStatsReport(
            CaptureStatsReport.CurrentSchema,
            DateTimeOffset.Now,
            run.Path,
            run.WindowTitle,
            run.ProcessName,
            run.Adapter,
            options.RequestedSeconds,
            run.StopReason,
            run.Seconds,
            stats.GpuPriority,
            new EncoderReport(
                options.Stages.ToString().ToLowerInvariant(),
                options.NvencPreset,
                options.NvencTune,
                options.Color == ColorPath.X2 ? "x2" : "x1",
                chain.Paced is not null),
            video,
            CaptureStatsReports.Latency(stats.Latency, encoder.FlushedPackets),
            stats.Resources(),
            audio);
    }

    /// <summary>The video counts: what arrived from capture, what the pacer did with it, and what was encoded.</summary>
    private static VideoReport Video(FrameCadence cadence, EncoderChain chain, VideoEncoder encoder)
    {
        var arrived = cadence.Frames;
        var stepped = arrived - cadence.NonIncreasing; // frames that advanced the capture clock
        var span = cadence.SpanSeconds;
        var slots = stepped + cadence.MissedSlots;
        var paced = chain.Paced;
        var pacer = paced?.Pacer;
        var emitted = pacer?.EmittedSlots ?? 0;
        var repeated = pacer?.RepeatedSlots ?? 0;
        return new VideoReport(
            chain.OutputSize.Width,
            chain.OutputSize.Height,
            arrived,
            encoder.EncodedFrames,
            repeated,
            (paced?.QueueDrops ?? 0) + encoder.PoolMisses,
            cadence.NonIncreasing,
            cadence.MissedSlots,
            cadence.Gaps,
            cadence.MaxStepMilliseconds,
            span > 0 ? (stepped - 1) / span : 0,
            emitted > 0 ? repeated * 100.0 / emitted : 0,
            slots > 0 ? cadence.MissedSlots * 100.0 / slots : 0,
            pacer?.FreshSlots ?? 0,
            pacer?.SkippedSlots ?? 0,
            pacer?.HeldReplaced ?? 0);
    }

    /// <summary>Every device, with the hub's readouts and the samples encoded by the first Track that holds it.</summary>
    private static List<AudioReport> Audio(EncoderChain chain)
    {
        var audio = new List<AudioReport>();
        for (var i = 0; i < chain.AudioTracks.Count; i++)
        {
            var track = chain.AudioTracks[i];
            if (track.Subscription is not AudioDeviceSubscription { Diagnostics: var diagnostics })
            {
                continue;
            }

            var role = track.Device.Flow == SessionFlow.Output ? "output" : "input";
            var first = chain.Spec.Audio.FirstTrackOf(i);
            var samples = first >= 0 ? chain.AudioEncoders[first].SamplesWritten : 0;
            audio.Add(CaptureStatsReports.Audio(
                role, track.Device.FriendlyName, diagnostics.Stamps, diagnostics.Stream, chain.AudioTimelines[i], samples));
        }

        return audio;
    }
}
