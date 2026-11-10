using System.IO;
using Encore.App.Library;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

/// <summary>The player page's Track list, over a fake player with the four-Track fixture's names.</summary>
[Collection(nameof(WpfCollection))]
public sealed class PlayerTrackTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly FakeEngineSession _engine = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-player-tracks-").FullName;

    public PlayerTrackTests()
    {
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [0]);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheListShowsTheTracksByNameWithTheFirstPlayingAndAgainOnReopen()
    {
        var library = Library(out var players);
        var viewModel = Open(library);

        Assert.Equal(["Game", "Mic", "Discord", "Desktop"], viewModel.Tracks.Select(t => t.Name));
        Assert.Same(viewModel.Tracks[0], viewModel.SelectedTrack);
        Assert.Equal([true, false, false, false], viewModel.Tracks.Select(t => t.IsPlaying));
        viewModel.SelectedTrack = viewModel.Tracks[3];

        library.DrillIn.Close();
        var again = Open(library);
        Assert.Equal(0, players[^1].SelectedTrack);
        Assert.Same(again.Tracks[0], again.SelectedTrack);
    }

    [Fact]
    public void ChoosingATrackWhilePlayingSwitchesWithoutMovingThePosition()
    {
        var viewModel = Open(Library(out var players));
        var player = players[^1];
        viewModel.PlayPauseCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(4);
        viewModel.RefreshPosition();

        viewModel.SelectedTrack = viewModel.Tracks[2];

        Assert.Equal(2, player.SelectedTrack);
        Assert.Empty(player.Seeks);
        Assert.Equal(TimeSpan.FromSeconds(4), viewModel.Position);
        Assert.Equal(PlayerState.Playing, viewModel.State);
        Assert.Equal([false, false, true, false], viewModel.Tracks.Select(t => t.IsPlaying));

        // A list that clears its selection never stops the Track that plays.
        viewModel.SelectedTrack = null;
        Assert.Same(viewModel.Tracks[2], viewModel.SelectedTrack);
    }

    [Fact]
    public void AnUnnamedTrackIsCountedFromOne()
    {
        var viewModel = Open(Library(out _, ["Game", "", " "]));

        Assert.Equal(["Game", "track 2", "track 3"], viewModel.Tracks.Select(t => t.Name));
    }

    private LibraryViewModel Library(out List<FakePlayer> players, IReadOnlyList<string>? names = null)
    {
        _engine.State = _engine.State! with
        {
            LibraryFolder = _folder,
            Library = [new LibraryItem(Clip, null, 1, DateTimeOffset.UnixEpoch, false)],
        };
        var made = new List<FakePlayer>();
        players = made;
        var media = new PlayerMedia(1280, 720, TimeSpan.FromSeconds(10), names ?? ["Game", "Mic", "Discord", "Desktop"], true);
        var library = new LibraryViewModel(_engine, shell: new FakeLibraryShell(), player: () =>
        {
            var player = new FakePlayer { Media = media };
            made.Add(player);
            return player;
        });
        library.Render(_engine.State);
        return library;
    }

    private static PlayerViewModel Open(LibraryViewModel library)
    {
        library.Click(library.Rows.Single(), ctrl: false, shift: false);
        library.OpenFocused();
        return library.Player!;
    }
}
