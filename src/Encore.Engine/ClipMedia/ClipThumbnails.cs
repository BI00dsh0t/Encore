using Encore.Contracts.Messages;
using Encore.Platform.Windows;

namespace Encore.Engine.ClipMedia;

/// <summary>Writes up to 64 small, evenly spaced frames of a source for the clip creator's timeline.</summary>
internal static class ClipThumbnails
{
    private const int MaxFrames = 64;

    public static async Task<ClipThumbnailDescriptor> PrepareAsync(ClipMediaRequest request, ClipMetadata metadata, CancellationToken token)
    {
        var folder = CacheFolder.RequireEmpty(request.Destination, "thumbnail");
        var interval = Math.Max(1, metadata.Duration / MaxFrames);
        var filter = $"setpts=PTS-{FFmpegArguments.Number(metadata.Origin)}/TB,"
            + $"fps=fps=1/{FFmpegArguments.Number(interval)}:start_time=0:round=down,"
            + "scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2,setsar=1";
        await SharedMediaProcess.RunAsync(
            FFmpegArguments.Tool("ffmpeg"),
            ["-v", "error", "-nostdin", "-threads", "2", "-copyts", "-i", "fd:", "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", filter, "-filter_threads", "1", "-threads:v", "2", "-frames:v", "64", "-q:v", "5", "-f", "image2",
                Path.Combine(folder, "frame-%03d.jpg")],
            request.Source.Path,
            async (stream, cancellationToken) => await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false),
            token).ConfigureAwait(false);
        var frames = Directory.EnumerateFiles(folder, "frame-*.jpg")
            .Order(StringComparer.Ordinal)
            .Take(MaxFrames)
            .Select((file, index) => new ClipThumbnail(Math.Min(metadata.Duration, index * interval), Path.GetFileName(file)))
            .ToArray();
        if (frames.Length == 0)
        {
            throw new IOException("No video thumbnails could be prepared.");
        }

        return new(1, request.Source, metadata.Duration, frames);
    }
}
