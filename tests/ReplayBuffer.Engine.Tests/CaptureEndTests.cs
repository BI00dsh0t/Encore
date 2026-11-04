using System.Diagnostics;
using System.Text.Json;
using ReplayBuffer.Platform.FFmpeg.Tests;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// The engine end to end against a throwaway window: closing the window, or Ctrl+C, must
/// finalize a file ffprobe reads with all three streams. Uses the default (eConsole) audio
/// devices for a few seconds; the file is deleted afterwards.
/// </summary>
public sealed class CaptureEndTests : IDisposable
{
    private const string WindowScript =
        """
        param([string] $Title, [int] $CloseAfterMs, [string] $CloseFlag)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.Width = 641
        $form.Height = 481
        $start = [Diagnostics.Stopwatch]::StartNew()
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 16
        $timer.Add_Tick({
            $v = [int](($start.ElapsedMilliseconds / 4) % 255)
            $form.BackColor = [Drawing.Color]::FromArgb($v, 255 - $v, 128)
            if ($CloseAfterMs -gt 0 -and $start.ElapsedMilliseconds -ge $CloseAfterMs) { $form.Close() }
            if ($CloseFlag -and (Test-Path $CloseFlag)) { $form.Close() }
        })
        $timer.Start()
        $form.Show()
        $form.Hide()
        [void] $form.ShowDialog()
        """;

    private const string CtrlCScript =
        """
        param([int] $TargetPid)
        Add-Type -Namespace Native -Name Con -MemberDefinition @'
        [DllImport("kernel32.dll")] public static extern bool FreeConsole();
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
        '@
        [void] [Native.Con]::FreeConsole()
        if (-not [Native.Con]::AttachConsole([uint32] $TargetPid)) { exit 1 }
        [void] [Native.Con]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
        if (-not [Native.Con]::GenerateConsoleCtrlEvent(0, 0)) { exit 2 }
        Start-Sleep -Milliseconds 500
        """;

