using System.Diagnostics;

namespace ReplayBuffer.Engine.Tests;

/// <summary>Runs the built engine executable and checks its exit codes and messages.</summary>
public sealed class EngineProcessTests
{
    private static string EngineFolder => EngineBinaries.Folder;

    private static (int ExitCode, string Stdout, string Stderr) RunEngine(string folder, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(folder, "ReplayBuffer.Engine.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the engine did not exit");
        return (process.ExitCode, stdout.Result, stderr);
    }

    [Fact]
    public void AnUnknownWindowListsCandidatesAndFailsWithWindowNotFound()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(EngineFolder, "avcodec-63.dll")), "native/ffmpeg is not fetched");

        var (exitCode, _, stderr) = RunEngine(EngineFolder, "debug-capture", "--window", "no-such-window-" + Guid.NewGuid().ToString("N"));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("error window-not-found", stderr);
        Assert.Contains("Candidates:", stderr);
    }

    [Fact]
    public void MissingFFmpegNamesTheFetchScriptAndFailsWithFFmpegMissing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rb-engine-noffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(EngineFolder))
            {
                var name = Path.GetFileName(file);
                var isFFmpeg = name.StartsWith("av", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sw", StringComparison.OrdinalIgnoreCase);
                if (!(isFFmpeg && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    File.Copy(file, Path.Combine(folder, name));
                }
            }

            var (exitCode, _, stderr) = RunEngine(folder, "debug-capture", "--window", "anything");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("error ffmpeg-missing", stderr);
            Assert.Contains("tools/fetch-ffmpeg.ps1", stderr);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AMalformedCommandLineFailsWithInvalidArguments()
    {
        var (exitCode, _, stderr) = RunEngine(EngineFolder, "debug-capture");

        Assert.Equal(2, exitCode);
        Assert.Contains("error invalid-arguments", stderr);
    }

    [Fact]
    public void AGpuPriorityOutsideTheRangeFailsWithInvalidArguments()
    {
        var (exitCode, _, stderr) = RunEngine(EngineFolder, "debug-capture", "--window", "a", "--gpu-priority", "9");

        Assert.Equal(2, exitCode);
        Assert.Contains("error invalid-arguments", stderr);
        Assert.Contains("--gpu-priority", stderr);
    }

    [Fact]
    public void DebugListPrintsWindowsAndBothDeviceKinds()
    {
        var (exitCode, stdout, _) = RunEngine(EngineFolder, "debug-list");

        Assert.Equal(0, exitCode);
        Assert.Contains("Windows:", stdout);
        Assert.Contains("Output Devices (loopback):", stdout);
        Assert.Contains("Input Devices:", stdout);

        // Every endpoint line: marker, flow, friendly name, parent name, then the endpoint id.
        var endpoints = stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .SkipWhile(l => l != "Output Devices (loopback):")
            .Where(l => l.StartsWith("  output", StringComparison.Ordinal) || l.StartsWith("* output", StringComparison.Ordinal)
                || l.StartsWith("  input", StringComparison.Ordinal) || l.StartsWith("* input", StringComparison.Ordinal))
            .ToList();
        var present = Platform.Audio.AudioEndpoints.List(Platform.Audio.AudioFlow.Output).Count
            + Platform.Audio.AudioEndpoints.List(Platform.Audio.AudioFlow.Input).Count;
        Assert.Equal(present, endpoints.Count);
        Assert.All(endpoints, l => Assert.Matches(@"^[* ] (output|input) +\S.*  parent: .+  \[\{.+\}\.\{.+\}\]$", l));
    }

    [Fact]
    public void DebugCaptureRefusesANonTestInstance()
    {
        var (exitCode, _, stderr) = RunEngine(EngineFolder, "debug-capture", "--window", "a", "--instance", "Foo");

        Assert.Equal(2, exitCode);
        Assert.Contains("error invalid-arguments", stderr);
    }

    [Fact]
    public void DebugCaptureRefusesTheTrackLayoutWithADeviceFlag()
    {
        var (exitCode, _, stderr) = RunEngine(EngineFolder, "debug-capture", "--window", "a", "--track-layout", "--output-device", "x");

        Assert.Equal(2, exitCode);
        Assert.Contains("error invalid-arguments", stderr);
    }
}
