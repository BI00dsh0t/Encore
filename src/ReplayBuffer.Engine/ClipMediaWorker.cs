using System.Globalization;
using System.Text;
using System.Text.Json;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine;

/// <summary>AD-28 isolated worker. No settings, capture host, instance mutex or live IPC.</summary>
public static class ClipMediaWorker
{
    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string Tool(string name) => Path.Combine(AppContext.BaseDirectory, name + ".exe");

    public static async Task<int> RunAsync()
    {
        using var cancel = new CancellationTokenSource();
        try
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line is null || line.Length > 65536) { throw new IOException("Missing or oversized clip request."); }
            var request = JsonSerializer.Deserialize(line, ClipMediaJsonContext.Default.ClipMediaRequest)
                ?? throw new IOException("Invalid clip request.");
            // Closing the private request pipe cancels this worker and its owned tool process.
            _ = Task.Run(() => CancelOnCloseAsync(cancel));
            var result = await ExecuteAsync(request, new ProgressSink(), cancel.Token).ConfigureAwait(false);
            Write(result);
            return 0;
        }
        catch (Exception error)
        {
            Write(new ClipMediaResponse("error", Error: error is OperationCanceledException ? "Clip operation cancelled." : error.Message));
            return 1;
        }
    }

    private static async Task CancelOnCloseAsync(CancellationTokenSource cancel)
    {
        try { await Console.In.ReadLineAsync().ConfigureAwait(false); cancel.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void Write(ClipMediaResponse response) =>
        Console.WriteLine(JsonSerializer.Serialize(response, ClipMediaJsonContext.Default.ClipMediaResponse));

    private sealed class ProgressSink : IProgress<double>
    {
        public void Report(double value) => Write(new ClipMediaResponse("progress", Progress: value));
    }

    public static void ValidateSource(ClipSource source)
    {
        var file = new FileInfo(source.Path);
        if (!file.Exists || file.Length != source.Length || file.LastWriteTimeUtc.Ticks != source.ModifiedUtcTicks)
        { throw new IOException("The source disappeared or changed. Reopen it from the Library."); }
    }

    public static async Task<ClipMediaResponse> ExecuteAsync(ClipMediaRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ValidateSource(request.Source);
        var metadata = await ProbeAsync(request.Source, cancellationToken).ConfigureAwait(false);
        ClipMediaResponse response;
        switch (request.Operation)
        {
            case "probe": response = new("complete", Metadata: metadata); break;
            case "waveform":
                CheckRange(request.Start, request.End, metadata.Duration);
                var lanes = new List<ClipWaveform>();
                foreach (var track in SelectedTracks(request, metadata))
                {
                    var bins = await WaveformAsync(request, metadata, track, cancellationToken).ConfigureAwait(false);
                    lanes.Add(new(request.Start, request.End, track.StreamIndex, bins));
                }
                response = new("complete", Waveforms: [.. lanes]);
                break;
            case "range":
                response = new("complete", Range: await EffectiveRangeAsync(request, metadata, cancellationToken).ConfigureAwait(false));
                break;
            case "export":
            case "preview":
                if (request.Format == "mp3" && SelectedTracks(request, metadata).Length == 0)
                { throw new IOException("MP3 export needs at least one audio track."); }
                var range = await EffectiveRangeAsync(request, metadata, cancellationToken).ConfigureAwait(false);
                // Preserve the chosen export's range semantics even though preview needs an MP4 picture.
                var renderRequest = request.Operation == "preview" ? request with { Format = "mp4" } : request;
                var output = await ExportAsync(renderRequest, metadata, range, progress, cancellationToken).ConfigureAwait(false);
                // Rename is the commit point. Do not turn an already-published output into failure.
                return new("complete", Range: range, Output: output);
            default: throw new IOException("Unknown clip operation.");
        }
        ValidateSource(request.Source);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    private static async Task<ClipMetadata> ProbeAsync(ClipSource source, CancellationToken token)
    {
        using var text = new MemoryStream();
        await SharedMediaProcess.RunAsync(Tool("ffprobe"),
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", "fd:"], source.Path,
            async (stream, ct) =>
            {
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    if (text.Length + count > 4 * 1024 * 1024) { throw new IOException("Media metadata is too large."); }
                    text.Write(buffer, 0, count);
                }
            }, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(text.ToArray());
        var root = json.RootElement;
        var format = root.GetProperty("format");
        var duration = ReadDouble(format, "duration");
        var origin = ReadDouble(format, "start_time");
        var tracks = new List<ClipAudioTrack>();
        var width = 0;
        var height = 0;
        var fps = 0d;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            if (stream.GetProperty("codec_type").GetString() == "video" && width == 0)
            {
                width = stream.GetProperty("width").GetInt32();
                height = stream.GetProperty("height").GetInt32();
                var rate = stream.GetProperty("avg_frame_rate").GetString()?.Split('/');
                if (rate is [var numerator, var denominator] && double.TryParse(numerator, CultureInfo.InvariantCulture, out var n)
                    && double.TryParse(denominator, CultureInfo.InvariantCulture, out var d) && d > 0) { fps = n / d; }
            }
            if (stream.GetProperty("codec_type").GetString() == "audio")
            {
                var index = stream.GetProperty("index").GetInt32();
                string? name = null;
                if (stream.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("title", out var title)) { name = title.GetString(); }
                    else if (tags.TryGetProperty("handler_name", out var handler)) { name = handler.GetString(); }
                }
                tracks.Add(new(index, string.IsNullOrWhiteSpace(name) ? $"Track {tracks.Count + 1}" : name,
                    (int)ReadDouble(stream, "sample_rate"), (int)ReadDouble(stream, "channels")));
            }
        }
        if (!double.IsFinite(duration) || duration <= 0 || width <= 0 || height <= 0)
        { throw new IOException("This source has no finished, seekable video timeline."); }
        return new(duration, width, height, fps, [.. tracks], origin);
    }

    private static double ReadDouble(JsonElement element, string key) => element.TryGetProperty(key, out var value)
        && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static ClipAudioTrack[] SelectedTracks(ClipMediaRequest request, ClipMetadata metadata)
    {
        var selected = request.Tracks ?? [];
        if (selected.Length != selected.Distinct().Count() || selected.Any(i => !metadata.Tracks.Any(t => t.StreamIndex == i)))
        { throw new IOException("The chosen audio tracks are no longer available."); }
        return metadata.Tracks.Where(t => selected.Contains(t.StreamIndex)).ToArray();
    }

    private static void CheckRange(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > duration + 0.000001)
        { throw new IOException("Choose a non-empty range within the source."); }
    }

    private static async Task<ClipRange> EffectiveRangeAsync(ClipMediaRequest request, ClipMetadata metadata, CancellationToken token)
    {
        CheckRange(request.Start, request.End, metadata.Duration);
        if (request.Format == "mp3")
        {
            // Audio-only boundaries are independent of the video's frame cadence. Output is 48 kHz;
            // atrim/resample retain each source stream's timing relative to this common sample origin.
            var startSample = Math.Ceiling(request.Start * 48000 - 0.0000001);
            var endSample = Math.Ceiling(request.End * 48000 - 0.0000001);
            if (endSample <= startSample) { throw new IOException("Choose a range containing at least one audio sample."); }
            return new(startSample / 48000, endSample / 48000, "audioSamples48000");
        }
        var boundaries = new double[2];
        for (var i = 0; i < 2; i++)
        {
            var target = i == 0 ? request.Start : request.End;
            if (i == 1 && metadata.Duration - target < 0.000001) { boundaries[i] = metadata.Duration; continue; }
            double? found = null;
            // ffprobe seeks to a preceding keyframe, then emits actual decoded frame timestamps.
            await SharedMediaProcess.RunAsync(Tool("ffprobe"),
                ["-v", "error", "-select_streams", "v:0", "-read_intervals", Number(metadata.Origin + target) + "%" + Number(metadata.Origin + Math.Min(metadata.Duration, target + 5)),
                    "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", "fd:"], request.Source.Path,
                async (stream, ct) =>
                {
                    using var reader = new StreamReader(stream);
                    while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        if (double.TryParse(line.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp))
                        {
                            var relative = timestamp - metadata.Origin;
                            if (relative + 0.0000001 >= target && (found is null || relative < found)) { found = relative; }
                        }
                    }
                }, token).ConfigureAwait(false);
            boundaries[i] = found ?? (i == 1 ? metadata.Duration : throw new IOException("No video frame exists in this selection."));
        }
        if (boundaries[1] <= boundaries[0]) { throw new IOException("Choose a range containing at least one video frame."); }
        return new(boundaries[0], boundaries[1]);
    }

    private static string AudioTrim(int streamIndex, ClipRange range, double origin) =>
        $"[0:{streamIndex}]atrim=start={Number(origin + range.Start)}:end={Number(origin + range.End)},asetpts=PTS-{Number(origin + range.Start)}/TB,aresample=48000:async=1:first_pts=0,apad,atrim=duration={Number(range.End - range.Start)}";

    private static async Task<float[]> WaveformAsync(ClipMediaRequest request, ClipMetadata metadata, ClipAudioTrack track, CancellationToken token)
    {
        var bins = new float[Math.Clamp(request.Bins, 32, 4096)];
        var duration = request.End - request.Start;
        var filter = AudioTrim(track.StreamIndex, new(request.Start, request.End), metadata.Origin) + "[a]";
        long sample = 0;
        await SharedMediaProcess.RunAsync(Tool("ffmpeg"),
            ["-v", "error", "-nostdin", "-threads", "2", "-copyts", "-seek_timestamp", "1", "-ss", Number(metadata.Origin + Math.Max(0, request.Start - 1)), "-i", "fd:", "-filter_complex_threads", "1", "-filter_complex", filter,
                "-map", "[a]", "-ac", "2", "-ar", "48000", "-t", Number(duration), "-f", "f32le", "pipe:1"], request.Source.Path,
            async (stream, ct) =>
            {
                var buffer = new byte[8192];
                var pending = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(pending), ct).ConfigureAwait(false)) != 0)
                {
                    count += pending;
                    var complete = count - count % 8;
                    for (var offset = 0; offset < complete; offset += 8)
                    {
                        var peak = Math.Max(Math.Abs(BitConverter.ToSingle(buffer, offset)), Math.Abs(BitConverter.ToSingle(buffer, offset + 4)));
                        var bin = (int)Math.Min(bins.Length - 1, sample * bins.Length / (48000 * duration));
                        if (float.IsFinite(peak)) { bins[bin] = Math.Max(bins[bin], Math.Min(1, peak)); }
                        sample++;
                    }
                    pending = count - complete;
                    buffer.AsSpan(complete, pending).CopyTo(buffer);
                }
            }, token).ConfigureAwait(false);
        return bins;
    }

    private static async Task<string> ExportAsync(ClipMediaRequest request, ClipMetadata metadata, ClipRange range, IProgress<double>? progress, CancellationToken token)
    {
        if (request.Format is not ("mp4" or "mp3") || string.IsNullOrWhiteSpace(request.Destination))
        { throw new IOException("Choose an MP4 or MP3 destination."); }
        var tracks = SelectedTracks(request, metadata);
        if (request.Format == "mp3" && tracks.Length == 0) { throw new IOException("MP3 export needs at least one audio track."); }
        var destination = Path.GetFullPath(request.Destination);
        if (File.Exists(destination) || Directory.Exists(destination)) { throw new IOException("That destination already exists. Choose a new name."); }
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose a destination folder.");
        if (!Directory.Exists(parent)) { throw new IOException("The destination folder no longer exists."); }
        var scratch = Path.Combine(parent, ".rb-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var separateFiles = request.Format == "mp3" && !request.Mix;
        try
        {
            if (separateFiles)
            {
                for (var i = 0; i < tracks.Length; i++)
                {
                    var name = string.Concat(tracks[i].Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    if (name.Length > 80) { name = name[..80]; }
                    var file = Path.Combine(scratch, $"{i + 1:00} {name.TrimEnd('.', ' ')}.mp3");
                    await EncodeAsync(request, metadata, range, [tracks[i]], file, progress, token).ConfigureAwait(false);
                }
            }
            else
            {
                await EncodeAsync(request, metadata, range, tracks, Path.Combine(scratch, "output.partial"), progress, token).ConfigureAwait(false);
            }
            ValidateSource(request.Source);
            token.ThrowIfCancellationRequested();
            if (separateFiles) { Directory.Move(scratch, destination); }
            else { File.Move(Path.Combine(scratch, "output.partial"), destination, false); }
            return destination;
        }
        finally
        {
            // Cleanup cannot convert an already committed export into failure.
            try { if (Directory.Exists(scratch)) { Directory.Delete(scratch, true); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task EncodeAsync(ClipMediaRequest request, ClipMetadata metadata, ClipRange range, ClipAudioTrack[] tracks, string output, IProgress<double>? progress, CancellationToken token)
    {
        var arguments = new List<string> { "-v", "error", "-nostdin", "-n", "-threads", "2", "-copyts", "-seek_timestamp", "1", "-ss", Number(metadata.Origin + Math.Max(0, range.Start - 1)), "-i", "fd:", "-filter_complex_threads", "1" };
        var filters = new List<string>();
        if (request.Format == "mp4")
        {
            filters.Add($"[0:v:0]trim=start={Number(metadata.Origin + range.Start)}:end={Number(metadata.Origin + range.End)},setpts=PTS-{Number(metadata.Origin + range.Start)}/TB,pad=ceil(iw/2)*2:ceil(ih/2)*2[v]");
        }
        for (var i = 0; i < tracks.Length; i++) { filters.Add(AudioTrim(tracks[i].StreamIndex, range, metadata.Origin) + $"[a{i}]"); }
        if (request.Mix && tracks.Length > 0)
        {
            filters.Add(string.Concat(Enumerable.Range(0, tracks.Length).Select(i => $"[a{i}]"))
                + $"amix=inputs={tracks.Length}:normalize=1:duration=longest,alimiter=limit=0.95:level=0:latency=1[mix]");
        }
        if (filters.Count > 0) { arguments.AddRange(["-filter_complex", string.Join(';', filters)]); }
        if (request.Format == "mp4")
        {
            arguments.AddRange(["-map", "[v]", "-c:v", "libx264", "-threads:v", "2", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough"]);
        }
        if (tracks.Length > 0)
        {
            if (request.Mix) { arguments.AddRange(["-map", "[mix]", "-metadata:s:a:0", "title=Combined mix", "-metadata:s:a:0", "handler_name=Combined mix"]); }
            else
            {
                for (var i = 0; i < tracks.Length; i++) { arguments.AddRange(["-map", $"[a{i}]", $"-metadata:s:a:{i}", "title=" + tracks[i].Name, $"-metadata:s:a:{i}", "handler_name=" + tracks[i].Name]); }
            }
            arguments.AddRange(["-c:a", request.Format == "mp4" ? "aac" : "libmp3lame", "-threads:a", "2", "-ar", "48000", "-ac", "2", "-b:a", "192k"]);
        }
        if (request.Format == "mp4") { arguments.AddRange(["-movflags", "+faststart"]); }
        arguments.AddRange(["-t", Number(range.End - range.Start), "-progress", "pipe:1", "-nostats", "-f", request.Format, output]);
        await SharedMediaProcess.RunAsync(Tool("ffmpeg"), arguments, request.Source.Path,
            async (stream, ct) =>
            {
                using var reader = new StreamReader(stream);
                var last = -1d;
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds))
                    {
                        var fraction = Math.Clamp(microseconds / 1_000_000 / (range.End - range.Start), 0, 1);
                        if (fraction - last >= 0.01) { last = fraction; progress?.Report(fraction); }
                    }
                }
            }, token).ConfigureAwait(false);
    }
}
