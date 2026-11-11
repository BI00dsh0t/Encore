using Encore.Contracts.Messages;
using Encore.Platform.Windows;
using Encore.Waveforms;

namespace Encore.Engine.ClipMedia;

/// <summary>Builds the peak cache of every audio track, and the waveform bins of a range.</summary>
internal static class ClipWaveforms
{
    /// <summary>Each coarser level holds one peak for this many of the level below.</summary>
    private const int PeaksPerCoarserPeak = 16;

    /// <summary>Writes each track's peak files: one peak per millisecond, then coarser levels down to one peak.</summary>
    public static async Task<ClipWaveformDescriptor> PrepareAsync(ClipMediaRequest request, ClipMetadata metadata, CancellationToken token)
    {
        var folder = CacheFolder.RequireEmpty(request.Destination, "waveform");
        var tracks = new List<ClipPeakTrack>();
        foreach (var track in metadata.Tracks)
        {
            token.ThrowIfCancellationRequested();
            var count = checked((long)Math.Ceiling(metadata.Duration * 1000));
            var levels = new List<ClipPeakLevel>
            {
                await WriteFinestLevelAsync(request, metadata, track, folder, count, token).ConfigureAwait(false),
            };
            while (levels[^1].Count > 1)
            {
                token.ThrowIfCancellationRequested();
                levels.Add(WriteCoarserLevel(folder, track.StreamIndex, levels[^1], levels.Count, token));
            }

            tracks.Add(new(track.StreamIndex, [.. levels]));
        }

        return new(ClipPeakFile.Version, request.Source, metadata.Duration, [.. tracks]);
    }

    /// <summary>The loudest frame in each of the range's bins, for one track.</summary>
    public static async Task<float[]> BinsAsync(
        ClipMediaRequest request,
        ClipMetadata metadata,
        ClipAudioTrack track,
        CancellationToken token)
    {
        var bins = new float[Math.Clamp(request.Bins, 32, 4096)];
        var duration = request.End - request.Start;
        var filter = FFmpegArguments.AudioTrim(track.StreamIndex, new(request.Start, request.End), metadata.Origin) + "[a]";
        var seek = FFmpegArguments.Number(metadata.Origin + Math.Max(0, request.Start - 1));
        long sample = 0;
        void Add(float peak)
        {
            var bin = (int)Math.Min(bins.Length - 1, sample * bins.Length / (48000 * duration));
            if (float.IsFinite(peak))
            {
                bins[bin] = Math.Max(bins[bin], Math.Min(1, peak));
            }

            sample++;
        }

        await SharedMediaProcess.RunAsync(
            FFmpegArguments.Tool("ffmpeg"),
            ["-v", "error", "-nostdin", "-threads", "2", "-copyts", "-seek_timestamp", "1", "-ss", seek, "-i", "fd:",
                "-filter_complex_threads", "1", "-filter_complex", filter,
                "-map", "[a]", "-ac", "2", "-ar", "48000", "-t", FFmpegArguments.Number(duration), "-f", "f32le", "pipe:1"],
            request.Source.Path,
            async (stream, cancellationToken) => await StereoPeaks.ReadAsync(stream, Add, cancellationToken).ConfigureAwait(false),
            token).ConfigureAwait(false);
        return bins;
    }

    private static async Task<ClipPeakLevel> WriteFinestLevelAsync(
        ClipMediaRequest request,
        ClipMetadata metadata,
        ClipAudioTrack track,
        string folder,
        long count,
        CancellationToken token)
    {
        var name = $"track-{track.StreamIndex}-0.rbpk";
        using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, name))))
        {
            ClipPeakFile.WriteHeader(writer, track.StreamIndex, 1, count);
            var peaks = new MillisecondPeakWriter(writer, count);
            var filter = FFmpegArguments.AudioTrim(track.StreamIndex, new(0, metadata.Duration), metadata.Origin) + "[a]";
            await SharedMediaProcess.RunAsync(
                FFmpegArguments.Tool("ffmpeg"),
                ["-v", "error", "-nostdin", "-threads", "2", "-copyts", "-i", "fd:",
                    "-filter_complex_threads", "1", "-filter_complex", filter,
                    "-map", "[a]", "-ac", "2", "-ar", "48000", "-t", FFmpegArguments.Number(metadata.Duration), "-f", "f32le", "pipe:1"],
                request.Source.Path,
                async (stream, cancellationToken) =>
                {
                    if (await StereoPeaks.ReadAsync(stream, peaks.Add, cancellationToken).ConfigureAwait(false) != 0)
                    {
                        throw new IOException("The waveform decoder returned incomplete audio frames.");
                    }
                },
                token).ConfigureAwait(false);
            peaks.Finish(token);
        }

        return new(name, 1, count);
    }

    /// <summary>Writes the level above previous: each peak is the largest of the next sixteen below it.</summary>
    private static ClipPeakLevel WriteCoarserLevel(
        string folder,
        int streamIndex,
        ClipPeakLevel previous,
        int level,
        CancellationToken token)
    {
        var count = (previous.Count + PeaksPerCoarserPeak - 1) / PeaksPerCoarserPeak;
        var stride = checked(previous.MillisecondsPerPeak * PeaksPerCoarserPeak);
        var name = $"track-{streamIndex}-{level}.rbpk";
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(folder, previous.FileName)));
        ClipPeakFile.Validate(reader, streamIndex, previous.MillisecondsPerPeak, previous.Count);
        using var writer = new BinaryWriter(File.Create(Path.Combine(folder, name)));
        ClipPeakFile.WriteHeader(writer, streamIndex, stride, count);
        for (long first = 0; first < previous.Count; first += PeaksPerCoarserPeak)
        {
            token.ThrowIfCancellationRequested();
            var peak = 0f;
            for (var i = 0; i < Math.Min(PeaksPerCoarserPeak, previous.Count - first); i++)
            {
                peak = Math.Max(peak, ClipPeakFile.ReadPeak(reader));
            }

            writer.Write(peak);
        }

        return new(name, stride, count);
    }
}
