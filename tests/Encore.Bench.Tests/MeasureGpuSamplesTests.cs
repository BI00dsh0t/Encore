using System.Diagnostics;
using System.Globalization;

namespace Encore.Bench.Tests;

/// <summary>A bug that bit: a process reports three identical VideoEncode instances, which the sampler used to sum.</summary>
public sealed class MeasureGpuSamplesTests
{
    [Fact]
    public async Task VideoEncodeIsCountedOncePerProcessAndSummedOverProcesses()
    {
        // Two 1 s samples of two engine processes, each with three VideoEncode engines reporting the same value.
        const string script = """
            $ErrorActionPreference = 'Stop'
            . (Join-Path $env:RB_TOOLS 'measure-gpu.ps1')
            $t0 = [datetime] '2026-11-04T22:30:00'
            $samples = foreach ($s in 0, 1) {
                foreach ($p in @(@{ Pid = 100; Enc = 10 + $s * 2 }, @{ Pid = 200; Enc = 20 })) {
                    foreach ($eng in 6..8) {
                        [pscustomobject]@{ Time = $t0.AddSeconds($s); Value = $p.Enc
                            Instance = "pid_$($p.Pid)_luid_0x00000000_0x0000D1A5_phys_0_eng_$($eng)_engtype_VideoEncode" }
                    }
                    [pscustomobject]@{ Time = $t0.AddSeconds($s); Value = 2.5
                        Instance = "pid_$($p.Pid)_luid_0x00000000_0x0000D1A5_phys_0_eng_0_engtype_3D" }
                }
            }
            $gpu = Measure-GpuSamples $samples
            $v = $gpu.ByType['VideoEncode']; $d = $gpu.ByType['3D']
            [Console]::Out.Write(('{0};{1};{2};{3}' -f $v.MeanPercent, $v.MaxPercent, $v.Samples, $d.MeanPercent).Replace(',', '.'))
            """;

        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        start.Environment["RB_TOOLS"] = Path.Combine(BenchContext.FindRepoRoot(null), "tools");

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"pwsh exited {process.ExitCode}: {await stderr}");

        var fields = output.Trim().Split(';').Select(f => double.Parse(f, CultureInfo.InvariantCulture)).ToArray();

        // Per sample: 10 + 20, then 12 + 20 (one instance each, summed over processes), not 3x that.
        Assert.Equal(31.0, fields[0], 3);
        Assert.Equal(32.0, fields[1], 3);
        Assert.Equal(2.0, fields[2]);
        Assert.Equal(5.0, fields[3], 3);
    }
}
