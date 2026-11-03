using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ReplayBuffer.Bench;

/// <summary>One stream of the file, from ffprobe; its span is from its packets' timestamps.</summary>
internal sealed record StreamSpan(int Index, string CodecType, string? Title, double StartSeconds, double EndSeconds, long Packets)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>What the drift analysis reads out of a captured file.</summary>
/// <param name="FrameTimes">Each video frame's presentation time, s, in order.</param>
/// <param name="FlashTimes">The frame time of each flash's first frame, s.</param>
/// <param name="OnsetTimes">Each burst onset's time in the burst track, s.</param>
internal sealed record MediaScan(
    double Fps,
    IReadOnlyList<StreamSpan> Streams,
    int VideoFrames,
    IReadOnlyList<double> FlashTimes,
    int BurstTrack,
    long BurstTrackSamples,
    IReadOnlyList<double> OnsetTimes)
{
    public StreamSpan Video => Streams.First(s => s.CodecType == "video");

    public IEnumerable<StreamSpan> AudioStreams => Streams.Where(s => s.CodecType == "audio");

    public StreamSpan BurstStream => AudioStreams.ElementAt(BurstTrack);
}

/// <summary>
/// Decodes a captured file with ffmpeg and ffprobe: the video to 64×36 gray frames (one mean luma each),
/// the burst track to 48 kHz stereo f32 (streamed through the onset detector), and the packet timestamps
/// of every stream. Frame i's time is its own presentation timestamp (i / fps from the start for CFR).
/// </summary>
internal static class MediaAnalysis
{
    private const int GrayWidth = 64;
    private const int GrayHeight = 36;

    public static MediaScan Scan(FfmpegTools tools, string file, int burstTrack, double burstDbfs, CancellationToken cancellation)
    {
        if (!File.Exists(file))
        {
            throw new BenchFailure($"No file to analyse at '{file}'.");
        }

        var (streams, fps) = Probe(tools, file);
        if (!streams.Any(s => s.CodecType == "video"))
        {
            throw new BenchFailure($"'{file}' has no video stream.");
        }

        var audio = streams.Where(s => s.CodecType == "audio").ToList();
        if (burstTrack < 0 || burstTrack >= audio.Count)
        {
            throw new BenchFailure($"'{file}' has {audio.Count} audio track(s); --burst-track {burstTrack} is not one of them.");
        }

        var packetTimes = PacketTimes(tools, file);
        var video = streams.First(s => s.CodecType == "video");
        var spans = streams.Select(s => Span(s, packetTimes.GetValueOrDefault(s.Index))).ToList();

        var videoTask = Task.Run(() => DecodeMeans(tools, file, cancellation), cancellation);
        var audioTask = Task.Run(() => DecodeOnsets(tools, file, burstTrack, burstDbfs, cancellation), cancellation);
        var means = videoTask.GetAwaiter().GetResult();
        var (onsets, samples) = audioTask.GetAwaiter().GetResult();

        // Frame i is the i-th presentation timestamp; without timestamps for every frame, i / fps from the start.
        var videoSpan = spans.First(s => s.Index == video.Index);
        var pts = packetTimes.TryGetValue(video.Index, out var v) ? v.Select(p => p.Pts).Order().ToArray() : [];
        double FrameTime(int i) => pts.Length == means.Count ? pts[i] : videoSpan.StartSeconds + (i / fps);

        var flashes = FlashDetector.Detect(means).Select(FrameTime).ToList();
        var burstSpan = spans.Where(s => s.CodecType == "audio").ElementAt(burstTrack);
        var onsetTimes = onsets.Select(n => burstSpan.StartSeconds + ((double)n / OnsetDetector.SampleRate)).ToList();
        return new MediaScan(fps, spans, means.Count, flashes, burstTrack, samples, onsetTimes);
    }

    private static StreamSpan Span(StreamSpan probed, List<(double Pts, double Duration)>? packets)
    {
        if (packets is null || packets.Count == 0)
        {
            return probed;
        }

        var start = packets.Min(p => p.Pts);
        var end = packets.Max(p => p.Pts + p.Duration);
        return probed with { StartSeconds = start, EndSeconds = end, Packets = packets.Count };
    }

