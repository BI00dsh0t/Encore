using System.IO;
using Encore.App.Settings;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>The Library's item pages: the in-app player and the clip creator, opened in the drill-in. UI thread.</summary>
internal sealed class LibraryPlayback : Observable
{
    private readonly DrillIn _drillIn;
    private readonly LibraryFileActions _actions;
    private readonly Func<IPlayer> _newPlayer;
    private readonly Func<string, Stream>? _openRead;
    private readonly Action<string> _log;
    private readonly IClipMediaService _clipMedia;
    private readonly string? _clipThumbnailCacheFolder;
    private PlayerViewModel? _player;
    private ClipCreatorViewModel? _creator;
    private LibraryItem? _creatorItem;

    /// <summary>The view state carries the volume and mute the last player page was left at.</summary>
    public LibraryPlayback(
        DrillIn drillIn,
        LibraryFileActions actions,
        LibraryViewState view,
        Func<IPlayer> newPlayer,
        Func<string, Stream>? openRead,
        Action<string> log,
        IClipMediaService clipMedia,
        string? clipThumbnailCacheFolder)
    {
        _drillIn = drillIn;
        _actions = actions;
        _newPlayer = newPlayer;
        _openRead = openRead;
        _log = log;
        _clipMedia = clipMedia;
        _clipThumbnailCacheFolder = clipThumbnailCacheFolder;
        (Volume, IsMuted) = (view.PlayerVolume, view.PlayerMuted);
        _drillIn.Closed += _ =>
        {
            ClosePlayer();
            CloseCreator();
        };
    }

    /// <summary>Raised after a player page closed, so its volume and mute can be saved.</summary>
    public event Action? PlayerClosed;

    /// <summary>The open player page, or null on the list.</summary>
    public PlayerViewModel? Player
    {
        get => _player;
        private set => Set(ref _player, value);
    }

    /// <summary>The open clip creator, or null.</summary>
    public ClipCreatorViewModel? Creator
    {
        get => _creator;
        private set => Set(ref _creator, value);
    }

    /// <summary>The player's volume, held as it changes and saved when the player page closes.</summary>
    public double Volume { get; private set; }

    public bool IsMuted { get; private set; }

    /// <summary>Opens the row's player page in the drill-in, then the file; the page is disposed when the drill-in closes.</summary>
    public void Play(LibraryRow row)
    {
        if (_actions.Existing([row]) is not [var path])
        {
            return;
        }

        _drillIn.Close();
        _actions.ClearError();
        var player = new PlayerViewModel(
            path,
            row.Name,
            _newPlayer(),
            _openRead,
            file => _actions.OpenInShell(row.Path, file, "Couldn't open the file."),
            link => _actions.OpenInShell(row.Path, link, "Couldn't open the Microsoft Store."),
            _log)
        {
            Volume = Volume,
            IsMuted = IsMuted,
        };
        player.VolumeChanged += OnPlayerVolumeChanged;
        player.CreateClip = () => CreateClip(row, player.Position.TotalSeconds);
        Player = player;
        _drillIn.Open(row.Path, row.Name);
        _ = player.OpenAsync();
    }

    /// <summary>Opens the clip creator on a written row, at a position in seconds; a file that changed shows why not.</summary>
    public void CreateClip(LibraryRow row, double position)
    {
        if (row.IsLocked || row.Item is not { BeingWritten: false } item || _actions.Existing([row]) is not [var path])
        {
            return;
        }

        try
        {
            var source = ReadSource(path, item);
            _drillIn.Close();
            _creatorItem = item;
            Creator = new ClipCreatorViewModel(source, row.Name, _clipMedia, _newPlayer, thumbnailCacheFolder: _clipThumbnailCacheFolder)
            {
                BackCommand = _drillIn.BackCommand,
            };
            Creator.SourceInvalidated = _drillIn.Close;
            _drillIn.Open(row.Path, "Create clip · " + row.Name);
            _ = Creator.OpenAsync(position);
        }
        catch (IOException error)
        {
            _actions.ShowError(error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            _actions.ShowError(error.Message);
        }
    }

    /// <summary>The window was hidden: the player pauses, and the creator pauses and cancels its work.</summary>
    public void Pause()
    {
        Player?.Pause();
        Creator?.PauseAndCancel();
    }

    /// <summary>The window is shown again.</summary>
    public void ResumeCreator() => Creator?.Resume();

    /// <summary>After the list read a snapshot: a page whose item left the Library, or changed, closes and releases the file.</summary>
    public void CloseIfSourceLeft(LibraryList list, string? libraryFolder)
    {
        if (_drillIn.ItemId is { } playing && !list.Contains(playing))
        {
            _drillIn.Close();
        }

        if (_creatorItem is { } original && CreatorSourceChanged(original, list, libraryFolder))
        {
            _drillIn.Close();
        }
    }

    // The file as it is on disk; it must still be the item the Library showed.
    private static ClipSource ReadSource(string path, LibraryItem item)
    {
        var file = new FileInfo(path);
        if (file.Length != item.Size || (item.Modified is { } modified && file.LastWriteTimeUtc != modified.UtcDateTime))
        {
            throw new IOException("This source changed. Wait for the Library to refresh, then reopen it.");
        }

        return new ClipSource(path, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    // Whether the creator's source is no longer the written file it opened: gone, rewritten, or under another folder.
    private bool CreatorSourceChanged(LibraryItem original, LibraryList list, string? libraryFolder)
    {
        if (list.Find(original.Path)?.Item is not { BeingWritten: false } current
            || current.Size != original.Size
            || current.Modified != original.Modified)
        {
            return true;
        }

        if (Creator is not { } creator)
        {
            return false;
        }

        return libraryFolder is null
            || !string.Equals(Path.GetFullPath(Path.Combine(libraryFolder, original.Path)), creator.Source.Path, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPlayerVolumeChanged(double volume, bool muted) => (Volume, IsMuted) = (volume, muted);

    private void ClosePlayer()
    {
        var player = Player;
        Player = null;
        if (player is null)
        {
            return;
        }

        player.VolumeChanged -= OnPlayerVolumeChanged;
        player.Dispose();
        PlayerClosed?.Invoke();
    }

    private void CloseCreator()
    {
        var creator = Creator;
        Creator = null;
        _creatorItem = null;
        creator?.Dispose();
    }
}
