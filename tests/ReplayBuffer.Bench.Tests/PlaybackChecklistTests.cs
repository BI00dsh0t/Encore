using System.Text.Json;

namespace ReplayBuffer.Bench.Tests;

/// <summary>The matrix's Playback row: a skipped player is recorded as <c>not-checked</c>, in playback.json and the report.</summary>
public sealed class PlaybackChecklistTests : IDisposable
{
    private readonly BenchRun _run = new();

    public void Dispose() => _run.Dispose();

    [Fact]
    public void SkippedAnswersAreRecordedAsNotChecked()
    {
        // Border: skip. Discord: skip. VLC: plays, in sync, both tracks, a note. Chrome: an empty answer. Then input ends.
        var input = new StringReader(string.Join('\n', "skip", "skip", "y", "y", "both", "fine", ""));

        var (border, players) = PlaybackCheck.Questionnaire(["Discord", "VLC", "Chrome", "DaVinci Resolve"], input, TextWriter.Null);

        Assert.Equal("not-checked", border);
        Assert.Equal(new PlayerCheck("Discord", "not-checked", null, null, null, null, PlayerScripts.Person), players[0]);
        Assert.Equal(new PlayerCheck("VLC", "checked", true, true, "both", "fine", PlayerScripts.Person), players[1]);
        Assert.Equal(new PlayerCheck("Chrome", "not-checked", null, null, null, null, PlayerScripts.Person), players[2]);
        Assert.Equal(new PlayerCheck("DaVinci Resolve", "not-checked", null, null, null, null, PlayerScripts.Person), players[3]);

        var report = new PlaybackReport(DateTimeOffset.Now, "clip.mp4", 30, new CaptureSettings(1920, 1080, 60, 20_000, null, null), 2, border, players, []);
        BenchJson.Write(_run.PathOf(PlaybackCheck.FileName), report);
        var saved = JsonSerializer.Deserialize<PlaybackReport>(File.ReadAllText(_run.PathOf(PlaybackCheck.FileName)), BenchJson.Options)!;
        Assert.Equal("not-checked", saved.Players[0].Status);
        Assert.Equal("not-checked", saved.CaptureBorder);

        var md = Report.Markdown(Report.Generate(_run.Folder));
        Assert.Contains("| Discord |  |  |  |  | not-checked |", md, StringComparison.Ordinal);
        Assert.Contains("| VLC | yes | yes | both | fine | pass |", md, StringComparison.Ordinal);
        Assert.Contains("(§7.4 `IsBorderRequired`): **not-checked**", md, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnswerThatIsNotYesNoOrSkipIsAskedAgain()
    {
        var input = new StringReader(string.Join('\n', "maybe", "n", "n", ""));
        var output = new StringWriter();

        var (border, players) = PlaybackCheck.Questionnaire(["VLC"], input, output);

        Assert.Equal("no", border);
        Assert.Equal(new PlayerCheck("VLC", "checked", false, null, null, null, PlayerScripts.Person), players[0]);
        Assert.Contains("Answer y, n or skip.", output.ToString(), StringComparison.Ordinal);
    }
}
