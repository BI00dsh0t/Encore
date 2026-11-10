using System.Globalization;
using Encore.Contracts.Messages;
using Encore.Platform.Windows;

namespace Encore.Engine.ClipMedia;

/// <summary>Renders a range to MP4 or MP3 in a scratch folder beside the destination, then moves it into place.</summary>
internal static class ClipExport
{
    private const string PartialName = "output.partial";

    /// <summary>Writes the export and returns its path; the move into place is the commit point.</summary>
    public static async Task<string> ExportAsync(
        ClipMediaRequest request,
        ClipMetadata metadata,
        ClipRange range,
        IProgress<double>? progress,
        CancellationToken token)
    {
        var tracks = ClipSelection.Tracks(Checked(request), metadata);
        if (request.Format == "mp3" && tracks.Length == 0)
        {
            throw new IOException("MP3 export needs at least one audio track.");
        }

        var destination = Path.GetFullPath(request.Destination!);
        var scratch = Path.Combine(FreeParent(destination), ".rb-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var separateFiles = request.Format == "mp3" && !request.Mix;
        try
        {
            await RenderAsync(new Job(request, metadata, range, progress), tracks, scratch, separateFiles, token).ConfigureAwait(false);
            ClipMediaWorker.ValidateSource(request.Source);
            token.ThrowIfCancellationRequested();
            if (separateFiles)
            {
                Directory.Move(scratch, destination);
            }
            else
            {
                File.Move(Path.Combine(scratch, PartialName), destination, false);
            }

            return destination;
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private static ClipMediaRequest Checked(ClipMediaRequest request) =>
        request.Format is not ("mp4" or "mp3") || string.IsNullOrWhiteSpace(request.Destination)
            ? throw new IOException("Choose an MP4 or MP3 destination.")
            : request;

    /// <summary>The destination's folder; refused when the destination exists or its folder doesn't.</summary>
    private static string FreeParent(string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("That destination already exists. Choose a new name.");
        }

        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose a destination folder.");
        return Directory.Exists(parent) ? parent : throw new IOException("The destination folder no longer exists.");
    }

    // Cleanup cannot convert an already committed export into failure.
    private static void DeleteScratch(string scratch)
    {
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One file with every track, or for an unmixed MP3 one numbered file per track.</summary>
    private static async Task RenderAsync(Job job, ClipAudioTrack[] tracks, string scratch, bool separateFiles, CancellationToken token)
    {
        if (!separateFiles)
        {
            await EncodeAsync(job, tracks, Path.Combine(scratch, PartialName), token).ConfigureAwait(false);
            return;
        }

        for (var i = 0; i < tracks.Length; i++)
        {
            var file = Path.Combine(scratch, $"{i + 1:00} {TrackFileName(tracks[i])}.mp3");
            await EncodeAsync(job, [tracks[i]], file, token).ConfigureAwait(false);
        }
    }

    private static string TrackFileName(ClipAudioTrack track)
    {
        var name = string.Concat(track.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length > 80)
        {
            name = name[..80];
        }

        return name.TrimEnd('.', ' ');
    }

    private static async Task EncodeAsync(Job job, ClipAudioTrack[] tracks, string output, CancellationToken token)
    {
        var length = job.Range.End - job.Range.Start;
        await SharedMediaProcess.RunAsync(
            FFmpegArguments.Tool("ffmpeg"),
            Arguments(job, tracks, output),
            job.Request.Source.Path,
            (stream, cancellationToken) => ReadProgressAsync(stream, length, job.Progress, cancellationToken),
            token).ConfigureAwait(false);
    }

    private static List<string> Arguments(Job job, ClipAudioTrack[] tracks, string output)
    {
        var (request, metadata, range) = (job.Request, job.Metadata, job.Range);
        var arguments = new List<string>
        {
            "-v", "error", "-nostdin", "-n", "-threads", "2", "-copyts", "-seek_timestamp", "1",
            "-ss", FFmpegArguments.Number(metadata.Origin + Math.Max(0, range.Start - 1)), "-i", "fd:", "-filter_complex_threads", "1",
        };
        var filters = Filters(job, tracks);
        if (filters.Count > 0)
        {
            arguments.AddRange(["-filter_complex", string.Join(';', filters)]);
        }

        if (request.Format == "mp4")
        {
            arguments.AddRange(["-map", "[v]", "-c:v", "libx264", "-threads:v", "2", "-preset", "fast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-fps_mode", "passthrough"]);
        }

        if (tracks.Length > 0)
        {
            AddAudioOutputs(arguments, request, tracks);
        }

        if (request.Format == "mp4")
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.AddRange(["-t", FFmpegArguments.Number(range.End - range.Start), "-progress", "pipe:1", "-nostats",
            "-f", request.Format, output]);
        return arguments;
    }

    /// <summary>The picture cut for MP4, each track's cut, and the mix of them when asked for.</summary>
    private static List<string> Filters(Job job, ClipAudioTrack[] tracks)
    {
        var (request, origin, range) = (job.Request, job.Metadata.Origin, job.Range);
        var start = FFmpegArguments.Number(origin + range.Start);
        var filters = new List<string>();
        if (request.Format == "mp4")
        {
            filters.Add($"[0:v:0]trim=start={start}:end={FFmpegArguments.Number(origin + range.End)},"
                + $"setpts=PTS-{start}/TB,pad=ceil(iw/2)*2:ceil(ih/2)*2[v]");
        }

        for (var i = 0; i < tracks.Length; i++)
        {
            filters.Add(FFmpegArguments.AudioTrim(tracks[i].StreamIndex, range, origin) + $"[a{i}]");
        }

        if (request.Mix && tracks.Length > 0)
        {
            filters.Add(string.Concat(Enumerable.Range(0, tracks.Length).Select(i => $"[a{i}]"))
                + $"amix=inputs={tracks.Length}:normalize=1:duration=longest,alimiter=limit=0.95:level=0:latency=1[mix]");
        }

        return filters;
    }

    private static void AddAudioOutputs(List<string> arguments, ClipMediaRequest request, ClipAudioTrack[] tracks)
    {
        if (request.Mix)
        {
            arguments.AddRange(["-map", "[mix]", "-metadata:s:a:0", "title=Combined mix", "-metadata:s:a:0", "handler_name=Combined mix"]);
        }
        else
        {
            for (var i = 0; i < tracks.Length; i++)
            {
                arguments.AddRange(["-map", $"[a{i}]", $"-metadata:s:a:{i}", "title=" + tracks[i].Name,
                    $"-metadata:s:a:{i}", "handler_name=" + tracks[i].Name]);
            }
        }

        arguments.AddRange(["-c:a", request.Format == "mp4" ? "aac" : "libmp3lame", "-threads:a", "2", "-ar", "48000", "-ac", "2",
            "-b:a", "192k"]);
    }

    /// <summary>Reports FFmpeg's progress lines as a fraction of the range, in steps of at least 1 %.</summary>
    private static async Task ReadProgressAsync(Stream stream, double length, IProgress<double>? progress, CancellationToken token)
    {
        using var reader = new StreamReader(stream);
        var last = -1d;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)
                || !double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds))
            {
                continue;
            }

            var fraction = Math.Clamp(microseconds / 1_000_000 / length, 0, 1);
            if (fraction - last >= 0.01)
            {
                last = fraction;
                progress?.Report(fraction);
            }
        }
    }

    private sealed record Job(ClipMediaRequest Request, ClipMetadata Metadata, ClipRange Range, IProgress<double>? Progress);
}
