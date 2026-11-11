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
        var viewModel = new LibraryViewModel(_engine, shell: _shell, player: () => _player);
        viewModel.Render(State());

        viewModel.Selection.Click(Row(viewModel, Saving), ctrl: false, shift: false);
        Assert.False(viewModel.PlayCommand.CanExecute(null));
        viewModel.OpenFocused();
        Assert.False(viewModel.DrillIn.IsOpen);
        Assert.Null(viewModel.Playback.Player);

        viewModel.Selection.Click(Row(viewModel, Clip), ctrl: false, shift: false);
        viewModel.OpenFocused();
        Assert.True(viewModel.DrillIn.IsOpen);
        Assert.Equal("clip.mp4", viewModel.DrillIn.ItemTitle);
        Assert.Equal(PlayerState.Paused, viewModel.Playback.Player!.State);
        Assert.Equal(0, _player.SelectedTrack);
        Assert.Equal(1, _player.FirstFrames);
        Assert.Empty(_shell.Calls);

        // "Open in default player" still hands the path to the shell.
        viewModel.OpenCommand.Execute(null);
        Assert.Equal([("open", Path.Combine(_folder, Clip))], _shell.Calls);
    }

    [Fact]
    public void ADoubleClickOpensARowAndTheListsKeysAndMenuFollowTheSelection()
    {
        var viewModel = new LibraryViewModel(_engine, shell: _shell, player: () => _player);
        viewModel.Render(State());

        // The menu stays shut off a row and with nothing selected; from the keyboard it takes the focused row.
        Assert.False(viewModel.Selection.TryPrepareContextMenu(fromKeyboard: false, onRow: false));
        Assert.False(viewModel.Selection.TryPrepareContextMenu(fromKeyboard: true, onRow: false));
        var entry = viewModel.Selection.FocusForEntry();
        Assert.NotNull(entry);
        Assert.Same(entry, viewModel.Selection.FocusedRow);
        Assert.True(viewModel.Selection.TryPrepareContextMenu(fromKeyboard: true, onRow: false));

        // Escape is the list's key and clears the selection; a key the list doesn't know is left alone.
        Assert.True(viewModel.HandleKey(Key.Escape, ctrl: false));
        Assert.False(viewModel.Selection.HasShownSelection);
        Assert.False(viewModel.HandleKey(Key.B, ctrl: false));

        // A double click with Ctrl only selects; a plain one opens the player page.
        viewModel.ClickRow(Row(viewModel, Clip), clickCount: 2, ctrl: true, shift: false);
        Assert.False(viewModel.DrillIn.IsOpen);
        viewModel.ClickRow(Row(viewModel, Clip), clickCount: 2, ctrl: false, shift: false);
        Assert.True(viewModel.DrillIn.IsOpen);
    }

    [Fact]
    public void ThePlayerSharesTheFileForRenameAndDeleteAndBackReleasesIt()
    {
        var viewModel = new LibraryViewModel(_engine, shell: _shell, player: () => _player);
        viewModel.Render(State());
        viewModel.Selection.Click(Row(viewModel, Clip), ctrl: false, shift: false);
        viewModel.OpenFocused();
        var absolute = Path.Combine(_folder, Clip);

        // Read-only and shared for read, write and delete: the engine can rename and delete it while it plays.
        Assert.False(_player.File!.CanWrite);
        File.Move(absolute, absolute + ".renamed");
        File.Delete(absolute + ".renamed");

        viewModel.DrillIn.Close();
        Assert.Null(viewModel.Playback.Player);
        Assert.True(_player.Disposed);
        Assert.False(_player.File.CanRead);
    }

    [Fact]
    public void AnItemLeavingTheLibraryClosesItsPlayer()
    {
        var viewModel = new LibraryViewModel(_engine, shell: _shell, player: () => _player);
        viewModel.Render(State());
        viewModel.Selection.Click(Row(viewModel, Clip), ctrl: false, shift: false);
        viewModel.OpenFocused();

        viewModel.Render(State() with { Library = [Item(Saving, writing: true)] });

        Assert.False(viewModel.DrillIn.IsOpen);
        Assert.True(_player.Disposed);
    }

    [Fact]
    public void AFileThatCantBePlayedSaysSoAndOffersTheDefaultPlayer()
    {
        _player.Fail = true;
        var viewModel = new LibraryViewModel(_engine, shell: _shell, player: () => _player);
        viewModel.Render(State());
        viewModel.Selection.Click(Row(viewModel, Clip), ctrl: false, shift: false);
        viewModel.OpenFocused();

        Assert.True(viewModel.Playback.Player!.IsFailed);
        Assert.Equal(PlayerViewModel.FailedText, viewModel.Playback.Player.Message);
        viewModel.Playback.Player.OpenInDefaultPlayerCommand.Execute(null);
        Assert.Equal([("open", Path.Combine(_folder, Clip))], _shell.Calls);
    }

    // Any GPU failure building the picture, however its HRESULT is mapped, fails the page, never a blank or a crash.
    [Theory]
    [InlineData("out of video memory")]
    [InlineData("com")]
    [InlineData("unsupported size")]
    public void APictureTheGpuRefusesShowsTheFailureAndPauses(string failure)
    {
        var viewModel = PlayingViewModel();
        var released = false;

        viewModel.DrawFrame(() => throw failure switch
            {
                "out of video memory" => new OutOfMemoryException(),
                "com" => new COMException("device removed", unchecked((int)0x887A0005)),
                _ => new ArgumentException("size"),
            },
            _ => Assert.Fail("Nothing is shown."),
            () => released = true);

        Assert.True(released);
        Assert.True(viewModel.IsFailed);
        Assert.False(_player.IsPlaying);
        Assert.False(viewModel.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void AFrameThatCantBeCopiedIsSkippedAndThePageKeepsPlaying() => Sta.Run(() =>
    {
        var viewModel = PlayingViewModel();
        _player.CopyFails = new COMException("lost", unchecked((int)0x887A0007));
        using var surface = new VideoSurface(64, 36, new WindowInteropHelper(new Window()).EnsureHandle());

        viewModel.DrawFrame(() => surface, _ => Assert.Fail("A skipped frame isn't shown."), () => Assert.Fail("The surface is kept."));

        Assert.Equal(PlayerState.Playing, viewModel.State);
    });

    [Fact]
    public void ThePagesKeysPauseSeekAndChangeTheVolume()
    {
        var viewModel = PlayingViewModel();

        Assert.True(viewModel.HandleKey(Key.Space));
        Assert.False(_player.IsPlaying);
        Assert.True(viewModel.HandleKey(Key.Right));
        Assert.Equal([PlayerViewModel.SeekStep], _player.Seeks);
        Assert.True(viewModel.HandleKey(Key.Down));
        Assert.Equal(0.95, _player.Volume);
        Assert.True(viewModel.HandleKey(Key.F));
        Assert.True(viewModel.IsFullScreen);
        Assert.False(viewModel.HandleKey(Key.B));
    }

    private PlayerViewModel PlayingViewModel()
    {
        var library = new LibraryViewModel(_engine, shell: _shell, player: () => _player);
        library.Render(State());
        library.Selection.Click(Row(library, Clip), ctrl: false, shift: false);
        library.OpenFocused();
        library.Playback.Player!.PlayPauseCommand.Execute(null);
        Assert.True(_player.IsPlaying);
        return library.Playback.Player;
    }

    [Fact]
    public void TheLibrarysPlayerIsTheShellsDrillInAndBackClosesIt()
    {
        _engine.State = State();
        var model = new MainViewModel(_engine, action => action(), player: () => _player);
        model.Library.Selection.Click(Row(model.Library, Clip), ctrl: false, shift: false);
        model.Library.OpenFocused();

        Assert.Same(model.Library.DrillIn, model.CurrentDrillIn);
        Assert.True(model.IsDrilledIn);

        // Settings opens over the player page and leaves it open; with Settings closed, Back closes it.
        model.OpenSettings(model.General);
        Assert.False(model.GoBack());
        model.Settings.Close();
        Assert.True(model.IsDrilledIn);
        Assert.True(model.GoBack());

        Assert.False(model.Library.DrillIn.IsOpen);
        Assert.True(_player.Disposed);
    }

    [Fact]
    public void TheSurfaceFitsTheVideosAspectIntoThePictureAndNeverGrowsPastIt()
    {
        Assert.Equal((1600, 540), PictureFit.Size(6400, 2160, 1600, 900));
        Assert.Equal((1280, 720), PictureFit.Size(1280, 720, 3840, 2160));
        Assert.Equal((800, 450), PictureFit.Size(1920, 1080, 801, 900));
    }

    private StateSnapshot State() => _engine.State! with
    {
        LibraryFolder = _folder,
        Library = [Item(Clip, writing: false), Item(Saving, writing: true)],
    };

    private static LibraryItem Item(string path, bool writing) =>
        new(path, null, 1, path == Clip ? DateTimeOffset.UnixEpoch : DateTimeOffset.UnixEpoch.AddDays(1), writing);

    private static LibraryRow Row(LibraryViewModel viewModel, string path) => viewModel.List.Rows.Single(r => r.Path == path);
}
