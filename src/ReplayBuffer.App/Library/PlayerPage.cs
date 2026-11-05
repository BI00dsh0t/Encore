using System.Collections.ObjectModel;
using System.Globalization;
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
/// One Library item's player page (AD-27, stories 14.2 and 14.5).
/// <list type="bullet">
/// <item>It opens the file read-only, shared for read, write and delete, and hands the stream to the player.
/// It opens paused on the first frame with the first Track (Windows would pick the last).</item>
/// <item>The transport: play and pause, a seek bar over the file's length with the elapsed and total time,
/// keys that seek 5 s and change the volume 5 %, and one volume with mute, kept by the Library.</item>
/// <item>A file with no known length (a crash-cut Recording) shows the elapsed time only, and can't be sought.</item>
/// <item>The Track list (story 14.4): the file's Tracks by name, in file order, one playing at a time, the
/// first at open. Choosing another switches mid-play, with no restart and no jump in position.</item>
/// <item>At the end it stops on the last frame, and Play starts again from the beginning.</item>
/// <item>When Windows can't play the file (story 14.6) it says so in plain words: the missing HEVC or AV1
/// Video Extension by name, with its Microsoft Store page; a file it can't open or that shows no picture;
/// and "Open in default player" either way. A Track Windows can't decode shows as unavailable.</item>
/// <item>Disposing it stops the player and releases the file.</item>
/// </list>
/// Created and used on the UI thread; the player's own events are brought back to it.
/// </summary>
internal sealed class PlayerPage : Observable, IDisposable
{
    /// <summary>The plain line when the file can't be opened, or shows no picture.</summary>
    public const string FailedText = "This file couldn't be played.";

    /// <summary>The line for a video format Windows can't play and that has no Store extension we know.</summary>
    public const string UnknownCodecText = "Windows can't play this video's format. The file itself is fine.";

