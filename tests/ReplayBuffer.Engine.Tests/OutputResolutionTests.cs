using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Engine.Sessions;
using ReplayBuffer.Platform.FFmpeg.Tests;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// Story 1.11's verify: a window that is resized and changes mode mid-session yields one playable
/// file at the fixed output size with no PTS break and the 4:3 phase letterboxed; a static window
/// still gets the configured fps; and a 20 s stats run on a fast window meets the latency, GC and
/// allocation bounds. Runs alone (<see cref="OutputSettingsCollection"/>): its timing and frame
/// counts would suffer from encode tests running beside it. The files are deleted afterwards.
/// </summary>
[Collection(nameof(OutputSettingsCollection))]
public sealed class OutputResolutionTests : IDisposable
{
    // A borderless 1280×720 window in one colour that, once the go file appears, becomes
    // 1920×1080, then covers its whole monitor ("fullscreen" here: borderless, monitor-sized),
    // then a bordered 1024×768 window; each phase lasts PhaseMs. It keeps invalidating.
    private const string ResizingWindowScript =
        """
        param([string] $Title, [string] $GoFile, [int] $PhaseMs)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.StartPosition = 'Manual'
        $form.FormBorderStyle = 'None'
        $form.BackColor = [Drawing.Color]::FromArgb(40, 200, 90)
        $form.Bounds = [Drawing.Rectangle]::new(0, 0, 1280, 720)
        # The hidden launch's SW_HIDE applies to the first window shown: spend it on a throwaway show.
        $form.Show()
        $form.Hide()
        $form.Show()
        $screen = [System.Windows.Forms.Screen]::FromControl($form).Bounds
        while ($form.Visible -and -not (Test-Path $GoFile)) {
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 2
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $phase = 0
        while ($form.Visible) {
            $now = [int][math]::Floor($clock.ElapsedMilliseconds / $PhaseMs)
            if ($now -ne $phase) {
                $phase = $now
                switch ($phase) {
                    1 { $form.Bounds = [Drawing.Rectangle]::new(0, 0, 1920, 1080) }
                    2 { $form.Bounds = $screen }
                    3 { $form.FormBorderStyle = 'FixedSingle'; $form.Bounds = [Drawing.Rectangle]::new(100, 100, 1024, 768) }
                }
            }
            $form.Invalidate()
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 2
        }
        """;

    // A borderless window painted once, then left alone.
    private const string StaticWindowScript =
        """
        param([string] $Title, [int] $Width, [int] $Height)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.FormBorderStyle = 'None'
        $form.StartPosition = 'Manual'
        $form.Left = 0
        $form.Top = 0
        $form.Width = $Width
        $form.Height = $Height
        $form.BackColor = [Drawing.Color]::FromArgb(200, 60, 60)
        $form.Show()
        $form.Hide()
        [void] $form.ShowDialog()
        """;

    // A borderless window that repaints as fast as it can: a new colour every pass.
    private const string FastWindowScript =
        """
        param([string] $Title, [int] $Width, [int] $Height)
        Add-Type -AssemblyName System.Windows.Forms
        $form = New-Object System.Windows.Forms.Form
        $form.Text = $Title
        $form.FormBorderStyle = 'None'
        $form.StartPosition = 'Manual'
        $form.Left = 0
        $form.Top = 0
        $form.Width = $Width
        $form.Height = $Height
        $form.Show()
        $form.Hide()
        $form.Show()
        $i = 0
        while ($form.Visible) {
            $i = ($i + 1) % 255
            $form.BackColor = [Drawing.Color]::FromArgb($i, 255 - $i, 128)
            $form.Refresh()
            [System.Windows.Forms.Application]::DoEvents()
        }
        """;

    private const int PhaseMs = 2000;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-resolution-").FullName;
    private readonly List<Process> _processes = [];