    // The test host ignores Ctrl+C, and children inherit that; the launcher turns it back on
    // just for the engine it starts, then waits and passes the engine's exit code through.
    private const string LauncherScript =
        """
        param([string] $Engine, [string] $EngineArgs)
        Add-Type -Namespace Native -Name Ctrl -MemberDefinition @'
        [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        '@
        [void] [Native.Ctrl]::SetConsoleCtrlHandler([IntPtr]::Zero, $false)
        $start = [Diagnostics.ProcessStartInfo]::new($Engine, $EngineArgs)
        $start.UseShellExecute = $false
        $child = [Diagnostics.Process]::Start($start)
        [void] [Native.Ctrl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
        $child.WaitForExit()
        exit $child.ExitCode
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-end-").FullName;
    private readonly List<Process> _processes = [];

    private static string BinFolder => EngineBinaries.Folder;

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ClosingTheWindowEndsCaptureAndFinalizesTheFile()
    {
        RequireFFmpeg();
        // The window closes once the engine is writing the file, not on a fixed timer: a cold engine
        // start on a loaded machine sometimes outlasted 2.5 s and found the window already gone.
        var closeFlag = Path.Combine(_directory, "close-now");
        var title = OpenWindow(closeAfterMs: 0, closeFlag);
        var output = Path.Combine(_directory, "closed.mp4");

        var engine = StartEngine(title, output);
        var deadline = Stopwatch.StartNew();
        while (!(File.Exists(output) && new FileInfo(output).Length > 0))
        {
            Assert.False(engine.HasExited, $"the engine exited with {(engine.HasExited ? engine.ExitCode : 0)} before writing the file");
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "the engine never started writing the file");
            Thread.Sleep(50);
        }

        File.WriteAllText(closeFlag, "");
        Assert.True(engine.WaitForExit(TimeSpan.FromSeconds(20)), "the engine did not stop when the window closed");

        Assert.Equal(0, engine.ExitCode);
        AssertThreeStreams(output);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void CtrlCEndsCaptureAndFinalizesTheFile()
    {
        RequireFFmpeg();
        var title = OpenWindow(closeAfterMs: 0);
        var output = Path.Combine(_directory, "ctrlc.mp4");

        var engine = Pwsh(LauncherScript, "launch.ps1",
            "-Engine", Path.Combine(BinFolder, "ReplayBuffer.Engine.exe"),
            "-EngineArgs", $"debug-capture --window {title} --seconds 30 --output \"{output}\"");
        Thread.Sleep(TimeSpan.FromSeconds(5));
        var sender = Pwsh(CtrlCScript, "ctrlc.ps1", "-TargetPid", engine.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(sender.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, sender.ExitCode);
        Assert.True(engine.WaitForExit(TimeSpan.FromSeconds(20)), "the engine did not stop on Ctrl+C");

        Assert.Equal(0, engine.ExitCode);
        AssertThreeStreams(output);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void AStatsRunWritesAReportThatMatchesTheFile()
    {
        RequireFFmpeg();
        var title = OpenWindow(closeAfterMs: 0);
        RealTimeAttempts.Run(attempt => StatsReportAttempt(title, attempt));
    }

    private void StatsReportAttempt(string title, int attempt)
    {
        var output = Path.Combine(_directory, $"stats-{attempt}.mp4");
        var statsPath = Path.Combine(_directory, $"stats-{attempt}.json");

        // Past the 10 s warm-up, so the steady-state block is filled too.
        var engine = StartEngine(title, output, "12", "--stats", statsPath, "--gpu-priority", "0");
        var stopped = engine.WaitForExit(TimeSpan.FromSeconds(40));
        if (!stopped)
        {
            // Not left running beside the next attempt.
            engine.Kill(entireProcessTree: true);
            engine.WaitForExit();
        }

        Assert.True(stopped, "the engine did not stop");
        Assert.Equal(0, engine.ExitCode);

        using var stats = JsonDocument.Parse(File.ReadAllText(statsPath));
        var root = stats.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());

        var video = root.GetProperty("video");
        var encoded = video.GetProperty("framesEncoded").GetInt64();
        Assert.True(encoded > 60, $"only {encoded} frames encoded");
        AssertPacedCounts(root);
        Assert.Equal(0, video.GetProperty("nonIncreasingStamps").GetInt64());

        // The defaults: the tracer's, with the encoder thread always on since story 1.11.
        var encoder = root.GetProperty("encoder");
        Assert.Equal("full", encoder.GetProperty("stages").GetString());
        Assert.Equal("p5", encoder.GetProperty("preset").GetString());
        Assert.Equal(JsonValueKind.Null, encoder.GetProperty("tune").ValueKind);
        Assert.Equal("x1", encoder.GetProperty("color").GetString());
        Assert.True(encoder.GetProperty("encodeThread").GetBoolean());

        var priority = root.GetProperty("gpuPriority");
        Assert.Equal(0, priority.GetProperty("requested").GetInt32());
        Assert.True(priority.GetProperty("succeeded").GetBoolean());

        var steady = root.GetProperty("resources").GetProperty("steady");
        Assert.InRange(steady.GetProperty("seconds").GetDouble(), 1, 3);
        Assert.True(steady.GetProperty("cpuSeconds").GetDouble() >= 0);

        var streams = ProbeStreams(output);
        Assert.Equal(encoded, streams[0].Packets);

        var audio = root.GetProperty("audio").EnumerateArray().ToList();
        Assert.Equal(["output", "input"], audio.Select(a => a.GetProperty("role").GetString()));
        for (var i = 0; i < 2; i++)
        {
            // Every Track is padded with silence to one common end (story 2.2), so a device's Track
            // holds all it wrote, plus at most the gap to the longest device or the last video frame.
            var samples = audio[i].GetProperty("samplesEncoded").GetInt64();
            Assert.Equal(audio[0].GetProperty("samplesEncoded").GetInt64(), samples);
            Assert.InRange(samples - audio[i].GetProperty("timeline").GetProperty("writtenFrames").GetInt64(), 0, 48_000 / 10);

            // The hub's steering of the device to QPC (story 1.12).
            var resampler = audio[i].GetProperty("resampler");
            Assert.Equal(JsonValueKind.Object, resampler.ValueKind);
            Assert.True(resampler.GetProperty("stepCorrections").GetInt64() >= 0);
            Assert.InRange(resampler.GetProperty("ratioPpm").GetDouble(), -1000, 1000);
            Assert.InRange(resampler.GetProperty("meanRatioPpm").GetDouble(), -1000, 1000);
            Assert.True(resampler.GetProperty("outputFrames").GetInt64() >= samples);

            // AAC packs 1024 samples a packet; the last one is partial.
            Assert.Equal((samples + 1023) / 1024, streams[i + 1].Packets);
            Assert.InRange(streams[i + 1].DurationTs, samples - 1024, samples + 1024);
        }

        // Then the timing bounds, which a neighbouring GPU test can make this attempt miss.
        RealTimeAttempts.InRange(video.GetProperty("queueDrops").GetInt64(), 0, 0, "queue drops");
        RealTimeAttempts.InRange(video.GetProperty("skippedSlots").GetInt64(), 0, 0, "skipped slots");
        RealTimeAttempts.InRange(root.GetProperty("latency").GetProperty("p99Ms").GetDouble(), 0, 250, "latency p99 ms");
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void TheEncodeExperimentsRecordTheirOptionsAndWriteAPlayableFile()
    {
        RequireFFmpeg();
        var title = OpenWindow(closeAfterMs: 0);
        var output = Path.Combine(_directory, "experiment.mp4");
        var statsPath = Path.Combine(_directory, "experiment.json");

        var engine = StartEngine(title, output, "4", "--stats", statsPath,
            "--color", "x2", "--nvenc-preset", "p1", "--nvenc-tune", "ll", "--encode-thread");
        Assert.True(engine.WaitForExit(TimeSpan.FromSeconds(30)), "the engine did not stop");
        Assert.Equal(0, engine.ExitCode);

        using var stats = JsonDocument.Parse(File.ReadAllText(statsPath));
        var root = stats.RootElement;
        var encoder = root.GetProperty("encoder");
        Assert.Equal("p1", encoder.GetProperty("preset").GetString());
        Assert.Equal("ll", encoder.GetProperty("tune").GetString());
        Assert.Equal("x2", encoder.GetProperty("color").GetString());
        Assert.True(encoder.GetProperty("encodeThread").GetBoolean());

        var video = root.GetProperty("video");
        var encoded = video.GetProperty("framesEncoded").GetInt64();
        Assert.True(encoded > 30, $"only {encoded} frames encoded");
        AssertPacedCounts(root);

        var streams = ProbeStreams(output);
        Assert.Equal(["h264", "aac", "aac"], streams.Select(s => s.Codec));
        Assert.Equal(encoded, streams[0].Packets);
    }

    [Fact]
    public void AnUnwritableStatsPathFailsBeforeCaptureStarts()
    {
        RequireFFmpeg();
        var title = OpenWindow(closeAfterMs: 0);
        var output = Path.Combine(_directory, "never.mp4");
        var blocker = Path.Combine(_directory, "a-file");
        File.WriteAllText(blocker, "");

        // The stats folder would have to be inside a file.
        var engine = StartEngine(title, output, "30", "--stats", Path.Combine(blocker, "stats.json"));
        Assert.True(engine.WaitForExit(TimeSpan.FromSeconds(15)), "the engine ran instead of failing up front");

        Assert.Equal(1, engine.ExitCode);
        Assert.False(File.Exists(output));
    }

    /// <summary>
    /// The paced counts agree: one packet per slot, new or repeated; every new slot is a frame that
    /// arrived; and latency is recorded for new frames only (with NVENC's delay 0, none waits for the flush).
    /// </summary>
    private static void AssertPacedCounts(JsonElement root)
    {
        var video = root.GetProperty("video");
        var encoded = video.GetProperty("framesEncoded").GetInt64();
        var fresh = video.GetProperty("freshSlots").GetInt64();
        var repeated = video.GetProperty("duplicates").GetInt64();
        Assert.Equal(encoded, fresh + repeated);
        Assert.True(fresh + video.GetProperty("heldReplaced").GetInt64() + video.GetProperty("queueDrops").GetInt64()
            <= video.GetProperty("framesArrived").GetInt64());

        var latency = root.GetProperty("latency");
        Assert.Equal(fresh, latency.GetProperty("count").GetInt64() + latency.GetProperty("flushed").GetInt64());
    }

    private static void RequireFFmpeg() =>
        Assert.SkipUnless(File.Exists(Path.Combine(BinFolder, "ffprobe.exe")), "native/ffmpeg is not fetched");

    private string OpenWindow(int closeAfterMs, string closeFlag = "")
    {
        var title = "RB-Test-" + Guid.NewGuid().ToString("N");
        Pwsh(WindowScript, "window.ps1", "-Title", title, "-CloseAfterMs", closeAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture), "-CloseFlag", closeFlag);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (Platform.Windows.WindowFinder.List().Any(w => w.Title == title))
            {
                return title;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The test window never appeared.");
    }

    private Process StartEngine(string title, string output, string seconds = "30", params string[] extra)
    {
        // A console of its own (hidden), so a Ctrl+C reaches the engine and nothing else.
        var start = new ProcessStartInfo(Path.Combine(BinFolder, "ReplayBuffer.Engine.exe"))
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[] { "debug-capture", "--window", title, "--seconds", seconds, "--output", output }.Concat(extra))
        {
            start.ArgumentList.Add(arg);
        }

        var process = Process.Start(start)!;
        _processes.Add(process);
        return process;
    }

    private Process Pwsh(string script, string name, params string[] args)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, script);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-File", path }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }

        var process = Process.Start(start)!;
        _processes.Add(process);
        return process;
    }

    private static List<(string Codec, long Packets, long DurationTs)> ProbeStreams(string file)
    {
        var start = new ProcessStartInfo(Path.Combine(BinFolder, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-count_packets", "-show_streams", "-of", "json", file })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);

        using var json = JsonDocument.Parse(stdout.Result);
        return json.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => (
                s.GetProperty("codec_name").GetString()!,
                long.Parse(s.GetProperty("nb_read_packets").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                s.TryGetProperty("duration_ts", out var d) ? d.GetInt64() : -1))
            .ToList();
    }

    private void AssertThreeStreams(string file)
    {
        var start = new ProcessStartInfo(Path.Combine(BinFolder, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-show_streams", "-of", "json", file })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);

        using var json = JsonDocument.Parse(stdout.Result);
        var codecs = json.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => s.GetProperty("codec_name").GetString())
            .ToList();
        Assert.Equal(["h264", "aac", "aac"], codecs);
    }
}
