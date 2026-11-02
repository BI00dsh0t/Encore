using System.Text.Json;
using ReplayBuffer.Platform.Audio;

namespace ReplayBuffer.Bench.Tests;

/// <summary>The matrix's failure rows for <c>drift</c> and <c>fps</c>, through the subcommands themselves.</summary>
[Collection(nameof(BenchProcessCollection))]
public sealed class CommandFailureTests : IDisposable
{
    /// <summary>A stand-in engine: it refuses <c>debug-capture</c> on stderr and exits 1.</summary>
    private static readonly string FailingEngine = Path.Combine(Environment.SystemDirectory, "whoami.exe");

    private readonly BenchRun _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public void AnEngineThatExitsEarlyLeavesItsStderrInDriftJsonAndExitsOne()
    {
        Assert.SkipUnless(HasDefaultDevices(), "no default output and input device");
        Assert.SkipUnless(FfmpegTools.Find(null, BenchContext.FindRepoRoot(null)) is not null, "native/ffmpeg is not fetched");

        var code = DriftBenchmark.Run(_run.Context("drift", "--minutes", "1", "--engine", FailingEngine));

        Assert.Equal(1, code);
        var drift = JsonSerializer.Deserialize<DriftReport>(File.ReadAllText(_run.PathOf(DriftBenchmark.FileName)), BenchJson.Options)!;
        Assert.Equal("failed", drift.Verdict);
        Assert.Equal(1, drift.EngineExit);

        // The stand-in's own stderr, whatever the Windows display language words it as.
        Assert.False(string.IsNullOrWhiteSpace(drift.EngineError));
        Assert.Equal(File.ReadAllText(_run.PathOf("drift-engine.err.log")).Trim(), drift.EngineError);
        Assert.Null(drift.Av);

        var md = Report.Markdown(Report.Generate(_run.Folder));
        Assert.Contains("**The engine failed** (exit 1)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingGameIsTheScriptsMessageAndExitOne()
    {
        Assert.SkipUnless(Pwsh() is not null, "pwsh.exe is not on the PATH");
        var errors = new StringWriter();

        var code = FpsBenchmark.Run(
            _run.Context("fps", "--game", "ReplayBufferNoSuchGame", "--seconds", "10", "--passes", "1", "--settle", "0",
                "--presentmon", FailingEngine, "--engine", FailingEngine),
            errors);

        Assert.Equal(1, code);
        Assert.Contains("The game process 'ReplayBufferNoSuchGame' is not running", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("measure-path-a.ps1 exited with 1.", errors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(_run.PathOf(FpsBenchmark.FileName)));
    }

    [Fact]
    public void ALoadThatExitsDuringTheRunIsExitOneWithItsStderr()
    {
        Assert.SkipWhen(GpuLoadProcess.Running().Count > 0, $"{GpuLoadProcess.ProcessName} is running");

        // A load that says ready, then gives up; and a script that outlives it, then fails.
        var load = Stub("load.cmd", """
            @echo off
            echo stub load gave up 1>&2
            echo ready {"hwnd":"0x1","width":320,"height":180,"targetFps":80,"calibratedFps":80,"iterations":1,"adapter":"stub"}
            exit /b 7
            """);
        var pwsh = Stub("pwsh.cmd", """
            @echo off
            ping -n 3 127.0.0.1 >nul
            exit /b 1
            """);
        var errors = new StringWriter();

        var code = FpsBenchmark.Run(
            _run.Context("fps", "--seconds", "10", "--passes", "1", "--settle", "0",
                "--pwsh", pwsh, "--presentmon", FailingEngine, "--engine", FailingEngine, "--load-exe", load),
            errors);

        Assert.Equal(1, code);
        Assert.Contains("The GPU load exited during the run (exit 7: stub load gave up).", errors.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(_run.PathOf(FpsBenchmark.FileName)));
    }

    [Fact]
    public void AMissingPresentMonIsRefusedWithTheScriptsMessage()
    {
        var missing = Path.Combine(_run.Folder, "PresentMon-9.9.9-x64.exe");

        var failure = Assert.Throws<BenchFailure>(() => _run.Context("fps", "--presentmon", missing).ResolvePresentMon());

        // measure-path-a.ps1's own words for a -PresentMon that isn't there.
        Assert.Equal($"PresentMon not found at '{missing}'.", failure.Message);
    }

    private string Stub(string name, string script)
    {
        var path = _run.PathOf(name);
        File.WriteAllText(path, script.ReplaceLineEndings("\r\n"));
        return path;
    }

    private static bool HasDefaultDevices()
    {
        try
        {
            AudioEndpoints.Default(AudioFlow.Output);
            AudioEndpoints.Default(AudioFlow.Input);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? Pwsh()
    {
        try
        {
            return BenchContext.Create(BenchOptions.Parse(["report", "--out", Path.GetTempPath()])).ResolvePwsh();
        }
        catch (BenchFailure)
        {
            return null;
        }
    }
}
