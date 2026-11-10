using System.Globalization;
using System.Text.Json;
using Encore.Contracts.Messages;
using Encore.Platform.Windows;

namespace Encore.Engine.ClipMedia;

/// <summary>Reads a source's length, picture size, frame rate and audio tracks with ffprobe.</summary>
internal static class ClipProbe
{
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    public static async Task<ClipMetadata> ProbeAsync(ClipSource source, CancellationToken token)
    {
        var json = await ReadMetadataAsync(source, token).ConfigureAwait(false);
        return Parse(json);
    }

    private static async Task<byte[]> ReadMetadataAsync(ClipSource source, CancellationToken token)
    {
        using var text = new MemoryStream();
        await SharedMediaProcess.RunAsync(
            FFmpegArguments.Tool("ffprobe"),
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", "fd:"],
            source.Path,
            async (stream, cancellationToken) =>
            {
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (text.Length + count > MaxMetadataBytes)
                    {
                        throw new IOException("Media metadata is too large.");
                    }

                    text.Write(buffer, 0, count);
                }
            },
            token).ConfigureAwait(false);
        return text.ToArray();
    }

    private static ClipMetadata Parse(byte[] text)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var format = root.GetProperty("format");
        var duration = ReadDouble(format, "duration");
        var origin = ReadDouble(format, "start_time");
        var tracks = new List<ClipAudioTrack>();
        var width = 0;
        var height = 0;
        var frameRate = 0d;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            if (stream.GetProperty("codec_type").GetString() == "video" && width == 0)
            {
                width = stream.GetProperty("width").GetInt32();
                height = stream.GetProperty("height").GetInt32();
                frameRate = ReadFrameRate(stream) ?? frameRate;
            }

            if (stream.GetProperty("codec_type").GetString() == "audio")
            {
                tracks.Add(ReadAudioTrack(stream, tracks.Count));
            }
        }

        if (!double.IsFinite(duration) || duration <= 0 || width <= 0 || height <= 0)
        {
            throw new IOException("This source has no finished, seekable video timeline.");
        }

        return new(duration, width, height, frameRate, [.. tracks], origin);
    }

    /// <summary>The stream's average frame rate, or null when it isn't a readable fraction.</summary>
    private static double? ReadFrameRate(JsonElement stream)
    {
        var rate = stream.GetProperty("avg_frame_rate").GetString()?.Split('/');
        if (rate is [var numerator, var denominator]
            && double.TryParse(numerator, CultureInfo.InvariantCulture, out var frames)
            && double.TryParse(denominator, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return frames / seconds;
        }

        return null;
    }

    /// <summary>An audio stream as a track, named by its title, else its handler, else its place.</summary>
    private static ClipAudioTrack ReadAudioTrack(JsonElement stream, int tracksBefore)
    {
        var index = stream.GetProperty("index").GetInt32();
        string? name = null;
        if (stream.TryGetProperty("tags", out var tags))
        {
            if (tags.TryGetProperty("title", out var title))
            {
                name = title.GetString();
            }
            else if (tags.TryGetProperty("handler_name", out var handler))
            {
                name = handler.GetString();
            }
        }

        return new(
            index,
            string.IsNullOrWhiteSpace(name) ? $"Track {tracksBefore + 1}" : name,
            (int)ReadDouble(stream, "sample_rate"),
            (int)ReadDouble(stream, "channels"));
    }

    /// <summary>A number property, or 0 when it is missing, unreadable or not finite.</summary>
    private static double ReadDouble(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value)
        && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number)
            ? number
            : 0;
}