    private static ITestOutputHelper? Output => TestContext.Current.TestOutputHelper;

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

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AWindowResizedAndChangingModeGivesOneFixedSizeFileWithNoPtsBreak()
    {
        SessionTestKit.RequireFFmpeg();
        var go = Path.Combine(_directory, "go");
        var window = SessionTestKit.OpenWindow(_directory, _processes, ResizingWindowScript, "-GoFile", go, "-PhaseMs", Text(PhaseMs));
        var output = Path.Combine(_directory, "resize.mp4");
        var video = new VideoSettings(VideoCodec.H264, 1920, 1080, 60, 20_000, RateControl.Cbr);
        var seconds = (4 * PhaseMs / 1000.0) + 1;

        var outcome = await SessionTestKit.CaptureAsync(
            window, video, output, new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output },
            new VideoEncoderCatalog(NullLoggerFactory.Instance), NullLoggerFactory.Instance, TimeSpan.FromSeconds(seconds),
            () => File.WriteAllText(go, ""));
        Assert.Null(outcome.Failure);
        Assert.Equal("test over", outcome.Reason);

        var stream = SessionTestKit.VideoStream(output);
        Assert.Equal(("h264", 1920, 1080), (stream.Codec, stream.Width, stream.Height));
        SessionTestKit.AssertDecodesCleanly(output);
        AssertUniformPts(output, 60, seconds);

        // The 4:3 phase (1024×768 with its border, last), past its first half second: black bars
        // left and right, the window's colour (40, 200, 90 → Y ≈ 136, V ≈ 66) in the middle.
        var fourByThree = (4 * PhaseMs / 1000.0) - 1.5;
        Assert.All(Frames(output, "crop=200:1080:0:0", fourByThree), f => Assert.InRange(f.Y, 0, 25));
        Assert.All(Frames(output, "crop=200:1080:1720:0", fourByThree), f => Assert.InRange(f.Y, 0, 25));
        Assert.All(Frames(output, "crop=400:400:760:500", fourByThree), f =>
        {
            Assert.InRange(f.Y, 120, 150);
            Assert.InRange(f.V, 50, 85);
        });

        // The first phase, 1280×720, is 16:9: scaled up to fill the frame, no bars.
        Assert.All(Frames(output, "crop=200:1080:0:0", 0.5, (PhaseMs / 1000.0) - 0.2), f => Assert.InRange(f.Y, 120, 150));

