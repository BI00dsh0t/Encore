using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Encore.Bench;

/// <summary>One stream of a Clip as ffprobe reports it: its type, its Track name (the <c>name</c> tag), length and frames.</summary>
internal sealed record ClipStream(string CodecType, string? Name, double? DurationSeconds, long? Frames);

/// <summary>A Clip's streams, and what decoding it to the end wrote on stderr (null when it decoded cleanly).</summary>
internal sealed record ClipProbe(IReadOnlyList<ClipStream> Streams, string? DecodeError);

/// <summary>One Clip's verdict: its video length, frames against length × fps (information only), and why it failed.</summary>
internal sealed record SoakClipResult(
    double? VideoSeconds,
    IReadOnlyList<double?> AudioSeconds,
    IReadOnlyList<string> Tracks,
    long? Frames,
    double? ExpectedFrames,
    bool Pass,
    IReadOnlyList<string> Failures);

/// <summary><c>bench soak</c>'s check of each Clip: its Tracks by name, its length, and that the whole file decodes.</summary>
internal static class SoakClipCheck
{
    public const double GopSeconds = 1;

    /// <summary>One AAC packet, 1024 samples at 48 kHz: a Track's length steps by this, so it trails the video by up to one.</summary>
    public const double AudioPacketSeconds = 1024.0 / 48_000;

    public static SoakClipResult Check(FfmpegTools tools, string path, IReadOnlyList<string> expectedTitles, int bufferSeconds, double fps) =>
        Evaluate(Probe(tools, path), expectedTitles, bufferSeconds, fps);

    public static ClipProbe Probe(FfmpegTools tools, string path)
    {
        var json = MediaAnalysis.Run(tools.Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,duration,nb_frames:stream_tags=name", "-of", "json", path]);
        using var document = JsonDocument.Parse(json);
        var streams = new List<ClipStream>();
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
            var name = stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var n) ? n.GetString() : null;
            double? duration = stream.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
            long? frames = stream.TryGetProperty("nb_frames", out var f)
                && long.TryParse(f.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
            streams.Add(new ClipStream(type, name, duration, frames));
        }

        return new ClipProbe(streams, Decode(tools, path));
    }

    public static SoakClipResult Evaluate(ClipProbe probe, IReadOnlyList<string> expectedTitles, int bufferSeconds, double fps)
    {
        var failures = new List<string>();
        var frame = 1 / fps;
        var audioSlack = Math.Max(frame, AudioPacketSeconds);
        var video = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
        var audio = probe.Streams.Where(s => s.CodecType == "audio").ToList();
        var tracks = audio.Select(a => a.Name ?? "(unnamed)").ToList();

        var missing = expectedTitles.Where(t => !tracks.Contains(t, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            failures.Add($"missing Track(s) {string.Join(", ", missing)} (has {Join(tracks)})");
        }
        else if (!tracks.Where(expectedTitles.Contains).SequenceEqual(expectedTitles, StringComparer.Ordinal))
        {
            failures.Add($"Tracks out of order: {Join(tracks)}, expected {Join(expectedTitles)}");
        }

        double? videoSeconds = video?.DurationSeconds;
        if (video is null)
        {
            failures.Add("no video stream");
        }
        else if (videoSeconds is not { } length)
        {
            failures.Add("the video has no duration");
        }
        else
        {
            var min = bufferSeconds - GopSeconds - frame;
            var max = bufferSeconds + frame;
            if (length < min || length > max)
            {
                failures.Add(Text($"video {length:0.###} s outside [{min:0.###}, {max:0.###}] s for a {bufferSeconds} s Buffer Window"));
            }

            foreach (var (track, i) in audio.Select((a, i) => (a, i)))
            {
                if (track.DurationSeconds is not { } audioLength)
                {
                    failures.Add($"Track {tracks[i]} has no duration");
                }
                else if (Math.Abs(audioLength - length) > audioSlack)
                {
                    failures.Add(Text($"Track {tracks[i]} {audioLength:0.###} s is {Math.Abs(audioLength - length) * 1000:0.#} ms off the video (> {audioSlack * 1000:0.#} ms)"));
                }
            }
        }

        if (probe.DecodeError is { } error)
        {
            failures.Add("decode: " + error);
        }

        return new SoakClipResult(
            videoSeconds,
            [.. audio.Select(a => a.DurationSeconds)],
            tracks,
            video?.Frames,
            videoSeconds is { } s ? Math.Round(s * fps, 1) : null,
            failures.Count == 0,
            failures);
    }

    /// <summary>Decodes every stream to the end; null when ffmpeg exits 0 with nothing on stderr, else what went wrong.</summary>
    private static string? Decode(FfmpegTools tools, string path)
    {
        var start = new ProcessStartInfo(tools.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-i", path, "-map", "0", "-f", "null", "-" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new BenchFailure($"Could not start {tools.Ffmpeg}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        _ = stdout.GetAwaiter().GetResult();
        if (process.ExitCode == 0 && stderr.Length == 0)
        {
            return null;
        }

        var detail = stderr.Length > 0 ? ": " + Truncate(stderr) : "";
        return $"ffmpeg exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}{detail}";
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
