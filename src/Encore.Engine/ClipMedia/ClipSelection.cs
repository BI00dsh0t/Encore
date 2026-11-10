using System.Globalization;
using Encore.Contracts.Messages;
using Encore.Platform.Windows;

namespace Encore.Engine.ClipMedia;

/// <summary>Checks what a request chose: its audio tracks, and its range snapped to video frames or audio samples.</summary>
internal static class ClipSelection
{
    private const int SampleRate = 48000;

    /// <summary>The chosen tracks in the source's order; refused when one is repeated or gone.</summary>
    public static ClipAudioTrack[] Tracks(ClipMediaRequest request, ClipMetadata metadata)
    {
        var selected = request.Tracks ?? [];
        var repeated = selected.Length != selected.Distinct().Count();
        if (repeated || selected.Any(index => !metadata.Tracks.Any(track => track.StreamIndex == index)))
        {
            throw new IOException("The chosen audio tracks are no longer available.");
        }

        return metadata.Tracks.Where(track => selected.Contains(track.StreamIndex)).ToArray();
    }

    public static void CheckRange(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > duration + 0.000001)
        {
            throw new IOException("Choose a non-empty range within the source.");
        }
    }

    /// <summary>The range as it will be cut: from the first frame at or after each end, or on whole samples for MP3.</summary>
    public static async Task<ClipRange> RangeAsync(ClipMediaRequest request, ClipMetadata metadata, CancellationToken token)
    {
        CheckRange(request.Start, request.End, metadata.Duration);
        if (request.Format == "mp3")
        {
            return SampleRange(request);
        }

        var start = await FrameAtOrAfterAsync(request, metadata, request.Start, token).ConfigureAwait(false)
            ?? throw new IOException("No video frame exists in this selection.");
        var end = metadata.Duration - request.End < 0.000001
            ? metadata.Duration
            : await FrameAtOrAfterAsync(request, metadata, request.End, token).ConfigureAwait(false) ?? metadata.Duration;
        if (end <= start)
        {
            throw new IOException("Choose a range containing at least one video frame.");
        }

        return new(start, end);
    }

    // Audio cuts don't follow the video's frame cadence; each stream keeps its timing from this common 48 kHz origin.
    private static ClipRange SampleRange(ClipMediaRequest request)
    {
        var startSample = Math.Ceiling((request.Start * SampleRate) - 0.0000001);
        var endSample = Math.Ceiling((request.End * SampleRate) - 0.0000001);
        if (endSample <= startSample)
        {
            throw new IOException("Choose a range containing at least one audio sample.");
        }

        return new(startSample / SampleRate, endSample / SampleRate, "audioSamples48000");
    }

    /// <summary>The time of the first decoded video frame at or after target, or null when there is none.</summary>
    private static async Task<double?> FrameAtOrAfterAsync(
        ClipMediaRequest request,
        ClipMetadata metadata,
        double target,
        CancellationToken token)
    {
        double? found = null;
        var interval = FFmpegArguments.Number(metadata.Origin + target)
            + "%" + FFmpegArguments.Number(metadata.Origin + Math.Min(metadata.Duration, target + 5));

        // ffprobe seeks to a preceding keyframe, then emits actual decoded frame timestamps.
        await SharedMediaProcess.RunAsync(
            FFmpegArguments.Tool("ffprobe"),
            ["-v", "error", "-select_streams", "v:0", "-read_intervals", interval,
                "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", "fd:"],
            request.Source.Path,
            async (stream, cancellationToken) =>
            {
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (!double.TryParse(line.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp))
                    {
                        continue;
                    }

                    var relative = timestamp - metadata.Origin;
                    if (relative + 0.0000001 >= target && (found is null || relative < found))
                    {
                        found = relative;
                    }
                }
            },
            token).ConfigureAwait(false);
        return found;
    }
}
