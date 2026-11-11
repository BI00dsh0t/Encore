using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Platform.Audio;
using PlatformFlow = Encore.Platform.Audio.AudioFlow;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Bench;

/// <summary>What a <c>bench tracks</c> capture played and how, kept so a re-analysis needs no devices.</summary>
internal sealed record TracksMarkerLog(
    long QpcFrequency,
    long EngineStartQpc,
    string Instance,
    string Hwnd,
    string DeviceA,
    string DeviceAId,
    string DeviceB,
    string DeviceBId,
    string InputDevice,
    string InputDeviceId,
    double ToneDbfs,
    double Minutes,
    CaptureSettings Capture,
    IReadOnlyList<long> BurstQpcsA,
    IReadOnlyList<long> BurstQpcsB);

/// <summary>The <c>tracks.json</c> report; VlcLabels is null when VLC was not found or listed none.</summary>
internal sealed record TracksReport(
    DateTimeOffset Created,
    double Minutes,
    string Engine,
    CaptureSettings Capture,
    double ToneDbfs,
    string? DeviceA,
    string? DeviceB,
    string? InputDevice,
    string File,
    int? EngineExit,
    string? EngineError,
    double? VideoDurationSeconds,
    double? BurstPhaseSeconds,
    IReadOnlyList<TrackFigures> Tracks,
    SyncFigures? Sync,
    IReadOnlyList<NamedTrackCheck> Checks,
    IReadOnlyList<string>? VlcLabels,
    string Verdict,
    IReadOnlyList<string> Warnings);

/// <summary><c>bench tracks</c>: two devices play known signals into a four-Track capture, then the file is checked.</summary>
internal static class TracksCheck
{
    public const string FileName = "tracks.json";
    public const string ClipName = "tracks.mp4";
    public const string MarkersName = "tracks-markers.json";

    private static readonly TimeSpan VlcTimeout = TimeSpan.FromSeconds(30);

    public static int Run(BenchContext context)
    {
        var options = context.Options;
        var minutes = options.Double("minutes", 5, 1, 60);
        var settings = options.CaptureSettings();
        var toneDbfs = options.Double("tone-dbfs", 0, -30, 0);
        var vlcOption = options.Text("vlc");
        var reanalyze = options.Flag("reanalyze");
        var (deviceA, deviceB) = reanalyze ? (null, null) : Devices(options);
        var tools = context.ResolveFfmpeg();

        var mp4 = Path.Combine(context.RunFolder, ClipName);
        var logPath = Path.Combine(context.RunFolder, MarkersName);
        var reportPath = Path.Combine(context.RunFolder, FileName);
        var warnings = new List<string>();
        string engine;
        int? exit = null;
        TracksMarkerLog log;
        if (reanalyze)
        {
            if (!File.Exists(logPath) || !File.Exists(mp4))
            {
                throw new BenchFailure($"--reanalyze needs {ClipName} and {MarkersName} in {context.RunFolder}.");
            }

            log = JsonSerializer.Deserialize<TracksMarkerLog>(File.ReadAllText(logPath), BenchJson.Options)
                ?? throw new BenchFailure($"{logPath} is empty.");
            if (Report.MissingMember(log) is { } missing)
            {
                throw new BenchFailure($"{logPath} has no {missing}.");
            }

            engine = "(reanalysed)";
            string? engineError = null;
            try
            {
                var previous = JsonSerializer.Deserialize<TracksReport>(File.ReadAllText(reportPath), BenchJson.Options);
                (engine, exit, engineError) = (previous?.Engine ?? engine, previous?.EngineExit, previous?.EngineError);

                // The capture's own warnings (B's offset, a tone player or marker failure, an instance folder left behind).
                warnings.AddRange(previous?.Warnings ?? []);
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                // No earlier tracks.json, or a malformed one.
            }

            // A capture whose engine failed stays failed, whatever its partial file holds.
            if (engineError is not null)
            {
                BenchJson.Write(reportPath, Failed(log, engine, exit, engineError, mp4, warnings));
                Console.Error.WriteLine($"The engine failed: {engineError}");
                return 1;
            }

            Console.WriteLine($"Reanalysing {mp4}");
        }
        else
        {
            engine = context.ResolveEngine();
            var seconds = (int)Math.Round(minutes * 60);
            Console.WriteLine($"Tracks: {minutes} min of the marker window with a four-Track layout, into {context.RunFolder}");
            var (code, stderr, captured) = Capture(engine, settings, seconds, mp4, Path.Combine(context.RunFolder, "tracks"), deviceA!, deviceB!, toneDbfs, minutes, warnings);
            log = captured;
            exit = code;
            File.WriteAllText(logPath, JsonSerializer.Serialize(log, BenchJson.Options));
            if (code != 0 || !File.Exists(mp4))
            {
                var error = stderr.Length > 0 ? stderr : $"the engine exited with {code} and wrote no file";
                BenchJson.Write(reportPath, Failed(log, engine, exit, error, mp4, warnings));
                Console.Error.WriteLine($"The engine failed: {error}");
                return 1;
            }
        }

        Console.WriteLine("Analysing the file ...");
        var vlc = PlayerScripts.FindVlc(vlcOption);
        if (vlc is null)
        {
            Console.WriteLine(vlcOption is null ? "VLC is not installed (pass --vlc <exe>): its check is not-checked." : $"VLC is not at '{vlcOption}': its check is not-checked.");
        }

        var report = Analyze(tools, mp4, log, engine, exit, vlc, warnings);
        BenchJson.Write(reportPath, report);
        Print(report);
        return report.Verdict == TrackAnalysis.PassVerdict ? 0 : 1;
    }