        SessionTestKit.AssertPlaysInVlc(output, _directory, _processes);
    }

    [Theory(Explicit = true)]
    [Trait("Category", "Heavy")]
    [InlineData(30)]
    [InlineData(60)]
    public async Task AStaticWindowStillGetsTheConfiguredFrameRate(int fps)
    {
        SessionTestKit.RequireFFmpeg();
        var window = SessionTestKit.OpenWindow(_directory, _processes, StaticWindowScript, "-Width", "640", "-Height", "480");
        var output = Path.Combine(_directory, $"static-{fps}.mp4");
        var video = new VideoSettings(VideoCodec.H264, 640, 480, fps, 8_000, RateControl.Cbr);

        var outcome = await SessionTestKit.CaptureAsync(
            window, video, output, new CaptureSessionOptions { OutputFolder = _directory, OutputPath = output },
            new VideoEncoderCatalog(NullLoggerFactory.Instance), NullLoggerFactory.Instance, TimeSpan.FromSeconds(5));
        Assert.Null(outcome.Failure);

        AssertUniformPts(output, fps, 5);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void AStatsRunOnAFastWindowMeetsTheLatencyGcAndAllocationBounds()
    {
        SessionTestKit.RequireFFmpeg();
        var window = SessionTestKit.OpenWindow(_directory, _processes, FastWindowScript, "-Width", "1920", "-Height", "1080");
        RealTimeAttempts.Run(attempt => FastWindowStatsAttempt(window, attempt));
    }

    private void FastWindowStatsAttempt(WindowInfo window, int attempt)
    {
        var output = Path.Combine(_directory, $"stats-{attempt}.mp4");
        var statsPath = Path.Combine(_directory, $"stats-{attempt}.json");

        // 20 s: the 10 s warm-up, then the steady state the bounds apply to.
        var start = new ProcessStartInfo(Path.Combine(EngineBinaries.Folder, "ReplayBuffer.Engine.exe"))
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[]
        {
            "debug-capture", "--hwnd", "0x" + window.Handle.ToString("X", CultureInfo.InvariantCulture), "--seconds", "20",
            "--size", "1920x1080", "--fps", "60", "--output", output, "--stats", statsPath,
        })
        {
            start.ArgumentList.Add(arg);
        }

        var engine = Process.Start(start)!;
        _processes.Add(engine);
        var stopped = engine.WaitForExit(TimeSpan.FromSeconds(60));
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
        var video = root.GetProperty("video");
        var latency = root.GetProperty("latency");
        var steady = root.GetProperty("resources").GetProperty("steady");
        var gc = steady.GetProperty("gc");
        var fresh = video.GetProperty("freshSlots").GetInt64();
        var slots = fresh + video.GetProperty("duplicates").GetInt64();
        Output?.WriteLine(
            $"latency p50 {latency.GetProperty("p50Ms").GetDouble()} ms, p99 {latency.GetProperty("p99Ms").GetDouble()} ms; "
            + $"steady {steady.GetProperty("allocatedBytesPerSecond").GetDouble():0} B/s, GC {gc.GetProperty("gen0")}/{gc.GetProperty("gen1")}/{gc.GetProperty("gen2")}; "
            + $"fresh {fresh} of {slots}; arrived {video.GetProperty("framesArrived").GetInt64()}, "
            + $"held replaced {video.GetProperty("heldReplaced").GetInt64()}, skipped {video.GetProperty("skippedSlots").GetInt64()}, "
            + $"queue drops {video.GetProperty("queueDrops").GetInt64()}");

        Assert.Equal(2, root.GetProperty("audio").GetArrayLength());

        // The timing bounds, which a neighbouring GPU test can make this attempt miss.
        var p99 = latency.GetProperty("p99Ms").GetDouble();
        RealTimeAttempts.Bound(p99 < 1000.0 / 60, $"latency p99 {p99} ms");
        var collections = (gc.GetProperty("gen0").GetInt32(), gc.GetProperty("gen1").GetInt32(), gc.GetProperty("gen2").GetInt32());
        RealTimeAttempts.Bound(collections == (0, 0, 0), $"steady-state GC {collections}");
        var bytesPerSecond = steady.GetProperty("allocatedBytesPerSecond").GetDouble();
        RealTimeAttempts.Bound(bytesPerSecond < 2048, $"{bytesPerSecond:0} B/s allocated in steady state");
        RealTimeAttempts.Bound(fresh >= 0.95 * slots, $"{fresh} of {slots} slots fresh");
    }

    /// <summary>
    /// Every PTS step is exactly one slot, and the count matches the session: <paramref name="seconds"/>
    /// of slots, ±2 %.
    /// </summary>
    private static void AssertUniformPts(string file, int fps, double seconds)
    {
        var (pts, num, den) = SessionTestKit.VideoPtsTicks(file);
        Assert.Equal(0, den % (fps * num));
        var step = den / (fps * num);
        for (var i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] - pts[i - 1] == step, $"PTS step {pts[i] - pts[i - 1]} at packet {i}, not {step}");
        }

        Output?.WriteLine($"{Path.GetFileName(file)}: {pts.Count} frames at {fps} fps over {seconds} s");
        Assert.InRange(pts.Count, fps * seconds * 0.98, fps * seconds * 1.02);
    }

    private static List<(double Time, double Y, double U, double V)> Frames(string file, string crop, double from, double to = double.MaxValue)
    {
        var frames = SessionTestKit.FrameStats(file, crop).Where(f => f.Time >= from && f.Time <= to).ToList();
        Assert.NotEmpty(frames);
        return frames;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
