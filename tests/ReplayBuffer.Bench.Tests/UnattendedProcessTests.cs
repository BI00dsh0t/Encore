using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ReplayBuffer.Bench.Tests;

/// <summary>
/// The unattended pieces against real processes: the GPU load (skipped without a hardware GPU), an injected
/// Hotkey on the built engine, and the scripted players on a clip ffmpeg makes (skipped without ffmpeg, or
/// without Chrome for its own check).
/// </summary>
[Collection(nameof(BenchProcessCollection))]
public sealed class UnattendedProcessTests : IDisposable
{
    private readonly BenchRun _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public void TheGpuLoadStartsCalibratesAndStops()
    {
        var exe = LoadExe();

        using var load = Start(exe);
        var info = load.Info;
        var id = load.Id;

        Assert.Equal(GpuLoadInfo.Synthetic, info.Kind);
        Assert.Equal(GpuLoadProcess.ProcessName, info.Process);
        Assert.Equal((320, 180), (info.Width, info.Height));
        Assert.Equal(80, info.TargetFps);
        Assert.True(info.CalibratedFps > 0);
        Assert.True(info.Iterations >= 1);
        Assert.False(string.IsNullOrEmpty(info.Adapter));

        // The script finds it as -Game by its name and main window.
        var found = Process.GetProcessesByName(GpuLoadProcess.ProcessName).Where(p => p.Id == id).ToList();
        Assert.Single(found);
        Assert.Equal(load.Hwnd, found[0].MainWindowHandle);

        load.Dispose();
        Assert.True(load.HasExited);
        Assert.DoesNotContain(Process.GetProcessesByName(GpuLoadProcess.ProcessName), p => p.Id == id);
    }

    [Fact]
    public async Task TheGpuLoadExitsWhenItsParentDoes()
    {
        var exe = LoadExe();
        using var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 6 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        _ = parent.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--size", "320x180", "--warmup", "1", "--parent", parent.Id.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(arg);
        }

        using var load = Process.Start(start)!;
        _ = load.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = load.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(parent.WaitForExit(TimeSpan.FromSeconds(20)), "ping did not finish");
            var exited = load.WaitForExit(TimeSpan.FromSeconds(15));
            Assert.SkipWhen(exited && load.ExitCode == 3, "no hardware GPU");
            Assert.True(exited, "the load outlived its parent");
            Assert.True(load.ExitCode == 0, $"exit {load.ExitCode}: {await stderr}");
        }
        finally
        {
            if (!load.HasExited)
            {
                load.Kill();
            }
        }
    }

    [Fact]
    public void AnInjectedHotkeyIsTimed()
    {
        var engine = BenchRun.EngineExe;
        Assert.SkipUnless(engine is not null, "the engine is not built beside these tests");

        // Ctrl+Alt+Shift+F16: no other test in the solution uses it.
        var code = HotkeyBenchmark.Run(_run.Context("hotkey", "--key", "ctrl+alt+shift+f16", "--presses", "3", "--timeout", "10", "--inject", "--engine", engine!));

        var report = JsonSerializer.Deserialize<HotkeyReport>(File.ReadAllText(_run.PathOf(HotkeyBenchmark.FileName)), BenchJson.Options)!;
        Assert.SkipWhen(code == 1 && report.Error?.StartsWith("SendInput", StringComparison.Ordinal) == true, "input is blocked on this desktop: " + report.Error);
        Assert.Equal(0, code);
        Assert.True(report.Injected);
        Assert.Equal(3, report.Presses);
        Assert.All(report.Timings, t => Assert.True(t.Injected));
        Assert.Equal(DriftMath.Pass, report.Verdict);
        Assert.Contains("**Presses injected**", Report.Markdown(Report.Generate(_run.Folder)), StringComparison.Ordinal);
    }

    [Fact]
    public void ChromePlaysTheClipHeadless()
    {
        var chrome = PlayerScripts.FindChrome(null);
        Assert.SkipUnless(chrome is not null, "Chrome is not installed");
        var clip = Clip();

        var result = PlayerScripts.RunChrome(chrome!, clip, _run.Folder, 3, TimeSpan.FromSeconds(60));

        Assert.True(result.Plays == true, $"Chrome: {result}");
        Assert.Equal((320, 180), (result.Width, result.Height));
        Assert.True(result.AudioTracks == 2, $"Chrome: {result}");
        Assert.False(Directory.Exists(_run.PathOf("playback-chrome-profile")));
    }

    [Fact]
    public void WindowsMediaPlayerPlaysTheClip()
    {
        var clip = Clip();

        var result = PlayerScripts.RunWindowsMediaPlayer(clip, _run.Folder, 3, TimeSpan.FromSeconds(60));

        Assert.SkipWhen(result.Plays is null && result.Error?.Contains("WMPlayer.OCX is not available", StringComparison.Ordinal) == true, "Windows Media Player is not installed");
        Assert.True(result.Plays == true, $"Windows Media Player: {result}");
        Assert.Equal((320, 180), (result.Width, result.Height));
        Assert.True(result.AudioTracks == 2, $"Windows Media Player: {result}");
    }

    private static string LoadExe()
    {
        try
        {
            return GpuLoadProcess.Resolve(BenchContext.Create(BenchOptions.Parse(["report", "--out", Path.GetTempPath()])));
        }
        catch (BenchFailure e)
        {
            Assert.Skip(e.Message);
            throw;
        }
    }

    private static GpuLoadProcess Start(string exe)
    {
        try
        {
            return GpuLoadProcess.Start(exe, 80, "320x180", 2, TimeSpan.FromSeconds(60));
        }
        catch (BenchFailure e) when (e.Message.Contains("no hardware GPU", StringComparison.Ordinal))
        {
            Assert.Skip(e.Message);
            throw;
        }
    }

    /// <summary>A 10 s fragmented MP4 like the engine's: H.264 and two AAC tracks.</summary>
    private string Clip()
    {
        var tools = FfmpegTools.Find(null, BenchContext.FindRepoRoot(null));
        Assert.SkipUnless(tools is not null, "native/ffmpeg is not fetched");
        var file = _run.PathOf("clip.mp4");
        string[] args =
        [
            "-v", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=320x180:r=60:d=10",
            "-f", "lavfi", "-i", "sine=f=440:r=48000:d=10",
            "-f", "lavfi", "-i", "sine=f=880:r=48000:d=10",
            "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "60",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            file,
        ];
        var start = new ProcessStartInfo(tools!.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ffmpeg failed: {error}");
        return file;
    }
}
