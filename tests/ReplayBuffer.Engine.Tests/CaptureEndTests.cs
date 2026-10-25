using System.Diagnostics;
using System.Text.Json;

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
        param([string] $Title, [int] $CloseAfterMs)
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

    private static string BinFolder => Path.GetDirectoryName(typeof(CommandLine).Assembly.Location)!;

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
        var title = OpenWindow(closeAfterMs: 5000);
        var output = Path.Combine(_directory, "closed.mp4");

        var engine = StartEngine(title, output);
        Assert.True(engine.WaitForExit(TimeSpan.FromSeconds(20)), "the engine did not stop when the window closed");

        Assert.Equal(0, engine.ExitCode);
        AssertThreeStreams(output);
    }

    [Fact]
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

    [Fact]
    public void AStatsRunWritesAReportThatMatchesTheFile()
    {
        RequireFFmpeg();
        var title = OpenWindow(closeAfterMs: 0);
        var output = Path.Combine(_directory, "stats.mp4");
        var statsPath = Path.Combine(_directory, "stats.json");

        // Past the 10 s warm-up, so the steady-state block is filled too.
        var engine = StartEngine(title, output, "12", "--stats", statsPath, "--gpu-priority", "0");
        Assert.True(engine.WaitForExit(TimeSpan.FromSeconds(40)), "the engine did not stop");
        Assert.Equal(0, engine.ExitCode);

        using var stats = JsonDocument.Parse(File.ReadAllText(statsPath));
        var root = stats.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());

        var video = root.GetProperty("video");
        var encoded = video.GetProperty("framesEncoded").GetInt64();
        Assert.True(encoded > 60, $"only {encoded} frames encoded");
        var latency = root.GetProperty("latency");
        Assert.Equal(encoded, latency.GetProperty("count").GetInt64() + latency.GetProperty("flushed").GetInt64());
        Assert.Equal(video.GetProperty("framesArrived").GetInt64(), encoded + video.GetProperty("duplicates").GetInt64());
        Assert.Equal(0, video.GetProperty("nonIncreasingStamps").GetInt64());
        Assert.InRange(root.GetProperty("latency").GetProperty("p99Ms").GetDouble(), 0, 250);

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
            var samples = audio[i].GetProperty("samplesEncoded").GetInt64();
            Assert.Equal(samples, audio[i].GetProperty("timeline").GetProperty("writtenFrames").GetInt64());

            // AAC packs 1024 samples a packet; the last one is partial.
            Assert.Equal((samples + 1023) / 1024, streams[i + 1].Packets);
            Assert.InRange(streams[i + 1].DurationTs, samples - 1024, samples + 1024);
        }
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

    private static void RequireFFmpeg() =>
        Assert.SkipUnless(File.Exists(Path.Combine(BinFolder, "ffprobe.exe")), "native/ffmpeg is not fetched");

    private string OpenWindow(int closeAfterMs)
    {
        var title = "RB-Test-" + Guid.NewGuid().ToString("N");
        Pwsh(WindowScript, "window.ps1", "-Title", title, "-CloseAfterMs", closeAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