    /// <summary>The report of a capture whose engine failed: no analysis, verdict <c>failed</c>.</summary>
    private static TracksReport Failed(TracksMarkerLog log, string engine, int? exit, string error, string mp4, List<string> warnings) =>
        new(DateTimeOffset.Now, log.Minutes, engine, log.Capture, log.ToneDbfs, log.DeviceA, log.DeviceB, log.InputDevice, mp4, exit, error,
            null, null, [], null, [], null, TrackAnalysis.FailedVerdict, warnings);

    /// <summary>The two device options: both required, and not the same; throws otherwise.</summary>
    internal static (string A, string B) Devices(BenchOptions options)
    {
        var a = options.Text("device-a") ?? throw new BenchUsageException("tracks needs --device-a <Output Device>.");
        var b = options.Text("device-b") ?? throw new BenchUsageException("tracks needs --device-b <Output Device>.");
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)
            ? throw new BenchUsageException("--device-a and --device-b must be two different Output Devices.")
            : (a, b);
    }

    /// <summary>The Track Layout: Everything = {A, B, Mic}, Game = {A}, Chat = {B}, Mic = {Mic}.</summary>
    internal static TrackLayout Layout(AudioEndpoint a, AudioEndpoint b, AudioEndpoint mic)
    {
        var deviceA = new LayoutDevice(Guid.NewGuid(), a.Id, a.FriendlyName, a.ParentName, SettingsFlow.Output, IsMic: false);
        var deviceB = new LayoutDevice(Guid.NewGuid(), b.Id, b.FriendlyName, b.ParentName, SettingsFlow.Output, IsMic: false);
        var deviceMic = new LayoutDevice(Guid.NewGuid(), mic.Id, mic.FriendlyName, mic.ParentName, SettingsFlow.Input, IsMic: true);
        return new TrackLayout(
            [deviceA, deviceB, deviceMic],
            [
                new Track(Keys.EverythingTrackId, TrackAnalysis.ExpectedTitles[0], [deviceA.Key, deviceB.Key, deviceMic.Key], MuteAffected: true),
                new Track(Guid.NewGuid(), TrackAnalysis.ExpectedTitles[1], [deviceA.Key], MuteAffected: false),
                new Track(Guid.NewGuid(), TrackAnalysis.ExpectedTitles[2], [deviceB.Key], MuteAffected: false),
                new Track(Guid.NewGuid(), TrackAnalysis.ExpectedTitles[3], [deviceMic.Key], MuteAffected: true),
            ]);
    }

    /// <summary>The settings document the throwaway instance starts from: empty but for the Global Track Layout.</summary>
    internal static string SettingsJson(TrackLayout layout)
    {
        var document = SettingsDocument.Empty with
        {
            Global = SettingsDocument.Empty.Global.SetItem(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(layout)),
        };
        return JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument);
    }

    private static (int ExitCode, string Stderr, TracksMarkerLog Log) Capture(
        string engine, CaptureSettings settings, int seconds, string mp4, string logPrefix, string queryA, string queryB, double toneDbfs, double minutes,
        List<string> warnings)
    {
        var a = MarkedCapture.ResolveDevice(PlatformFlow.Output, queryA);
        var b = MarkedCapture.ResolveDevice(PlatformFlow.Output, queryB);
        var mic = MarkedCapture.ResolveDevice(PlatformFlow.Input, settings.InputDevice);
        if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new BenchFailure($"--device-a and --device-b both name '{a.FriendlyName}' [{a.Id}]; give two different Output Devices.");
        }

        Console.WriteLine($"Device A (Game): {a.FriendlyName}  [{a.Id}]");
        Console.WriteLine($"Device B (Chat): {b.FriendlyName}  [{b.Id}]");
        Console.WriteLine($"Mic:             {mic.FriendlyName}  [{mic.Id}]");

        var instance = Instance.ForTest(Instance.TestPrefix + "bench-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        Console.WriteLine($"Test instance: {instance.Name}");
        TonePlayer? toneA = null;
        TonePlayer? toneB = null;
        try
        {
            Directory.CreateDirectory(instance.RoamingDataFolder);
            File.WriteAllText(Path.Combine(instance.RoamingDataFolder, "settings.json"), SettingsJson(Layout(a, b, mic)));

            var frequency = Stopwatch.Frequency;
            (toneA, toneB) = StartStaggered(a, b, toneDbfs, warnings);

            using var marker = MarkerWindow.Open(settings.Fps);
            var hwnd = "0x" + marker.Handle.ToString("X", CultureInfo.InvariantCulture);
            var args = new List<string>
            {
                "debug-capture", "--instance", instance.Name, "--track-layout", "--hwnd", hwnd,
                "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--output", mp4,
                "--size", settings.Size, "--fps", settings.Fps.ToString(CultureInfo.InvariantCulture),
                "--bitrate", settings.BitrateKbps.ToString(CultureInfo.InvariantCulture),
            };
            Console.WriteLine($"Engine: {engine} {string.Join(' ', args)}");

            // Ctrl+C reaches the engine too, which ends its capture early and finishes the file; Bench stays to analyse it.
            ConsoleCancelEventHandler keep = (_, e) => e.Cancel = true;
            Console.CancelKeyPress += keep;
            int exit;
            string stderr;
            long startQpc;
            try
            {
                (exit, stderr, startQpc) = EngineProcess.Run(engine, args, logPrefix, TimeSpan.FromSeconds(seconds + 180));
            }
            finally
            {
                Console.CancelKeyPress -= keep;
            }

            Thread.Sleep(500);
            foreach (var (tone, name) in new[] { (toneA, "A"), (toneB!, "B") })
            {
                if (tone.Fault is { } fault)
                {
                    warnings.Add($"The tone player on device {name} failed: {fault.Message}");
                }
            }

            if (marker.Fault is { } markerFault)
            {
                warnings.Add($"The marker window failed: {markerFault.Message}");
            }

            CopyEngineLogs(instance, logPrefix + "-instance-logs");
            var log = new TracksMarkerLog(
                frequency, startQpc, instance.Name, hwnd, a.FriendlyName, a.Id, b.FriendlyName, b.Id, mic.FriendlyName, mic.Id, toneDbfs, minutes, settings,
                Stamps(toneA), Stamps(toneB!));
            return (exit, stderr, log);
        }
        finally
        {
            toneB?.Dispose();
            toneA?.Dispose();
            foreach (var folder in new[] { instance.RoamingDataFolder, instance.LocalDataFolder })
            {
                if (!DeleteQuietly(folder))
                {
                    warnings.Add($"Could not delete the test instance's folder {folder}.");
                }
            }
        }
    }

    /// <summary>Starts a tone player on each device, the second's bursts about half a second after the first's.</summary>
    internal static (TonePlayer A, TonePlayer B) StartStaggered(AudioEndpoint a, AudioEndpoint b, double toneDbfs, List<string> warnings)
    {
        TonePlayer? toneB = null;
        var frequency = Stopwatch.Frequency;
        var startA = Stopwatch.GetTimestamp();
        var toneA = StartTone(a, toneDbfs);
        try
        {
            var firstA = toneA.BurstQpc(0);

            // B's first burst comes a second plus its latency after it starts; take A's latency as B's, then check.
            var latency = firstA - startA - frequency;
            for (var attempt = 1; ; attempt++)
            {
                var phase = (long)(0.5 * frequency) - latency;
                var now = Stopwatch.GetTimestamp() + (frequency / 20);
                var at = firstA + phase + (long)Math.Ceiling((double)(now - firstA - phase) / frequency) * frequency;
                SleepUntil(at);
                toneB = StartTone(b, toneDbfs);
                var offset = Phase(firstA, toneB.BurstQpc(0), frequency);
                if (Math.Abs(offset - 0.5) <= 0.15 || attempt == 3)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"B's bursts come {offset:0.###} s after A's."));
                    if (Math.Abs(offset - 0.5) > 0.15)
                    {
                        warnings.Add(string.Create(CultureInfo.InvariantCulture, $"B's bursts come {offset:0.###} s after A's, not about 0.5 s."));
                    }

                    break;
                }

                // B's own latency, measured: the next try aims with it.
                latency = toneB.BurstQpc(0) - at - frequency;
                toneB.Dispose();
                toneB = null;
            }

            return (toneA, toneB!);
        }
        catch
        {
            toneB?.Dispose();
            toneA.Dispose();
            throw;
        }
    }

    private static TonePlayer StartTone(AudioEndpoint device, double toneDbfs)
    {
        var tone = new TonePlayer(device.Id, toneDbfs);
        tone.Start();
        var waited = Stopwatch.StartNew();
        while (tone.BurstCount == 0)
        {
            if (tone.Fault is { } fault)
            {
                tone.Dispose();
                throw new BenchFailure($"The tone player failed on '{device.FriendlyName}': {fault.Message}");
            }

            if (waited.Elapsed > TimeSpan.FromSeconds(5))
            {
                tone.Dispose();
                throw new BenchFailure($"No burst played on '{device.FriendlyName}' within 5 s.");
            }

            Thread.Sleep(5);
        }

        return tone;
    }

    private static long[] Stamps(TonePlayer tone) => [.. Enumerable.Range(0, tone.BurstCount).Select(tone.BurstQpc)];

    /// <summary>Where <paramref name="b"/> falls in <paramref name="a"/>'s one-second grid, s, from 0 to 1.</summary>
    internal static double Phase(long a, long b, long frequency)
    {
        var offset = (b - a) % frequency;
        return (double)(offset < 0 ? offset + frequency : offset) / frequency;
    }

    private static void SleepUntil(long qpc)
    {
        while (Stopwatch.GetTimestamp() < qpc)
        {
            var left = (qpc - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            Thread.Sleep((int)Math.Clamp(left - 2, 0, 50));
        }
    }

    public static TracksReport Analyze(FfmpegTools tools, string mp4, TracksMarkerLog log, string engine, int? exit, string? vlc, List<string> warnings)
    {
        if (!File.Exists(mp4))
        {
            throw new BenchFailure($"No file to analyse at '{mp4}'.");
        }

        var (video, audio) = Probe(tools, mp4);
        var decoded = Enumerable.Range(0, Math.Min(audio.Count, TrackAnalysis.Chat + 1))
            .Select(i => Task.Run(() => Decode(tools, mp4, i, audio[i].StartSeconds, log.ToneDbfs)))
            .ToArray();
        var signals = decoded.Select(t => (TrackSignal?)t.GetAwaiter().GetResult()).ToList();

        VlcListing? listing = null;
        if (vlc is not null)
        {
            Console.WriteLine($"VLC: opening the file through its HTTP interface ({vlc}) ...");
            listing = PlayerScripts.RunVlc(vlc, mp4, audio.Count, VlcTimeout);
        }

        var analysis = TrackAnalysis.Evaluate(video ?? 0, audio, signals, log.ToneDbfs, listing);
        const string NoVideo = "The file has no video stream.";
        if (video is null && !warnings.Contains(NoVideo))
        {
            warnings.Add(NoVideo);
        }

        double? phase = null;
        if (log.BurstQpcsA.Count > 0 && log.BurstQpcsB.Count > 0)
        {
            phase = Math.Round(Phase(log.BurstQpcsA[0], log.BurstQpcsB[0], log.QpcFrequency), 4);
        }

        var verdict = video is null ? TrackAnalysis.FailVerdict : analysis.Verdict;
        return new TracksReport(
            DateTimeOffset.Now, log.Minutes, engine, log.Capture, log.ToneDbfs, log.DeviceA, log.DeviceB, log.InputDevice, mp4, exit, null,
            video is { } v ? Math.Round(v, 4) : null, phase, analysis.Tracks, analysis.Sync, analysis.Checks, listing?.Labels, verdict, warnings);
    }

    /// <summary>The video's duration and each audio stream's title, default flag and span; never the title from handler_name.</summary>
    internal static (double? VideoSeconds, List<TrackProbe> Audio) Probe(FfmpegTools tools, string file)
    {
        var json = MediaAnalysis.Run(tools.Ffprobe,
            ["-v", "error", "-show_entries", "stream=index,codec_type,start_time,duration:stream_disposition=default:stream_tags=name,title", "-of", "json", file]);
        var packets = MediaAnalysis.PacketTimes(tools, file);
        using var document = JsonDocument.Parse(json);
        double? video = null;
        var audio = new List<TrackProbe>();
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var index = stream.GetProperty("index").GetInt32();
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            var (start, duration) = Span(stream, packets.GetValueOrDefault(index));
            if (type == "video")
            {
                video ??= duration;
                continue;
            }

            if (type != "audio")
            {
                continue;
            }

            string? title = null;
            if (stream.TryGetProperty("tags", out var tags))
            {
                foreach (var name in new[] { "name", "title" })
                {
                    if (title is null && tags.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text)
                    {
                        title = text;
                    }
                }
            }

            var isDefault = stream.TryGetProperty("disposition", out var disposition)
                && disposition.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == 1;
            audio.Add(new TrackProbe(title, isDefault, start, duration));
        }

        return (video, audio);
    }

    private static (double Start, double Duration) Span(JsonElement stream, List<(double Pts, double Duration)>? packets)
    {
        if (packets is { Count: > 0 })
        {
            var start = packets.Min(p => p.Pts);
            return (start, packets.Max(p => p.Pts + p.Duration) - start);
        }

        var probedStart = Seconds(stream, "start_time") ?? 0;
        return (probedStart, Seconds(stream, "duration") ?? 0);
    }

    private static double? Seconds(JsonElement stream, string name) =>
        stream.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>One audio stream decoded to 48 kHz stereo f32 and streamed through a <see cref="TrackSignalScanner"/>.</summary>
    private static TrackSignal Decode(FfmpegTools tools, string file, int track, double startSeconds, double toneDbfs)
    {
        var scanner = new TrackSignalScanner(toneDbfs);
        var bytes = new byte[2 * sizeof(float) * 8192];
        MediaAnalysis.RunTool(
            tools.Ffmpeg,
            ["-v", "error", "-nostdin", "-i", file, "-map", $"0:a:{track}", "-ac", "2", "-ar", OnsetDetector.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "f32le", "pipe:1"],
            output =>
            {
                var carry = 0;
                int read;
                while ((read = output.Read(bytes, carry, bytes.Length - carry)) > 0)
                {
                    var total = carry + read;
                    var whole = total - (total % (2 * sizeof(float)));
                    scanner.FeedStereo(MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole)));
                    carry = total - whole;
                    bytes.AsSpan(whole, carry).CopyTo(bytes);
                }
            });
        return scanner.Result(startSeconds);
    }

    private static void CopyEngineLogs(Instance instance, string target)
    {
        var logs = Path.Combine(instance.LocalDataFolder, "logs");
        try
        {
            if (!Directory.Exists(logs))
            {
                return;
            }

            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(logs))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The engine's own log is a convenience; its stdout and stderr are kept anyway.
        }
    }

    private static bool DeleteQuietly(string folder)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The engine may hold its log for a moment after it exits.
                Thread.Sleep(500);
            }
        }

        return !Directory.Exists(folder);
    }

    private static void Print(TracksReport report)
    {
        foreach (var check in report.Checks)
        {
            Console.WriteLine($"{check.Check}: {check.Figure} (target {check.Target}): {check.Mark}");
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine("Warning: " + warning);
        }

        Console.WriteLine($"Verdict: {report.Verdict}");
    }
}
