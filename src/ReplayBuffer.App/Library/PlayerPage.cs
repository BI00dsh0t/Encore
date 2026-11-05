using System.IO;
using System.Windows.Input;

namespace ReplayBuffer.App.Library;

/// <summary>Where the player page is: opening the file, paused, playing, or unable to play it.</summary>
internal enum PlayerState
{
    Opening,
    Paused,
    Playing,
    Failed,
}

/// <summary>
/// One Library item's player page (AD-27, story 14.2): it opens the file read-only, shared for read, write and
/// delete, and hands the stream to the player. It opens paused on the first frame with the first Track
/// (Windows would pick the last). Disposing it stops the player and releases the file. Created and used
/// on the UI thread; the player's own events are brought back to it.
/// </summary>
internal sealed class PlayerPage : Observable, IDisposable
{
    /// <summary>The plain line shown when the file can't be played; 14.6 names the missing codec instead.</summary>
    public const string FailedText = "This file couldn't be played.";

    private readonly Func<string, Stream> _openRead;
    private readonly Action<string> _openInDefaultPlayer;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private Stream? _stream;
    private PlayerState _state = PlayerState.Opening;
    private PlayerMedia? _media;

    /// <param name="path">The file's absolute path.</param>
    /// <param name="title">The row's name, shown in the breadcrumb.</param>
    /// <param name="player">The player; the page owns and disposes it.</param>
    /// <param name="openRead">Opens the file; read-only and shared for read, write and delete without it (AD-27).</param>
    /// <param name="openInDefaultPlayer">Hands the path to the shell's default player, offered when this page can't play it.</param>
    public PlayerPage(string path, string title, IPlayer player, Func<string, Stream>? openRead = null, Action<string>? openInDefaultPlayer = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Title = title ?? "";
        Player = player ?? throw new ArgumentNullException(nameof(player));
        _openRead = openRead ?? OpenShared;
        _openInDefaultPlayer = openInDefaultPlayer ?? (_ => { });
        PlayPauseCommand = new RelayCommand(PlayPause, () => State is PlayerState.Paused or PlayerState.Playing);
        OpenInDefaultPlayerCommand = new RelayCommand(() => _openInDefaultPlayer(Path));
        Player.Ended += OnEnded;
    }

    /// <summary>The file's absolute path.</summary>
    public string Path { get; }

    /// <summary>The item's name.</summary>
    public string Title { get; }

    /// <summary>The player; the view copies its frames.</summary>
    public IPlayer Player { get; }

    /// <summary>What the file holds, once opened.</summary>
    public PlayerMedia? Media
    {
        get => _media;
        private set => Set(ref _media, value);
    }

    public PlayerState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(PlayPauseLabel));
                OnPropertyChanged(nameof(Message));
                ((RelayCommand)PlayPauseCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlaying => State == PlayerState.Playing;

    public bool IsFailed => State == PlayerState.Failed;

    /// <summary>The play button's text.</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>The line over the video while it opens or when it can't play; null otherwise.</summary>
    public string? Message => State switch
    {
        PlayerState.Opening => "Opening…",
        PlayerState.Failed => FailedText,
        _ => null,
    };

    /// <summary>Whether the page has been closed; a late frame or event is then ignored.</summary>
    public bool IsClosed { get; private set; }

    public ICommand PlayPauseCommand { get; }

    /// <summary>"Open in default player", offered when the page can't play the file.</summary>
    public ICommand OpenInDefaultPlayerCommand { get; }

    /// <summary>Opens the file, then shows its first frame, paused, with the first Track.</summary>
    public async Task OpenAsync()
    {
        try
        {
            _stream = _openRead(Path);
            var media = await Player.OpenAsync(_stream);
            if (IsClosed)
            {
                return;
            }

            if (media.TrackNames.Count > 0)
            {
                Player.SelectedTrack = 0;
            }

            Player.ShowFirstFrame();
            Media = media;
            State = PlayerState.Paused;
        }
#pragma warning disable CA1031 // Any failure to open is the page's plain message, never a crash.
        catch (Exception) when (!IsClosed)
#pragma warning restore CA1031
        {
            State = PlayerState.Failed;
        }
#pragma warning disable CA1031 // A failure after the page closed has nothing to show.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Pauses, if playing: the window was hidden.</summary>
    public void Pause()
    {
        if (State == PlayerState.Playing)
        {
            Player.Pause();
            State = PlayerState.Paused;
        }
    }

    /// <summary>Stops the player and releases the file; safe to call twice.</summary>
    public void Dispose()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        Player.Ended -= OnEnded;
        Player.Dispose();
        _stream?.Dispose();
        _stream = null;
    }

    // Read-only, shared for read, write and delete, so the engine's rename, delete and Cleanup never fail on it (AD-27).
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private void PlayPause()
    {
        if (IsClosed)
        {
            return;
        }

        if (State == PlayerState.Playing)
        {
            Player.Pause();
            State = PlayerState.Paused;
        }
        else if (State == PlayerState.Paused)
        {
            Player.Play();
            State = PlayerState.Playing;
        }
    }

    private void OnEnded() => OnUi(() =>
    {
        if (!IsClosed && State == PlayerState.Playing)
        {
            State = PlayerState.Paused;
        }
    });

    private void OnUi(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
