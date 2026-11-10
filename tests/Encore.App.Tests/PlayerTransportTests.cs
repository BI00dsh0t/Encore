using System.IO;
using Encore.App.Library;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

/// <summary>The player page's transport, over a fake player with a 30 s file.</summary>
[Collection(nameof(WpfCollection))]
public sealed class PlayerTransportTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";

    private readonly FakeEngineSession _engine = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-player-transport-").FullName;

    public PlayerTransportTests()
    {
        var absolute = Path.Combine(_folder, Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, [0]);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheSeekBarAndKeysSeekWithinTheFileAndADragSeeksOncePerFrame()
    {
        var player = new FakePlayer { Media = Media(TimeSpan.FromSeconds(30)) };
        var page = Open(player);

        page.SeekBy(PlayerViewModel.SeekStep);
        Assert.Equal(TimeSpan.FromSeconds(5), player.Seeks[^1]);
        page.SeekTo(TimeSpan.FromSeconds(28));
        page.SeekBy(PlayerViewModel.SeekStep);
        Assert.Equal(TimeSpan.FromSeconds(30), player.Seeks[^1]);
        page.SeekBy(-TimeSpan.FromSeconds(40));
        Assert.Equal(TimeSpan.Zero, player.Seeks[^1]);

        // A click on the bar seeks there.
        page.PositionSeconds = 15;
        Assert.Equal(TimeSpan.FromSeconds(15), player.Seeks[^1]);
        Assert.Equal("0:15 / 0:30", page.TimeText);

        // A drag moves the bar at once but seeks only to its latest point, once a rendered frame.
        var seeks = player.Seeks.Count;
        page.IsScrubbing = true;
        page.PositionSeconds = 10;
        page.PositionSeconds = 11;
        page.PositionSeconds = 12;
        Assert.Equal(seeks, player.Seeks.Count);
        Assert.Equal("0:12 / 0:30", page.TimeText);
        page.ApplyPendingScrub();
        page.IsScrubbing = false;
        Assert.Equal([TimeSpan.FromSeconds(12)], player.Seeks[seeks..]);
    }

    [Fact]
    public void PlayPausesAndAtTheEndStopsOnTheLastFrameThenStartsAgain() => Sta.Run(() =>
    {
        var player = new FakePlayer { Media = Media(TimeSpan.FromSeconds(30)) };
        var page = Open(player);

        page.PlayPauseCommand.Execute(null);
        Assert.True(player.IsPlaying);
        player.Position = TimeSpan.FromSeconds(7.6);
        page.RefreshPosition();
        Assert.Equal("0:07 / 0:30", page.TimeText);

        player.RaiseEnded();
        Assert.Equal(PlayerState.Paused, page.State);
        Assert.Equal("0:30 / 0:30", page.TimeText);

        page.PlayPauseCommand.Execute(null);
        Assert.Equal(TimeSpan.Zero, player.Seeks[^1]);
        Assert.True(player.IsPlaying);
        Assert.Equal(PlayerState.Playing, page.State);
    });

    [Fact]
    public void TheVolumeAndMuteAreKeptAcrossRuns()
    {
        var viewFile = Path.Combine(_folder, "library-view.json");
        var first = new FakePlayer();
        var page = Open(first, viewFile, out var library);

        page.Volume = 0.4;
        page.MuteCommand.Execute(null);
        Assert.Equal((0.4, true), (first.Volume, first.IsMuted));

        // Written when the page closes, not once a slider step.
        library.DrillIn.Close();

        var second = new FakePlayer();
        var again = Open(second, viewFile);
        Assert.Equal((0.4, true), (again.Volume, again.IsMuted));
        Assert.Equal((0.4, true), (second.Volume, second.IsMuted));

        // Up unmutes and raises the volume a step.
        again.ChangeVolume(PlayerViewModel.VolumeStep);
        Assert.Equal((0.45, false), (Math.Round(second.Volume, 2), second.IsMuted));
    }

    [Fact]
    public void AFileWithNoKnownLengthShowsTheElapsedTimeOnlyAndCantBeSought()
    {
        var player = new FakePlayer { Media = Media(null) };
        var page = Open(player);

        Assert.False(page.CanSeek);
        Assert.Equal("0:00", page.TimeText);
        page.SeekBy(PlayerViewModel.SeekStep);
        page.PositionSeconds = 3;
        Assert.Empty(player.Seeks);

        page.PlayPauseCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(65);
        page.RefreshPosition();
        Assert.Equal("1:05", page.TimeText);
        Assert.Equal("1:02:03", PlayerViewModel.Format(TimeSpan.FromSeconds(3723), TimeSpan.FromHours(2)));
    }

    // A view file from before the player existed has no player keys and loads full and unmuted.
    [Fact]
    public void AViewFileFromBeforeThePlayerLoadsFullAndUnmuted()
    {
        var file = Path.Combine(_folder, "old-view.json");
        File.WriteAllText(file, """{ "GroupBy": "Day", "Game": null, "Date": "All", "FavouritesOnly": true, "Sort": "Name" }""");

        var state = LibraryViewState.Load(file);

        Assert.Equal((LibraryGrouping.Day, true, LibrarySort.Name), (state.GroupBy, state.FavouritesOnly, state.Sort));
        Assert.Equal((1.0, false), (state.PlayerVolume, state.PlayerMuted));
    }

    // The volume steps by whole hundredths with no drift, and any change unmutes, the slider's included.
    [Fact]
    public void TheVolumeStepsWithoutDriftAndAnyChangeUnmutes()
    {
        var player = new FakePlayer();
        var page = Open(player);

        // A slider at 0.333 is told the 0.33 kept, so its thumb snaps.
        var raised = 0;
        page.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(PlayerViewModel.Volume) ? 1 : 0;
        page.Volume = 0.333333;
        Assert.Equal(0.33, page.Volume);
        Assert.Equal(1, raised);
        page.Volume = 0.1;
        page.ChangeVolume(-PlayerViewModel.VolumeStep);
        page.ChangeVolume(-PlayerViewModel.VolumeStep);
        Assert.Equal(0.0, page.Volume);
        for (var i = 0; i < 25; i++)
        {
            page.ChangeVolume(PlayerViewModel.VolumeStep);
        }

        Assert.Equal(1.0, page.Volume);

        // Up at 1.0 while muted still unmutes, though the volume can't rise.
        page.MuteCommand.Execute(null);
        page.ChangeVolume(PlayerViewModel.VolumeStep);
        Assert.Equal((1.0, false, false), (page.Volume, page.IsMuted, player.IsMuted));

        // The slider, dragged while muted, unmutes, as the keys do.
        page.MuteCommand.Execute(null);
        Assert.True(player.IsMuted);
        page.Volume = 0.5;
        Assert.Equal((0.5, false, false), (player.Volume, player.IsMuted, page.IsMuted));
    }

    private static PlayerMedia Media(TimeSpan? duration) => new(1280, 720, duration, ["Game", "Mic", "Discord", "Desktop"], true);

    private PlayerViewModel Open(FakePlayer player, string? viewFile = null) => Open(player, viewFile, out _);

    private PlayerViewModel Open(FakePlayer player, string? viewFile, out LibraryViewModel opened)
    {
        _engine.State = _engine.State! with
        {
            LibraryFolder = _folder,
            Library = [new LibraryItem(Clip, null, 1, DateTimeOffset.UnixEpoch, false)],
        };
        var library = new LibraryViewModel(_engine, viewStateFile: viewFile, shell: new FakeLibraryShell(), player: () => player);
        library.Render(_engine.State);
        library.Click(library.Rows.Single(), ctrl: false, shift: false);
        library.OpenFocused();
        opened = library;
        return library.Player!;
    }
}
