using System.Diagnostics;
using System.Globalization;

namespace ReplayBuffer.Bench.Tests;

/// <summary>
/// <c>tools/measure-path-a.ps1</c>'s sweep of leftover <c>ReplayBuffer-PathA-&lt;pid&gt;</c> ETW sessions,
/// against the real script and real sessions (skipped when this account may not start one): a dead
/// run's session is stopped and named, a live run's is left alone.
/// </summary>
[Collection(nameof(BenchProcessCollection))]
public sealed class MeasureScriptSessionTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-etw-").FullName;
    private readonly List<string> _sessions = [];

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            Logman("stop", session, "-ets");
        }

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void TheScriptStopsADeadRunsSessionAndLeavesALiveOneAlone()
    {
        var dead = "ReplayBuffer-PathA-" + DeadPid().ToString(CultureInfo.InvariantCulture);
        var live = "ReplayBuffer-PathA-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        StartSession(dead);
        StartSession(live);
        Assert.True(IsRunning(dead));
        Assert.True(IsRunning(live));

        var repo = BenchContext.FindRepoRoot(null);
        var (code, output) = Run("pwsh", _folder,
            "-NoProfile", "-File", Path.Combine(repo, "tools", "measure-path-a.ps1"),
            "-Game", "NoSuchGame", "-PresentMon", Path.Combine(_folder, "missing", "PresentMon.exe"));
        TestContext.Current.TestOutputHelper?.WriteLine(output);

        // A setup check fails (elevated terminal, or PresentMon missing), after the sweep.
        Assert.Equal(1, code);
        Assert.False(IsRunning(dead), $"{dead} still runs");
        Assert.Contains(dead, output, StringComparison.Ordinal);
        Assert.True(IsRunning(live), $"{live} was stopped");
    }

    /// <summary>
    /// A pid no process answers to. <c>OpenProcess</c> ignores a pid's low two bits, so an odd pid can
    /// still name a running process; the lookup here is the script's, so the two agree.
    /// </summary>
    private static int DeadPid()
    {
        for (var pid = 999_999; ; pid -= 2)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
    }

    private void StartSession(string name)
    {
        // Stop a same-named leftover first, so the start below is this test's.
        Logman("stop", name, "-ets");
        var (code, output) = Logman("start", name, "-p", "{" + Guid.NewGuid() + "}",
            "-o", Path.Combine(_folder, name + ".etl"), "-ets");
        Assert.SkipUnless(code == 0, $"logman may not start an ETW session here: {output.Trim()}");
        _sessions.Add(name);
    }

    private static bool IsRunning(string name) => Logman("query", name, "-ets").Code == 0;

    private static (int Code, string Output) Logman(params string[] args) =>
        Run(Path.Combine(Environment.SystemDirectory, "logman.exe"), null, args);

    private static (int Code, string Output) Run(string exe, string? workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
