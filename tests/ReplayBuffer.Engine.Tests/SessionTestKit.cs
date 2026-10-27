using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.Audio;
using ReplayBuffer.Platform.Windows;
using PlatformFlow = ReplayBuffer.Platform.Audio.AudioFlow;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// What the session verify tests share (stories 1.10 and 1.11): throwaway pwsh windows, an
/// in-process session through the orchestrator, ffprobe/ffmpeg readouts and headless VLC.
/// </summary>
internal static class SessionTestKit
{
    public const string VlcPath = @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static void RequireFFmpeg() => Assert.SkipUnless(
        File.Exists(Path.Combine(EngineBinaries.Folder, "ffprobe.exe")) && File.Exists(Path.Combine(EngineBinaries.Folder, "ffmpeg.exe")),
        "native/ffmpeg is not fetched");

    /// <summary>Runs a pwsh <paramref name="script"/> with <c>-Title &lt;unique&gt;</c> and <paramref name="args"/>, and waits for its window.</summary>
    public static WindowInfo OpenWindow(string directory, List<Process> processes, string script, params string[] args)
    {
        var title = "RB-Session-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, title + ".ps1");
        File.WriteAllText(path, script);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-File", path, "-Title", title }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }

        processes.Add(Process.Start(start)!);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (WindowFinder.List().FirstOrDefault(w => w.Title == title) is { } window)
            {
                return window;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The test window never appeared.");
    }

    /// <summary>
    /// Runs one in-process session on <paramref name="window"/> for <paramref name="duration"/> from
    /// the anchor, through the orchestrator; <paramref name="anchored"/> runs once the anchor is known.
    /// </summary>
    public static async Task<SessionOutcome> CaptureAsync(
        WindowInfo window,
        VideoSettings video,
        string output,
        CaptureSessionOptions options,
        IVideoEncoderCatalog encoders,
        ILoggerFactory loggers,
        TimeSpan duration,
        Action? anchored = null)
    {
        var anchor = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        options = options with { Anchored = qpc => anchor.TrySetResult(qpc) };
        var document = SettingsDocument.Empty with { Global = SettingsDocument.Empty.Global.SetItem(Keys.Video.Name, Keys.Video.ToJson(video)) };
        using var orchestrator = new SessionOrchestrator(new CaptureSessionFactory(options, loggers, encoders), () => document, _ => { });

        var running = await orchestrator.StartAsync(SessionKind.Game, new CaptureTarget((int)window.ProcessId, window.Handle, null), DefaultAudio())
            .WaitAsync(Timeout, Token);
        Assert.Equal(video, running.Spec.Snapshot.Video);
        await anchor.Task.WaitAsync(Timeout, Token);
        anchored?.Invoke();
        await Task.Delay(duration, Token);
        var outcome = await orchestrator.EndAsync("test over").WaitAsync(Timeout, Token);
        Assert.Equal(output, outcome.Path);
        return outcome;
    }

    public static SessionAudio DefaultAudio()
    {
        var output = AudioEndpoints.Default(PlatformFlow.Output);
        var input = AudioEndpoints.Default(PlatformFlow.Input);
        return new SessionAudio(
        [
            new SessionAudioDevice(output.Id, SettingsFlow.Output, output.FriendlyName),
            new SessionAudioDevice(input.Id, SettingsFlow.Input, input.FriendlyName),
        ]);
    }

    /// <summary>Plays the file headless; it passes when VLC picked a video decoder and logged no decoder error.</summary>
    public static void AssertPlaysInVlc(string file, string directory, List<Process> processes)
    {
        Assert.SkipUnless(File.Exists(VlcPath), "VLC is not installed");
        var log = Path.Combine(directory, Path.GetFileNameWithoutExtension(file) + ".vlc.log");
        var start = new ProcessStartInfo(VlcPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
        {
            "-I", "dummy", "--vout=dummy", "--aout=dummy", "--no-video-title-show", "--play-and-exit", "--stop-time=2",
            "-vv", "--file-logging", "--logfile=" + log, file,
        })
        {
            start.ArgumentList.Add(arg);
        }

        using (var vlc = Process.Start(start)!)
        {
            processes.Add(vlc);
            Assert.True(vlc.WaitForExit(TimeSpan.FromSeconds(60)), "VLC did not exit");
        }

        var lines = File.ReadAllLines(log);
        Assert.Contains(lines, l => l.Contains("using video decoder module", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("decoder error", StringComparison.OrdinalIgnoreCase)
            || l.Contains("no suitable decoder", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Decodes the whole file with ffmpeg; passes when it exits 0 and reports no error.</summary>
    public static void AssertDecodesCleanly(string file)
    {
        var (exitCode, _, stderr) = Run("ffmpeg.exe", "-v", "error", "-i", file, "-f", "null", "-");
        Assert.True(exitCode == 0 && stderr.Trim().Length == 0, $"ffmpeg decode of {file}: {stderr}");
    }

    public static (string Codec, int Width, int Height, string Tag) VideoStream(string file)
    {
        var stream = Probe(file, "-select_streams", "v:0", "-show_streams").GetProperty("streams")[0];
        return (
            stream.GetProperty("codec_name").GetString()!,
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32(),
            stream.GetProperty("codec_tag_string").GetString()!);
    }

    public static List<double> VideoPts(string file) =>
        [.. Probe(file, "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-show_packets")
            .GetProperty("packets").EnumerateArray()
            .Select(p => double.Parse(p.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture))
            .Order()];

    /// <summary>The video packets' PTS in stream ticks, sorted, with the stream's time base.</summary>
    public static (List<long> Pts, long Num, long Den) VideoPtsTicks(string file)
    {
        var stream = Probe(file, "-select_streams", "v:0", "-show_streams").GetProperty("streams")[0];
        var timeBase = stream.GetProperty("time_base").GetString()!.Split('/');
        var pts = Probe(file, "-select_streams", "v:0", "-show_entries", "packet=pts", "-show_packets")
            .GetProperty("packets").EnumerateArray()
            .Select(p => p.GetProperty("pts").GetInt64())
            .Order()
            .ToList();
        return (pts, long.Parse(timeBase[0], CultureInfo.InvariantCulture), long.Parse(timeBase[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Per decoded frame, its time and the mean Y, U and V of <paramref name="crop"/> (an ffmpeg crop filter).</summary>
    public static List<(double Time, double Y, double U, double V)> FrameStats(string file, string crop)
    {
        var directory = Path.GetDirectoryName(file)!;
        var (exitCode, stdout, stderr) = RunIn(
            directory,
            "ffprobe.exe",
            "-v", "error", "-f", "lavfi", "-i", $"movie={Path.GetFileName(file)},{crop},signalstats",
            "-show_entries", "frame=pts_time:frame_tags=lavfi.signalstats.YAVG,lavfi.signalstats.UAVG,lavfi.signalstats.VAVG",
            "-of", "csv=p=0");
        Assert.True(exitCode == 0, stderr);
        return [.. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Where(parts => parts.Length >= 4)
            .Select(parts => (
                double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture)))];
    }

    public static JsonElement Probe(string file, params string[] args)
    {
        var (exitCode, stdout, stderr) = Run("ffprobe.exe", ["-v", "error", "-of", "json", .. args, file]);
        Assert.True(exitCode == 0, stderr);
        using var json = JsonDocument.Parse(stdout);
        return json.RootElement.Clone();
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string tool, params string[] args) => RunIn(null, tool, args);

    private static (int ExitCode, string Stdout, string Stderr) RunIn(string? workingDirectory, string tool, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr);
    }
}
