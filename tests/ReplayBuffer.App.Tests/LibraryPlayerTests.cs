using System.IO;
using ReplayBuffer.App.Library;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

/// <summary>Story 14.2: the Library's in-app player page, over a fake player (AD-27). Real playback is Heavy (<see cref="PlayerPlaybackTests"/>).</summary>
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
    public void ThePlayerSharesTheFileForRenameAndDeleteAndBackReleasesIt()
    {
        var page = new LibraryPage(_engine, shell: _shell, player: () => _player);
        page.Render(State());
        page.Click(Row(page, Clip), ctrl: false, shift: false);
        page.OpenFocused();
        var absolute = Path.Combine(_folder, Clip);

        // Read-only and shared for read, write and delete (AD-27): the engine can rename and delete it while it plays.
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

/// <summary>A player that opens at once and plays nothing: records what the page asks of it.</summary>
internal sealed class FakePlayer : IPlayer
{
    public event Action? FrameAvailable;

    public event Action? Ended;

    /// <summary>OpenAsync throws, as Windows does for a file it can't play.</summary>
    public bool Fail { get; set; }

    public PlayerMedia Media { get; set; } = new(1280, 720, TimeSpan.FromSeconds(10), ["Game", "Mic", "Discord", "Desktop"], true);

    /// <summary>The stream the page handed over.</summary>
    public Stream? File { get; private set; }

    public int FirstFrames { get; private set; }

    public bool Disposed { get; private set; }

    public bool IsPlaying { get; private set; }

    public TimeSpan Position => TimeSpan.Zero;

    // Windows picks the last Track at open (AD-27).
    public int SelectedTrack { get; set; } = 3;

    public Task<PlayerMedia> OpenAsync(Stream file)
    {
        File = file;
        return Fail ? Task.FromException<PlayerMedia>(new PlayerException("unsupported")) : Task.FromResult(Media);
    }

    public void ShowFirstFrame() => FirstFrames++;

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void CopyFrame(VideoSurface target)
    {
    }

    public void Dispose() => Disposed = true;

    public void RaiseFrame() => FrameAvailable?.Invoke();

    public void RaiseEnded() => Ended?.Invoke();
}