    private static (List<StreamSpan> Streams, double Fps) Probe(FfmpegTools tools, string file)
    {
        var json = Run(tools.Ffprobe, ["-v", "error", "-show_entries", "stream=index,codec_type,start_time,r_frame_rate:stream_tags=title,handler_name", "-of", "json", file]);
        using var document = JsonDocument.Parse(json);
        var streams = new List<StreamSpan>();
        var fps = 0.0;
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
            var start = stream.TryGetProperty("start_time", out var s) && double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            // The engine names each track in both; the mp4 demuxer reports the handler name.
            string? title = null;
            if (stream.TryGetProperty("tags", out var tags))
            {
                title = tags.TryGetProperty("title", out var tt) ? tt.GetString()
                    : tags.TryGetProperty("handler_name", out var handler) ? handler.GetString()
                    : null;
            }

            if (type == "video" && fps == 0 && stream.TryGetProperty("r_frame_rate", out var rate))
            {
                fps = ParseRate(rate.GetString());
            }

            streams.Add(new StreamSpan(stream.GetProperty("index").GetInt32(), type, title, start, start, 0));
        }

        return (streams, fps > 0 ? fps : 60);
    }

    internal static double ParseRate(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        var parts = text.Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? n / d
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : 0;
    }

    /// <summary>Every packet's pts and duration, s, per stream index, without decoding.</summary>
    internal static Dictionary<int, List<(double Pts, double Duration)>> PacketTimes(FfmpegTools tools, string file)
    {
        var result = new Dictionary<int, List<(double, double)>>();
        var text = Run(tools.Ffprobe, ["-v", "error", "-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "csv=p=0", file]);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
            {
                continue;
            }

            var duration = double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            if (!result.TryGetValue(index, out var list))
            {
                result[index] = list = [];
            }

            list.Add((pts, duration));
        }

        return result;
    }

    private static List<double> DecodeMeans(FfmpegTools tools, string file, CancellationToken cancellation)
    {
        var means = new List<double>();
        var frame = new byte[GrayWidth * GrayHeight];
        RunTool(
            tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-i", file, "-map", "0:v:0", "-vf", $"scale={GrayWidth}:{GrayHeight}:flags=area,format=gray",
                "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1"],
            output =>
            {
                while (ReadExactly(output, frame))
                {
                    cancellation.ThrowIfCancellationRequested();
                    means.Add(FlashDetector.MeanLuma(frame));
                }
            });
        return means;
    }

    private static (List<long> Onsets, long Samples) DecodeOnsets(FfmpegTools tools, string file, int track, double burstDbfs, CancellationToken cancellation)
    {
        var detector = new OnsetDetector(burstDbfs);
        var bytes = new byte[2 * sizeof(float) * 8192];
        var scratch = new float[8192];
        RunTool(
            tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-i", file, "-map", $"0:a:{track}", "-ac", "2", "-ar", OnsetDetector.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "f32le", "pipe:1"],
            output =>
            {
                var carry = 0;
                int read;
                while ((read = output.Read(bytes, carry, bytes.Length - carry)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var total = carry + read;
                    var whole = total - (total % (2 * sizeof(float)));
                    detector.ProcessStereo(MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole)), scratch);
                    carry = total - whole;
                    bytes.AsSpan(whole, carry).CopyTo(bytes);
                }
            });
        return (detector.Onsets, detector.Samples);
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }

    internal static string Run(string exe, IReadOnlyList<string> args)
    {
        var text = "";
        RunTool(exe, args, output =>
        {
            using var reader = new StreamReader(output);
            text = reader.ReadToEnd();
        });
        return text;
    }

    /// <summary>Runs a tool, hands its stdout to <paramref name="read"/>, and fails with its stderr on a nonzero exit.</summary>
    internal static void RunTool(string exe, IReadOnlyList<string> args, Action<Stream> read)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new BenchFailure($"Could not start {exe}.");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            read(process.StandardOutput.BaseStream);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            throw;
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new BenchFailure($"{Path.GetFileName(exe)} {string.Join(' ', args)} exited with {process.ExitCode}: {stderr.GetAwaiter().GetResult().Trim()}");
        }
    }
}
