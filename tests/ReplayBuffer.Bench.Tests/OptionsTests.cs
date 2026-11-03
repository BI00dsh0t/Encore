using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Bench.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void DefaultsAreTheStoryOutput()
    {
        var options = BenchOptions.Parse(["drift"]);

        var capture = options.CaptureSettings();

        Assert.Equal("drift", options.Command);
        Assert.Equal(new CaptureSettings(1920, 1080, 60, 20_000, null, null), capture);
        Assert.Equal(60, options.Double("minutes", 60, 1, 600));
        Assert.Equal(1000.0 / 60, capture.FrameMs);
    }

    [Fact]
    public void ValuesAndFlagsAreRead()
    {
        var options = BenchOptions.Parse(["drift", "--minutes", "2", "--size", "1280x720", "--fps", "30", "--output-device", "Game (BEACN)", "--reanalyze"]);

        Assert.Equal(2, options.Double("minutes", 60, 1, 600));
        Assert.True(options.Flag("reanalyze"));
        Assert.Equal(new CaptureSettings(1280, 720, 30, 20_000, "Game (BEACN)", null), options.CaptureSettings());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("hotkey", "--size", "1920x1080")]
    public void BadCommandLinesAreRefused(params string[] args) =>
        Assert.Throws<BenchUsageException>(() => BenchOptions.Parse(args));

    [Theory]
    [InlineData("--size", "1921x1080")]
    [InlineData("--size", "big")]
    public void BadCaptureValuesAreRefused(string name, string value) =>
        Assert.Throws<BenchUsageException>(() => BenchOptions.Parse(["drift", name, value]).CaptureSettings());

    [Fact]
    public void AnOutOfRangeNumberIsRefused()
    {
        var options = BenchOptions.Parse(["hotkey", "--presses", "0"]);

        Assert.Throws<BenchUsageException>(() => options.Int("presses", 20, 1, 1000));
    }

    [Fact]
    public void TracksReadsItsDevicesAndOptions()
    {
        var options = BenchOptions.Parse(["tracks", "--device-a", "Speakers A", "--device-b", "Speakers B", "--input-device", "Mic", "--minutes", "2", "--vlc", "vlc.exe"]);

        Assert.Equal(("Speakers A", "Speakers B"), TracksCheck.Devices(options));
        Assert.Equal(new CaptureSettings(1920, 1080, 60, 20_000, null, "Mic"), options.CaptureSettings());
        Assert.Equal(2, options.Double("minutes", 5, 1, 60));
        Assert.Equal("vlc.exe", options.Text("vlc"));
    }

    [Theory]
    [InlineData("tracks", "--device-b", "Speakers B")]
    [InlineData("tracks", "--device-a", "Speakers A")]
    public void TracksNeedsTwoDifferentDevices(params string[] args) =>
        Assert.Throws<BenchUsageException>(() => TracksCheck.Devices(BenchOptions.Parse(args)));

    [Theory]
    [InlineData("tracks", "--output-device", "Speakers")]
    [InlineData("tracks", "--players", "VLC")]
    public void TracksRefusesOptionsItDoesNotTake(params string[] args) =>
        Assert.Throws<BenchUsageException>(() => BenchOptions.Parse(args));

    [Fact]
    public void ANoDeviceTracksRunIsAUsageError()
    {
        using var run = new BenchRun();

        Assert.Throws<BenchUsageException>(() => TracksCheck.Run(run.Context("tracks", "--device-b", "B")));
    }

    [Fact]
    public void ListsAreCommaSeparated()
    {
        var options = BenchOptions.Parse(["playback", "--players", "VLC, Chrome ,,"]);

        Assert.Equal(["VLC", "Chrome"], options.List("players", PlaybackCheck.DefaultPlayers));
        Assert.Equal(PlaybackCheck.DefaultPlayers, BenchOptions.Parse(["playback"]).List("players", PlaybackCheck.DefaultPlayers));
    }

    [Theory]
    [InlineData("ctrl+alt+f9", 0x78, HotkeyModifiers.Control | HotkeyModifiers.Alt)]
    [InlineData("Shift+F24", 0x87, HotkeyModifiers.Shift)]
    public void KeysParse(string text, int vk, int modifiers)
    {
        var combo = KeyCombo.Parse(text);

        Assert.Equal(vk, combo.Vk);
        Assert.Equal(modifiers, combo.Modifiers);
    }

    [Theory]
    [InlineData("ctrl+")]
    [InlineData("ctrl+ctrl+f9")]
    public void BadKeysAreRefused(string text) => Assert.Throws<BenchUsageException>(() => KeyCombo.Parse(text));

    [Fact]
    public void TheRunFolderIsNewUnderMeasurementsOrTheOneGiven()
    {
        var repo = Directory.CreateTempSubdirectory("bench-repo-").FullName;
        try
        {
            var now = new DateTime(2026, 10, 27, 14, 5, 9);
            Assert.Equal(Path.Combine(repo, "measurements", "bench-20261027-140509"), BenchContext.ResolveRunFolder(repo, null, false, now));
            Assert.Equal(Path.GetFullPath("elsewhere"), BenchContext.ResolveRunFolder(repo, "elsewhere", false, now));
            Assert.Throws<BenchFailure>(() => BenchContext.ResolveRunFolder(repo, null, true, now));

            Directory.CreateDirectory(Path.Combine(repo, "measurements", "bench-20261027-100000"));
            Directory.CreateDirectory(Path.Combine(repo, "measurements", "bench-20261027-120000"));
            Directory.CreateDirectory(Path.Combine(repo, "measurements", "path-a-20261028-000000"));
            Assert.Equal(Path.Combine(repo, "measurements", "bench-20261027-120000"), BenchContext.ResolveRunFolder(repo, null, true, now));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("n", false)]
    public void PlaybackAnswersParse(string? line, bool? expected) => Assert.Equal(expected, PlaybackCheck.ParseAnswer(line));
}
