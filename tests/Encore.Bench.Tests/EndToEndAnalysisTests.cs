using System.Diagnostics;
using System.Globalization;

namespace Encore.Bench.Tests;

/// <summary>The whole analysis on a file ffmpeg makes with a known drift between flashes and bursts. Skipped without ffmpeg.</summary>
public sealed class EndToEndAnalysisTests : IDisposable
{
    private const long Frequency = 10_000_000;
    private const long Anchor = 7_000_000_000;
    private const int Seconds = 120;

    private readonly string _folder = Directory.CreateTempSubdirectory("bench-e2e-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AFiveMillisecondDriftIsReported()
    {
        var report = Analyze(driftMs: 5);

        var av = report.Av!;
        Assert.True(av.FlashMatchedFraction >= 0.9, $"flashes matched {av.FlashMatchedFraction}");
        Assert.True(av.BurstMatchedFraction >= 0.9, $"bursts matched {av.BurstMatchedFraction}");
        Assert.InRange(av.DriftMs!.Value, 3.5, 6.5);
        Assert.InRange(av.RegressionDriftMs!.Value, 3.5, 6.5);
        Assert.Equal(DriftMath.Pass, av.Verdict);
        Assert.Single(report.Tracks);
    }

    private static FfmpegTools Tools()
    {
        string? repo = null;
        try
        {
            repo = BenchContext.FindRepoRoot(null);
        }
        catch (BenchFailure)
        {
            // Only Bench's own folder, then.
        }

        var tools = FfmpegTools.Find(null, repo);
        Assert.SkipUnless(tools is not null, "native/ffmpeg is not fetched");
        return tools!;
    }

    private DriftReport Analyze(double driftMs)
    {
        var tools = Tools();
        var file = Path.Combine(_folder, $"drift-{driftMs.ToString(CultureInfo.InvariantCulture)}.mp4");
        Make(tools, file, driftMs / 1000);

        var flashes = Enumerable.Range(0, Seconds).Select(k => Anchor + (k * Frequency)).ToList();
        var bursts = flashes.Select(q => q + (2 * Frequency / 10)).ToList();
        var log = new MarkerLog(Frequency, Anchor, "0x0", "Game", "out", "Mic", "in", flashes, bursts);
        var settings = new CaptureSettings(320, 180, 60, 2_000, null, null);
        var warnings = new List<string>();

        // 10 s buckets: over 2 minutes, a minute's median would hide half of a linear drift.
        return DriftBenchmark.Analyze(tools, file, null, log, settings, 2, "lavfi", 0, 10, 0, OnsetDetector.DefaultBurstDbfs, warnings);
    }

    /// <summary>Burst k starts at 0.2 + k (1 + drift / 119) s, so burst 119 is the whole drift late; a quiet tone plays between.</summary>
    private static void Make(FfmpegTools tools, string file, double driftSeconds)
    {
        var stretch = (1 + (driftSeconds / (Seconds - 1))).ToString("R", CultureInfo.InvariantCulture);
        var u = $"((t-0.2)/{stretch})";
        var gate = $"(gte({u},0)*lt(mod({u},1),0.05))";
        var channel = $"0.0316*sin(2*PI*3000*t)*{gate}+0.01*sin(2*PI*440*t)*(1-{gate})";
        string[] args =
        [
            "-v", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s=320x180:r=60:d={Seconds},drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='lt(mod(n,60),3)'",
            "-f", "lavfi", "-i", $"aevalsrc=exprs='{channel}|{channel}':s=48000:d={Seconds}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "60",
            "-c:a", "aac", "-b:a", "160k",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            file,
        ];
        var start = new ProcessStartInfo(tools.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ffmpeg failed: {error}");
    }
}
