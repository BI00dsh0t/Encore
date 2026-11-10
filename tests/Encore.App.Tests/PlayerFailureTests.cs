using System.IO;
using Encore.App.Library;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

/// <summary>The player page when Windows can't play the file, over a fake player.</summary>
[Collection(nameof(WpfCollection))]
public sealed class PlayerFailureTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly FakeEngineSession _engine = new();
    private readonly FakeLibraryShell _shell = new();
    private readonly List<string> _log = [];
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-player-failure-").FullName;

    public PlayerFailureTests()
    {
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [0]);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("HEVC", "HEVC Video Extension", "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7")]
    [InlineData("AV1", "AV1 Video Extension", "ms-windows-store://pdp/?ProductId=9MVZQVXJBQ9V")]
    public void AMissingExtensionIsNamedWithItsStorePageAndTheDefaultPlayer(string codec, string extension, string store)
    {
        var player = new FakePlayer { Media = Media(decodable: false, codec) };
        var (library, viewModel) = Open(player);

        Assert.True(viewModel.IsFailed);
        Assert.Equal($"This video is {codec}, which Windows can't play without the {extension}. Get it from the Microsoft Store, then open the file again. The file itself is fine.", viewModel.Message);
        Assert.Equal($"Get the {extension}", viewModel.StoreLinkText);
        Assert.Equal(0, player.FirstFrames);
        Assert.Contains(_log, l => l.Contains($"video {codec} NOT decodable", StringComparison.Ordinal));

        viewModel.OpenStoreCommand.Execute(null);
        viewModel.OpenInDefaultPlayerCommand.Execute(null);
        Assert.Equal([("open", store), ("open", Path.Combine(_folder, Clip))], _shell.Calls);

        library.DrillIn.Close();
        Assert.True(player.Disposed);
    }

    [Fact]
    public void AnUnknownFormatOrAFileThatWontOpenSaysSoWithNoStoreLink()
    {
        var (_, unknown) = Open(new FakePlayer { Media = Media(decodable: false, "VP9") });
        Assert.Equal(PlayerViewModel.UnknownCodecText, unknown.Message);
        Assert.False(unknown.HasStoreLink);

        var (_, corrupt) = Open(new FakePlayer { Fail = true });
        Assert.Equal(PlayerViewModel.FailedText, corrupt.Message);
        Assert.False(corrupt.HasStoreLink);
        Assert.Contains(_log, l => l.Contains("couldn't open clip.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public void ATrackWindowsCantDecodeIsUnavailableAndCantBeChosen()
    {
        var player = new FakePlayer { Media = Media(decodable: true, "H264") with { TracksPlayable = [true, false, true, true] } };
        var (_, viewModel) = Open(player);

        Assert.Equal(["Game", "Mic (unavailable)", "Discord", "Desktop"], viewModel.Tracks.Select(t => t.Name));
        viewModel.SelectedTrack = viewModel.Tracks[1];
        Assert.Same(viewModel.Tracks[0], viewModel.SelectedTrack);
        Assert.Equal(0, player.SelectedTrack);
    }

    [Fact]
    public void TheDevSwitchNamesACodecOnAnyInstanceButTheInstalledOne()
    {
        var dev = new Instance(Instance.DevName, _folder, _folder);
        var installed = new Instance(Instance.InstalledName, _folder, _folder);

        Assert.Equal("AV1", ShellApp.PlayerUnsupported(["--player-unsupported=av1"], dev));
        Assert.Equal("HEVC", ShellApp.PlayerUnsupported(["--tray", "--player-unsupported=HEVC"], dev));
        Assert.Null(ShellApp.PlayerUnsupported(["--player-unsupported=hevc"], installed));
        Assert.Null(ShellApp.PlayerUnsupported(["--player-unsupported=vp9"], dev));
        Assert.Null(ShellApp.PlayerUnsupported([], dev));
    }

    private static PlayerMedia Media(bool decodable, string codec) =>
        new(1280, 720, TimeSpan.FromSeconds(10), ["Game", "Mic", "Discord", "Desktop"], decodable, codec);

    private (LibraryViewModel Library, PlayerViewModel ViewModel) Open(FakePlayer player)
    {
        _engine.State = _engine.State! with
        {
            LibraryFolder = _folder,
            Library = [new LibraryItem(Clip, null, 1, DateTimeOffset.UnixEpoch, false)],
        };
        var library = new LibraryViewModel(_engine, shell: _shell, player: () => player, log: _log.Add);
        library.Render(_engine.State);
        library.Click(library.Rows.Single(), ctrl: false, shift: false);
        library.OpenFocused();
        return (library, library.Player!);
    }
}
