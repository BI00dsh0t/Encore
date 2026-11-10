using System.Windows.Input;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Encore.App.Library;
using Encore.Contracts.Messages;
using Encore.Platform.Desktop;

namespace Encore.App.Tests;

/// <summary>The Library's in-app player page, over a fake player. Real playback is Heavy (<see cref="PlayerPlaybackTests"/>).</summary>
[Collection(nameof(WpfCollection))]
public sealed class LibraryPlayerTests : IDisposable
{
    private const string Clip = @"Game\clip.mp4";
    private const string Saving = @"Game\saving.mp4";

    private readonly FakeEngineSession _engine = new();
    private readonly FakeLibraryShell _shell = new();
    private readonly FakePlayer _player = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-library-player-").FullName;

    public LibraryPlayerTests()
    {
        foreach (var path in new[] { Clip, Saving })
        {
            var absolute = Path.Combine(_folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllBytes(absolute, [0]);
        }
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void EnterPlaysAFinishedRowOnItsPageAndNotOneBeingWritten()
    {
        var page = new LibraryPage(_engine, shell: _shell, player: () => _player);
        page.Render(State());

        page.Click(Row(page, Saving), ctrl: false, shift: false);
        Assert.False(page.PlayCommand.CanExecute(null));
        page.OpenFocused();
        Assert.False(page.DrillIn.IsOpen);
        Assert.Null(page.Player);

        page.Click(Row(page, Clip), ctrl: false, shift: false);
        page.OpenFocused();
        Assert.True(page.DrillIn.IsOpen);
        Assert.Equal("clip.mp4", page.DrillIn.ItemTitle);
        Assert.Equal(PlayerState.Paused, page.Player!.State);
        Assert.Equal(0, _player.SelectedTrack);
        Assert.Equal(1, _player.FirstFrames);
        Assert.Empty(_shell.Calls);

        // "Open in default player" still hands the path to the shell.
        page.OpenCommand.Execute(null);
        Assert.Equal([("open", Path.Combine(_folder, Clip))], _shell.Calls);
    }

    [Fact]
    public void ADoubleClickOpensARowAndTheListsKeysAndMenuFollowTheSelection()
    {
        var page = new LibraryPage(_engine, shell: _shell, player: () => _player);
        page.Render(State());

        // The menu stays shut off a row and with nothing selected; from the keyboard it takes the focused row.
        Assert.False(page.TryPrepareContextMenu(fromKeyboard: false, onRow: false));
        Assert.False(page.TryPrepareContextMenu(fromKeyboard: true, onRow: false));
        var entry = page.FocusForEntry();
        Assert.NotNull(entry);
        Assert.Same(entry, page.FocusedRow);
        Assert.True(page.TryPrepareContextMenu(fromKeyboard: true, onRow: false));

        // Escape is the list's key and clears the selection; a key the list doesn't know is left alone.
        Assert.True(page.HandleKey(Key.Escape, ctrl: false));
        Assert.False(page.HasShownSelection);
        Assert.False(page.HandleKey(Key.B, ctrl: false));

        // A double click with Ctrl only selects; a plain one opens the player page.
        page.ClickRow(Row(page, Clip), clickCount: 2, ctrl: true, shift: false);
        Assert.False(page.DrillIn.IsOpen);
        page.ClickRow(Row(page, Clip), clickCount: 2, ctrl: false, shift: false);
        Assert.True(page.DrillIn.IsOpen);
    }

    [Fact]
    public void ThePlayerSharesTheFileForRenameAndDeleteAndBackReleasesIt()
    {
        var page = new LibraryPage(_engine, shell: _shell, player: () => _player);
        page.Render(State());
        page.Click(Row(page, Clip), ctrl: false, shift: false);
        page.OpenFocused();
        var absolute = Path.Combine(_folder, Clip);

        // Read-only and shared for read, write and delete: the engine can rename and delete it while it plays.
        Assert.False(_player.File!.CanWrite);
        File.Move(absolute, absolute + ".renamed");
        File.Delete(absolute + ".renamed");

        page.DrillIn.Close();
        Assert.Null(page.Player);
        Assert.True(_player.Disposed);
        Assert.False(_player.File.CanRead);
    }

    [Fact]
    public void AnItemLeavingTheLibraryClosesItsPlayer()
    {
        var page = new LibraryPage(_engine, shell: _shell, player: () => _player);
        page.Render(State());
        page.Click(Row(page, Clip), ctrl: false, shift: false);
        page.OpenFocused();

        page.Render(State() with { Library = [Item(Saving, writing: true)] });

        Assert.False(page.DrillIn.IsOpen);
        Assert.True(_player.Disposed);
    }

    [Fact]
    public void AFileThatCantBePlayedSaysSoAndOffersTheDefaultPlayer()
    {
        _player.Fail = true;
        var page = new LibraryPage(_engine, shell: _shell, player: () => _player);
        page.Render(State());
        page.Click(Row(page, Clip), ctrl: false, shift: false);
        page.OpenFocused();

        Assert.True(page.Player!.IsFailed);
        Assert.Equal(PlayerPage.FailedText, page.Player.Message);
        page.Player.OpenInDefaultPlayerCommand.Execute(null);
        Assert.Equal([("open", Path.Combine(_folder, Clip))], _shell.Calls);
    }

    // Any GPU failure building the picture, however its HRESULT is mapped, fails the page, never a blank or a crash.
    [Theory]
    [InlineData("out of video memory")]
    [InlineData("com")]
    [InlineData("unsupported size")]
    public void APictureTheGpuRefusesShowsTheFailureAndPauses(string failure)
    {
        var page = PlayingPage();
        var released = false;

        PlayerView.Draw(
            page,
            () => throw failure switch
            {
                "out of video memory" => new OutOfMemoryException(),
                "com" => new COMException("device removed", unchecked((int)0x887A0005)),
                _ => new ArgumentException("size"),
            },
            _ => Assert.Fail("Nothing is shown."),
            () => released = true);

        Assert.True(released);
        Assert.True(page.IsFailed);
        Assert.False(_player.IsPlaying);
        Assert.False(page.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void AFrameThatCantBeCopiedIsSkippedAndThePageKeepsPlaying() => Sta.Run(() =>
    {
        var page = PlayingPage();
        _player.CopyFails = new COMException("lost", unchecked((int)0x887A0007));
        using var surface = new VideoSurface(64, 36, new WindowInteropHelper(new Window()).EnsureHandle());

        PlayerView.Draw(page, () => surface, _ => Assert.Fail("A skipped frame isn't shown."), () => Assert.Fail("The surface is kept."));

        Assert.Equal(PlayerState.Playing, page.State);
    });

    private PlayerPage PlayingPage()
    {
        var library = new LibraryPage(_engine, shell: _shell, player: () => _player);
        library.Render(State());
        library.Click(Row(library, Clip), ctrl: false, shift: false);
        library.OpenFocused();
        library.Player!.PlayPauseCommand.Execute(null);
        Assert.True(_player.IsPlaying);
        return library.Player;
    }

    [Fact]
    public void TheLibrarysPlayerIsTheShellsDrillInAndLeavingTheLibraryClosesIt()
    {
        _engine.State = State();
        var model = new MainViewModel(_engine, action => action(), player: () => _player);
        model.Library.Click(Row(model.Library, Clip), ctrl: false, shift: false);
        model.Library.OpenFocused();

        Assert.Same(model.Library.DrillIn, model.CurrentDrillIn);
        Assert.True(model.IsDrilledIn);

        model.SelectedNavItem = model.NavItems.First(i => i.Page == model.General);

        Assert.False(model.Library.DrillIn.IsOpen);
        Assert.True(_player.Disposed);
    }

    [Fact]
    public void TheSurfaceFitsTheVideosAspectIntoThePictureAndNeverGrowsPastIt()
    {
        Assert.Equal((1600, 540), PlayerView.Fit(6400, 2160, 1600, 900));
        Assert.Equal((1280, 720), PlayerView.Fit(1280, 720, 3840, 2160));
        Assert.Equal((800, 450), PlayerView.Fit(1920, 1080, 801, 900));
    }

    private StateSnapshot State() => _engine.State! with
    {
        LibraryFolder = _folder,
        Library = [Item(Clip, writing: false), Item(Saving, writing: true)],
    };

    private static LibraryItem Item(string path, bool writing) =>
        new(path, null, 1, path == Clip ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddDays(1), writing);

    private static LibraryRow Row(LibraryPage page, string path) => page.Rows.Single(r => r.Path == path);
}
