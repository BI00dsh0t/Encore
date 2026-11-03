using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ReplayBuffer.Bench.Tests;

/// <summary>
/// <c>bench tracks --reanalyze</c> on a file ffmpeg makes from lavfi: 20 s of H.264 and four AAC streams titled
/// Everything, Game, Chat and Mic (only the first default) that mimic the layout. Game has a 3 kHz burst at
/// −6 dBFS each second from 0.25 s over a 440 Hz tone 10 dB down, Chat the same from 0.75 s, Everything their
/// sum (limited to full scale) and Mic silence. Skipped without ffmpeg.
/// </summary>
public sealed class TracksEndToEndTests : IDisposable
{
    private const int Seconds = 20;
    private const double ToneDbfs = -6;

    private readonly BenchRun _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public void AGoodFilePassesItsReanalysis()
    {
        var tools = Tools();
        Make(tools, _run.PathOf(TracksCheck.ClipName), gameDelaySeconds: 0);

        var (exit, report) = Reanalyze();

        Assert.True(exit == 0, string.Join(Environment.NewLine, report.Checks.Select(c => $"{c.Check}: {c.Figure} ({c.Mark})")));
        Assert.Equal(TrackAnalysis.PassVerdict, report.Verdict);
        Assert.Equal(["Everything", "Game", "Chat", "Mic"], report.Tracks.Select(t => t.Title));
        Assert.Equal([true, false, false, false], report.Tracks.Select(t => t.Default));
        Assert.Equal(TrackAnalysis.NotChecked, report.Checks.Single(c => c.Check == "vlc").Mark);
        Assert.InRange(report.Sync!.MaxMs!.Value, 0, 0.5);
        Assert.InRange(report.Tracks[TrackAnalysis.Game].BurstLevelDbfs!.Value, ToneDbfs - 0.5, ToneDbfs + 0.5);
        Assert.InRange(report.Tracks[TrackAnalysis.Game].Onsets!.Value, 19, 20);
        Assert.InRange(report.Tracks[TrackAnalysis.Everything].Onsets!.Value, 38, 40);
        Assert.Equal(report.Tracks[TrackAnalysis.Game].ExpectedOnsets + report.Tracks[TrackAnalysis.Chat].ExpectedOnsets, report.Tracks[TrackAnalysis.Everything].ExpectedOnsets);
        Assert.InRange(report.Tracks[TrackAnalysis.Everything].P999!.Value, 0.5, 1.05);

        var md = Report.Markdown(Report.Generate(_run.Folder));
        Assert.Contains("## Named Tracks (R3, R7)", md, StringComparison.Ordinal);
        Assert.Contains("| titles | Everything, Game, Chat, Mic | Everything, Game, Chat, Mic | pass |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void GameFiveMillisecondsLateFailsSync()
    {
        var tools = Tools();
        Make(tools, _run.PathOf(TracksCheck.ClipName), gameDelaySeconds: 0.005);

        var (exit, report) = Reanalyze();

        Assert.Equal(1, exit);
        Assert.Equal(TrackAnalysis.FailVerdict, report.Verdict);
        Assert.Equal(DriftMath.Miss, report.Checks.Single(c => c.Check == "sync").Mark);
        Assert.Equal(DriftMath.Pass, report.Checks.Single(c => c.Check == "titles").Mark);
    }

    [Fact]
    public void NamesOnlyInTheHandlerNameFailTitles()
    {
        var tools = Tools();
        Make(tools, _run.PathOf(TracksCheck.ClipName), gameDelaySeconds: 0, titleTag: "handler_name");

        var (exit, report) = Reanalyze();

        Assert.Equal(1, exit);
        Assert.Equal(DriftMath.Miss, report.Checks.Single(c => c.Check == "titles").Mark);
        Assert.All(report.Tracks, t => Assert.Null(t.Title));
    }

    [Fact]
    public void VlcListsTheTitlesInOrder()
    {
        var tools = Tools();
        var vlc = PlayerScripts.FindVlc(null);
        Assert.SkipUnless(vlc is not null, "VLC is not installed");
        var clip = _run.PathOf(TracksCheck.ClipName);
        Make(tools, clip, gameDelaySeconds: 0);

        var listing = PlayerScripts.RunVlc(vlc!, clip, 4, TimeSpan.FromSeconds(30));

        Assert.True(listing.Error is null, listing.Error);
        Assert.Equal(["Everything", "Game", "Chat", "Mic"], listing.Labels!);
    }

    [Fact]
    public void AReanalysisKeepsTheCapturesWarnings()
    {
        var tools = Tools();
        Make(tools, _run.PathOf(TracksCheck.ClipName), gameDelaySeconds: 0);
        const string Warning = "B's bursts come 0.2 s after A's, not about 0.5 s.";

        var (exit, report) = Reanalyze(Previous(1, null, Warning));

        Assert.Equal(0, exit);
        Assert.Equal(TrackAnalysis.PassVerdict, report.Verdict);
        Assert.Contains(Warning, report.Warnings);
    }

    [Fact]
    public void AReanalysisOfAFailedCaptureStaysFailed()
    {
        var tools = Tools();
        Make(tools, _run.PathOf(TracksCheck.ClipName), gameDelaySeconds: 0);
        const string Warning = "Could not delete the test instance's folder C:\\x.";

        var (exit, report) = Reanalyze(Previous(1, "error encode-failed: the encoder stopped", Warning));

        Assert.Equal(1, exit);
        Assert.Equal(TrackAnalysis.FailedVerdict, report.Verdict);
        Assert.Equal(1, report.EngineExit);
        Assert.Equal("error encode-failed: the encoder stopped", report.EngineError);
        Assert.Contains(Warning, report.Warnings);
    }

    /// <summary>An earlier tracks.json for the run: its engine exit and error, and one capture-time warning.</summary>
    private static TracksReport Previous(int exit, string? error, string warning) => new(
        DateTimeOffset.Now, Seconds / 60.0, "engine.exe", new CaptureSettings(320, 180, 60, 2_000, null, null), ToneDbfs,
        "A", "B", "Mic", "tracks.mp4", exit, error, null, null, [], null, [], null,
        error is null ? TrackAnalysis.PassVerdict : TrackAnalysis.FailedVerdict, [warning]);

    /// <param name="previous">An earlier <c>tracks.json</c> to write first, as a capture run leaves it.</param>
    private (int Exit, TracksReport Report) Reanalyze(TracksReport? previous = null)
    {
        var bursts = Enumerable.Range(0, Seconds).Select(k => (long)k * 10_000_000).ToList();
        var log = new TracksMarkerLog(
            10_000_000, 0, "ReplayBuffer-Test-bench-00000000", "0x0", "A", "a", "B", "b", "Mic", "m", ToneDbfs, Seconds / 60.0,
            new CaptureSettings(320, 180, 60, 2_000, null, null), bursts, [.. bursts.Select(q => q + 5_000_000)]);
        File.WriteAllText(_run.PathOf(TracksCheck.MarkersName), JsonSerializer.Serialize(log, BenchJson.Options));
        if (previous is not null)
        {
            BenchJson.Write(_run.PathOf(TracksCheck.FileName), previous);
        }

        // A --vlc that isn't there leaves VLC not-checked, whatever is installed.
        var exit = TracksCheck.Run(_run.Context("tracks", "--reanalyze", "--vlc", _run.PathOf("no-vlc.exe")));
        var report = JsonSerializer.Deserialize<TracksReport>(File.ReadAllText(_run.PathOf(TracksCheck.FileName)), BenchJson.Options)!;
        Assert.Equal(-6, report.ToneDbfs);
        if (report.EngineError is null)
        {
            Assert.Equal(0.5, report.BurstPhaseSeconds);
        }

        return (exit, report);
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

    /// <summary>A source's signal: a 3 kHz burst at −6 dBFS for 50 ms from <paramref name="first"/> s each second, else 440 Hz 10 dB down.</summary>
    private static string Source(double first, double delay)
    {
        var t = delay == 0 ? "t" : $"(t-{delay.ToString("R", CultureInfo.InvariantCulture)})";
        var gate = $"lt(mod({t}+{(1 - first).ToString("R", CultureInfo.InvariantCulture)},1),0.05)";
        var burst = Math.Pow(10, ToneDbfs / 20).ToString("R", CultureInfo.InvariantCulture);
        var tone = Math.Pow(10, (ToneDbfs - 10) / 20).ToString("R", CultureInfo.InvariantCulture);
        return $"({burst}*sin(2*PI*3000*{t})*{gate}+{tone}*sin(2*PI*440*{t})*(1-{gate}))";
    }

    /// <param name="titleTag">The stream tag the names go in: <c>title</c>, as the engine writes it, or <c>handler_name</c> alone.</param>
    private static void Make(FfmpegTools tools, string file, double gameDelaySeconds, string titleTag = "title")
    {
        var game = Source(0.25, 0);
        var chat = Source(0.75, 0);
        var everything = $"max(-1,min(1,{game}+{chat}))";
        string Audio(string channel) => $"aevalsrc=exprs='{channel}|{channel}':s=48000:d={Seconds}";
        var args = new List<string>
        {
            "-v", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"color=c=black:s=320x180:r=60:d={Seconds}",
            "-f", "lavfi", "-i", Audio(everything),
            "-f", "lavfi", "-i", Audio(Source(0.25, gameDelaySeconds)),
            "-f", "lavfi", "-i", Audio(chat),
            "-f", "lavfi", "-i", Audio("0"),
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "60",
            "-c:a", "aac", "-b:a", "160k",
        };
        string[] titles = ["Everything", "Game", "Chat", "Mic"];
        for (var i = 0; i < titles.Length; i++)
        {
            args.AddRange([$"-metadata:s:a:{i}", $"{titleTag}={titles[i]}", $"-disposition:a:{i}", i == 0 ? "default" : "0"]);
        }

        args.AddRange(["-movflags", "frag_keyframe+empty_moov+default_base_moof", file]);
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