    /// <summary>How far Left and Right seek.</summary>
    public static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);

    /// <summary>How far Up and Down change the volume.</summary>
    public const double VolumeStep = 0.05;

    private readonly Func<string, Stream> _openRead;
    private readonly Action<string> _openInDefaultPlayer;
    private readonly Action<string> _openLink;
    private readonly Action<string> _log;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private Stream? _stream;
    private PlayerState _state = PlayerState.Opening;
    private PlayerMedia? _media;
    private TimeSpan _position;
    private double _volume = 1.0;
    private bool _isMuted;
    private bool _atEnd;
    private bool _isScrubbing;
    private TimeSpan? _pendingScrub;
    private PlayerTrack? _selectedTrack;
    private string? _failure;
    private CodecExtension? _missing;

    /// <param name="path">The file's absolute path.</param>
    /// <param name="title">The row's name, shown in the breadcrumb.</param>
    /// <param name="player">The player; the page owns and disposes it.</param>
    /// <param name="openRead">Opens the file; read-only and shared for read, write and delete without it (AD-27).</param>
    /// <param name="openInDefaultPlayer">Hands the path to the shell's default player, offered when this page can't play it.</param>
    /// <param name="openLink">Opens a Microsoft Store page through the shell (story 14.6).</param>
    /// <param name="log">Writes a line to the App's log: what was opened, its codec and why it can't play.</param>
    public PlayerPage(string path, string title, IPlayer player, Func<string, Stream>? openRead = null, Action<string>? openInDefaultPlayer = null, Action<string>? openLink = null, Action<string>? log = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Title = title ?? "";
        Player = player ?? throw new ArgumentNullException(nameof(player));
        _openRead = openRead ?? OpenShared;
        _openInDefaultPlayer = openInDefaultPlayer ?? (_ => { });
        _openLink = openLink ?? (_ => { });
        _log = log ?? (_ => { });
        PlayPauseCommand = new RelayCommand(PlayPause, () => State is PlayerState.Paused or PlayerState.Playing);
        MuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        OpenInDefaultPlayerCommand = new RelayCommand(() => _openInDefaultPlayer(Path));
        OpenStoreCommand = new RelayCommand(() =>
        {
            if (_missing is { } extension)
            {
                _openLink(extension.StoreLink);
            }
        });
        Player.Ended += OnEnded;
    }

    /// <summary>The volume or mute changed: the Library keeps them (story 14.5).</summary>
    public event Action<double, bool>? VolumeChanged;

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
        private set
        {
            if (Set(ref _media, value))
            {
                OnPropertyChanged(nameof(HasDuration));
                OnPropertyChanged(nameof(DurationSeconds));
                OnPropertyChanged(nameof(CanSeek));
                OnPropertyChanged(nameof(TimeText));
            }
        }
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
                OnPropertyChanged(nameof(StoreLinkText));
                OnPropertyChanged(nameof(HasStoreLink));
                OnPropertyChanged(nameof(CanSeek));
                OnPropertyChanged(nameof(HasControls));
                OnPropertyChanged(nameof(HasTracks));
                ((RelayCommand)PlayPauseCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlaying => State == PlayerState.Playing;

    public bool IsFailed => State == PlayerState.Failed;

    /// <summary>Whether the transport shows: once the file is open and playable.</summary>
    public bool HasControls => State is PlayerState.Paused or PlayerState.Playing;

    /// <summary>The play button's text.</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>The line over the video while it opens or when it can't play; null otherwise.</summary>
    public string? Message => State switch
    {
        PlayerState.Opening => "Opening…",
        PlayerState.Failed => _failure ?? FailedText,
        _ => null,
    };

    /// <summary>The Store link's text when an extension is missing: "Get the HEVC Video Extension"; null otherwise.</summary>
    public string? StoreLinkText => IsFailed && _missing is { } extension ? $"Get the {extension.Name}" : null;

    /// <summary>Whether the Store link shows.</summary>
    public bool HasStoreLink => StoreLinkText is not null;

    /// <summary>Whether the file's length is known; a crash-cut Recording's isn't.</summary>
    public bool HasDuration => Media?.Duration is not null;

    /// <summary>Whether the seek bar and the seek keys work: a known length, and a playable file.</summary>
    public bool CanSeek => HasDuration && HasControls;

    /// <summary>Where the player is, as last read.</summary>
    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (Set(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionSeconds));
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    /// <summary>
    /// The seek bar's value. Set by a click, it seeks there; set while <see cref="IsScrubbing"/>, it waits for
    /// <see cref="ApplyPendingScrub"/>, so a drag seeks about once a rendered frame rather than once a pixel.
    /// </summary>
    public double PositionSeconds
    {
        get => Position.TotalSeconds;
        set
        {
            var target = TimeSpan.FromSeconds(value);
            if (IsScrubbing)
            {
                _pendingScrub = target;
                Position = Clamp(target);
            }
            else if (Math.Abs(value - Position.TotalSeconds) > 0.001)
            {
                SeekTo(target);
            }
        }
    }

    /// <summary>The seek bar's maximum: the length in seconds, or 0 when unknown.</summary>
    public double DurationSeconds => Media?.Duration?.TotalSeconds ?? 0;

    /// <summary>"0:03 / 0:10"; the elapsed time alone when the length is unknown.</summary>
    public string TimeText => Media?.Duration is { } duration
        ? $"{Format(Position, duration)} / {Format(duration, duration)}"
        : Format(Position, Position);

    /// <summary>Whether the seek bar's thumb is being dragged; the view sets it.</summary>
    public bool IsScrubbing
    {
        get => _isScrubbing;
        set
        {
            if (Set(ref _isScrubbing, value) && !value)
            {
                ApplyPendingScrub();
            }
        }
    }

    /// <summary>The volume, 0 to 1, as the slider shows it.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, Math.Clamp(value, 0, 1)))
            {
                Player.Volume = _volume;
                VolumeChanged?.Invoke(_volume, _isMuted);
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (Set(ref _isMuted, value))
            {
                Player.IsMuted = value;
                OnPropertyChanged(nameof(MuteGlyph));
                OnPropertyChanged(nameof(MuteName));
                VolumeChanged?.Invoke(_volume, _isMuted);
            }
        }
    }

    /// <summary>The mute button's glyph (Segoe Fluent Icons): Volume, or Mute while muted.</summary>
    public string MuteGlyph => IsMuted ? "" : "";

    /// <summary>The mute button's name for screen readers.</summary>
    public string MuteName => IsMuted ? "Unmute" : "Mute";

    /// <summary>The file's Tracks, in file order (story 14.4).</summary>
    public ObservableCollection<PlayerTrack> Tracks { get; } = [];

    /// <summary>Whether the Track list shows: an open, playable file with at least one Track.</summary>
    public bool HasTracks => HasControls && Tracks.Count > 0;

    /// <summary>
    /// The Track that plays. Choosing another switches to it mid-play (AD-27: one at a time); the position
    /// doesn't change, and Windows leaves a gap of about 0.1 s.
    /// </summary>
    public PlayerTrack? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (value is null || IsClosed || !HasControls || ReferenceEquals(value, _selectedTrack) || !Tracks.Contains(value) || !value.IsAvailable)
            {
                // Re-raised so a list that tried to clear or change it shows the Track that plays.
                OnPropertyChanged();
                return;
            }

            Player.SelectedTrack = value.Index;
            ShowSelected(value);
        }
    }

    /// <summary>Whether the page has been closed; a late frame or event is then ignored.</summary>
    public bool IsClosed { get; private set; }

    public ICommand PlayPauseCommand { get; }

    public ICommand MuteCommand { get; }

    /// <summary>"Open in default player", offered when the page can't play the file.</summary>
    public ICommand OpenInDefaultPlayerCommand { get; }

    /// <summary>Opens the missing extension's Microsoft Store page.</summary>
    public ICommand OpenStoreCommand { get; }

    /// <summary>Opens the file, then shows its first frame, paused, with the first Track.</summary>
    public async Task OpenAsync()
    {
        try
        {
            Player.Volume = _volume;
            Player.IsMuted = _isMuted;
            _stream = _openRead(Path);
            var media = await Player.OpenAsync(_stream);
            if (IsClosed)
            {
                return;
            }

            _log($"player: opened {System.IO.Path.GetFileName(Path)}: video {Describe(media.VideoCodec)} {(media.VideoDecodable ? "decodable" : "NOT decodable")}, "
                + $"{media.TrackNames.Count} tracks, {media.TracksPlayable?.Count(p => !p) ?? 0} not decodable");
            if (!media.VideoDecodable)
            {
                // Windows can't decode the picture: say which extension is missing, never a black page (14.6).
                Media = media;
                Fail(CodecExtension.For(media.VideoCodec) is { } extension
                    ? (Missing(extension), extension)
                    : (UnknownCodecText, null));
                return;
            }

            if (media.TrackNames.Count > 0)
            {
                Player.SelectedTrack = 0;
            }

            Player.ShowFirstFrame();
            for (var i = 0; i < media.TrackNames.Count; i++)
            {
                Tracks.Add(new PlayerTrack(i, media.TrackNames[i], media.TracksPlayable is not { } playable || i >= playable.Count || playable[i]));
            }

            Media = media;
            State = PlayerState.Paused;
            if (Tracks.Count > 0)
            {
                ShowSelected(Tracks[0]);
            }
        }
#pragma warning disable CA1031 // Any failure to open is the page's plain message, never a crash.
        catch (Exception e) when (!IsClosed)
#pragma warning restore CA1031
        {
            _log($"player: couldn't open {System.IO.Path.GetFileName(Path)}: {e.GetType().Name}: {e.Message}");
            Fail((FailedText, null));
        }
#pragma warning disable CA1031 // A failure after the page closed has nothing to show.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Moves to <paramref name="target"/>, clamped to the file; the paused picture follows. A no-op with no known length.</summary>
    public void SeekTo(TimeSpan target)
    {
        if (!CanSeek || IsClosed)
        {
            return;
        }

        var position = Clamp(target);
        Player.Seek(position);
        _atEnd = false;
        Position = position;
    }

    /// <summary>Left and Right: <see cref="SeekStep"/> back or forward.</summary>
    public void SeekBy(TimeSpan delta) => SeekTo(Position + delta);

    /// <summary>Up and Down: <see cref="VolumeStep"/> up or down; a change unmutes.</summary>
    public void ChangeVolume(double delta)
    {
        Volume += delta;
        IsMuted = false;
    }

    /// <summary>Seeks to the drag's latest position, if it moved since the last; the view calls it once a rendered frame while dragging.</summary>
    public void ApplyPendingScrub()
    {
        if (_pendingScrub is { } target)
        {
            _pendingScrub = null;
            SeekTo(target);
        }
    }

    /// <summary>Reads the player's position for the seek bar and time; the view calls it while playing. Ignored while dragging.</summary>
    public void RefreshPosition()
    {
        if (!IsScrubbing && !IsClosed && State == PlayerState.Playing)
        {
            Position = Clamp(Player.Position);
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

    /// <summary>The picture can't be shown (the GPU refused the video surface): pauses and shows the plain failure line.</summary>
    public void ShowFailure()
    {
        if (IsClosed || State == PlayerState.Failed)
        {
            return;
        }

        Player.Pause();
        _log($"player: no picture for {System.IO.Path.GetFileName(Path)}");
        Fail((FailedText, null));
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

    // The plain words for a missing extension (story 14.6, epic 10's glossary).
    private static string Missing(CodecExtension extension) =>
        $"This video is {extension.Codec}, which Windows can't play without the {extension.Name}. "
        + "Get it from the Microsoft Store, then open the file again. The file itself is fine.";

    private static string Describe(string codec) => string.IsNullOrEmpty(codec) ? "(none)" : codec;

    private void Fail((string Text, CodecExtension? Missing) why)
    {
        (_failure, _missing) = why;
        State = PlayerState.Failed;
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(StoreLinkText));
        OnPropertyChanged(nameof(HasStoreLink));
    }

    // Marks the Track that plays, in the list and on each item.
    private void ShowSelected(PlayerTrack track)
    {
        foreach (var each in Tracks)
        {
            each.IsPlaying = ReferenceEquals(each, track);
        }

        _selectedTrack = track;
        OnPropertyChanged(nameof(SelectedTrack));
    }

    // m:ss, or h:mm:ss when the file runs an hour or more.
    internal static string Format(TimeSpan time, TimeSpan length)
    {
        var whole = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, time.TotalSeconds)));
        return length.TotalHours >= 1
            ? whole.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : $"{(int)whole.TotalMinutes}:{whole.Seconds:00}";
    }

    // Read-only, shared for read, write and delete, so the engine's rename, delete and Cleanup never fail on it (AD-27).
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private TimeSpan Clamp(TimeSpan time) =>
        time < TimeSpan.Zero ? TimeSpan.Zero : Media?.Duration is { } duration && time > duration ? duration : time;

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
            Position = Clamp(Player.Position);
        }
        else if (State == PlayerState.Paused)
        {
            // From the end, Play starts again from the beginning.
            if (_atEnd)
            {
                _atEnd = false;
                Player.Seek(TimeSpan.Zero);
                Position = TimeSpan.Zero;
            }

            Player.Play();
            State = PlayerState.Playing;
        }
    }

    // At the end it stops on the last frame, the seek bar full.
    private void OnEnded() => OnUi(() =>
    {
        if (IsClosed || State != PlayerState.Playing)
        {
            return;
        }

        _atEnd = true;
        State = PlayerState.Paused;
        Position = Media?.Duration ?? Clamp(Player.Position);
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
