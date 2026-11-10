using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Encore.Platform.Desktop;

namespace Encore.App.Library;

/// <summary>One Library item's player page: opens the file shared, plays one Track at a time. UI thread; docs/code-notes.md.</summary>
internal sealed class PlayerViewModel : Observable, IDisposable
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
    private bool _isFullScreen;

    /// <summary>The page owns and disposes the player; the other optional arguments replace shell calls for tests.</summary>
    public PlayerViewModel(
        string path,
        string title,
        IPlayer player,
        Func<string, Stream>? openRead = null,
        Action<string>? openInDefaultPlayer = null,
        Action<string>? openLink = null,
        Action<string>? log = null,
        bool embedded = false)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Title = title ?? "";
        Player = player ?? throw new ArgumentNullException(nameof(player));
        SupportsFullScreen = !embedded;
        SupportsExternalActions = !embedded;
        _openRead = openRead ?? OpenShared;
        _openInDefaultPlayer = openInDefaultPlayer ?? (_ => { });
        _openLink = openLink ?? (_ => { });
        _log = log ?? (_ => { });
        PlayPauseCommand = new RelayCommand(PlayPause, () => State is PlayerState.Paused or PlayerState.Playing);
        MuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        FullScreenCommand = new RelayCommand(() => IsFullScreen = !IsFullScreen, () => SupportsFullScreen);
        OpenInDefaultPlayerCommand = new RelayCommand(() => _openInDefaultPlayer(Path), () => SupportsExternalActions);
        CreateClipCommand = new RelayCommand(StartCreateClip, () => !IsClosed && CreateClip is not null);
        OpenStoreCommand = new RelayCommand(() =>
        {
            if (SupportsExternalActions && _missing is { } extension)
            {
                _openLink(extension.StoreLink);
            }
        }, () => SupportsExternalActions);
        Player.Ended += OnEnded;
    }

    /// <summary>The volume or mute changed: the Library keeps them.</summary>
    public event Action<double, bool>? VolumeChanged;

    /// <summary>The file's absolute path.</summary>
    public string Path { get; }
    private Action? _createClip;
    public Action? CreateClip
    {
        get => _createClip;
        set { _createClip = value; OnPropertyChanged(nameof(HasCreateClip)); ((RelayCommand)CreateClipCommand).RaiseCanExecuteChanged(); }
    }
    public bool HasCreateClip => CreateClip is not null;
    public ICommand CreateClipCommand { get; }

    /// <summary>The item's name.</summary>
    public string Title { get; }

    /// <summary>The file's name, for the log.</summary>
    private string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The player; the view copies its frames.</summary>
    public IPlayer Player { get; }
    public bool SupportsFullScreen { get; }
    public bool SupportsExternalActions { get; }
    public bool ShowsDefaultPlayerAction => SupportsExternalActions && IsFailed;

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
                OnPropertyChanged(nameof(ShowsDefaultPlayerAction));
                OnPropertyChanged(nameof(PlayPauseLabel));
                OnPropertyChanged(nameof(PlayPauseGlyph));
                OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(StoreLinkText));
                OnPropertyChanged(nameof(HasStoreLink));
                OnPropertyChanged(nameof(CanSeek));
                OnPropertyChanged(nameof(HasControls));
                ((RelayCommand)PlayPauseCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlaying => State == PlayerState.Playing;

    public bool IsFailed => State == PlayerState.Failed;

    /// <summary>Whether the transport shows: once the file is open and playable.</summary>
    public bool HasControls => State is PlayerState.Paused or PlayerState.Playing;

    /// <summary>The play button's name: "Play" or "Pause".</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>The play button's glyph (Segoe Fluent Icons): Play, or Pause while playing.</summary>
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";

    /// <summary>Whether the player page fills the screen; the button or F toggles it, and Esc or closing the page leaves it.</summary>
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            if (Set(ref _isFullScreen, value && !IsClosed && SupportsFullScreen))
            {
                OnPropertyChanged(nameof(FullScreenGlyph));
                OnPropertyChanged(nameof(FullScreenName));
            }
        }
    }

    /// <summary>The full-screen button's glyph: Full screen, or Back to window while in it.</summary>
    public string FullScreenGlyph => IsFullScreen ? "\uE73F" : "\uE740";

    /// <summary>The full-screen button's name for screen readers and its tooltip.</summary>
    public string FullScreenName => IsFullScreen ? "Exit full screen" : "Full screen";

    /// <summary>The line over the video while it opens or when it can't play; null otherwise.</summary>
    public string? Message => State switch
    {
        PlayerState.Opening => SupportsExternalActions ? "Opening…" : null,
        PlayerState.Failed => _failure ?? FailedText,
        _ => null,
    };

    /// <summary>The Store link's text when an extension is missing: "Get the HEVC Video Extension"; null otherwise.</summary>
    public string? StoreLinkText => SupportsExternalActions && IsFailed && _missing is { } extension ? $"Get the {extension.Name}" : null;

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

    /// <summary>The seek bar's value: a click seeks there; during a drag it waits, so a drag seeks about once per frame.</summary>
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

    /// <summary>The volume, 0 to 1, rounded to two places; any request while muted unmutes. See docs/code-notes.md.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var rounded = Math.Round(Math.Clamp(value, 0, 1), 2);
            var changed = rounded != _volume;
            _volume = rounded;
            OnPropertyChanged();
            if (changed)
            {
                Player.Volume = _volume;
            }

            var unmuted = _isMuted;
            if (unmuted)
            {
                _isMuted = false;
                Player.IsMuted = false;
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(MuteGlyph));
                OnPropertyChanged(nameof(MuteName));
            }

            if (changed || unmuted)
            {
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
    public string MuteGlyph => IsMuted ? "\uE74F" : "\uE767";

    /// <summary>The mute button's name for screen readers.</summary>
    public string MuteName => IsMuted ? "Unmute" : "Mute";

    /// <summary>The file's Tracks, in file order.</summary>
    public ObservableCollection<PlayerTrack> Tracks { get; } = [];

    /// <summary>The Track that plays; choosing another switches mid-play without moving the position.</summary>
    public PlayerTrack? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (value is null || IsClosed || !HasControls || ReferenceEquals(value, _selectedTrack)
                || !Tracks.Contains(value) || !value.IsAvailable)
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

    /// <summary>Optional creator-owned live range; ordinary Library players remain non-looping.</summary>
    internal Func<(TimeSpan Start, TimeSpan End)?>? PlaybackLoop { get; set; }

    private (TimeSpan Start, TimeSpan End)? LoopRange => IsScrubbing ? null : PlaybackLoop?.Invoke();

    public ICommand MuteCommand { get; }

    /// <summary>The full-screen button and F: toggles <see cref="IsFullScreen"/>.</summary>
    public ICommand FullScreenCommand { get; }

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
            if (!IsClosed)
            {
                ShowOpened(media);
            }
        }
#pragma warning disable CA1031 // Any failure to open is the page's plain message, never a crash.
        catch (Exception e) when (!IsClosed)
#pragma warning restore CA1031
        {
            _log($"player: couldn't open {FileName}: {e.GetType().Name}: {e.Message}");
            Fail((FailedText, null));
        }
#pragma warning disable CA1031 // A failure after the page closed has nothing to show.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // The opened file: its first frame, paused, with its Tracks; or which extension is missing.
    private void ShowOpened(PlayerMedia media)
    {
        var video = media.VideoDecodable ? "decodable" : "NOT decodable";
        var undecodableTracks = media.TracksPlayable?.Count(playable => !playable) ?? 0;
        _log($"player: opened {FileName}: video {Describe(media.VideoCodec)} {video}, "
            + $"{media.TrackNames.Count} tracks, {undecodableTracks} not decodable");
        if (!media.VideoDecodable)
        {
            // Windows can't decode the picture: say which extension is missing, never a black page.
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
            var isAvailable = media.TracksPlayable is not { } playable || i >= playable.Count || playable[i];
            Tracks.Add(new PlayerTrack(i, media.TrackNames[i], isAvailable));
        }

        Media = media;
        State = PlayerState.Paused;
        if (Tracks.Count > 0)
        {
            ShowSelected(Tracks[0]);
        }
    }

    // Create clip, from the player: it stops playing and leaves full screen first.
    private void StartCreateClip()
    {
        Pause();
        IsFullScreen = false;
        CreateClip?.Invoke();
    }

    /// <summary>Moves to a position, clamped to the file; the paused picture follows. A no-op with no known length.</summary>
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

    /// <summary>Up and Down: <see cref="VolumeStep"/> up or down; while muted it unmutes, even at the end of the range.</summary>
    public void ChangeVolume(double delta) => Volume += delta;

    /// <summary>Seeks to the drag's latest position, if it moved; the view calls it once a rendered frame while dragging.</summary>
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
            var position = Clamp(Player.Position);
            if (LoopRange is { } loop && (position < loop.Start || position >= loop.End)) { SeekTo(loop.Start); }
            else { Position = position; }
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
        _log($"player: no picture for {FileName}");
        Fail((FailedText, null));
    }

    /// <summary>Runs the page's shortcut for a key pressed with no modifier. False when the key isn't one.</summary>
    public bool HandleKey(Key key)
    {
        if (IsClosed)
        {
            return false;
        }

        switch (key)
        {
            case Key.Space:
                PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                SeekBy(-SeekStep);
                break;
            case Key.Right:
                SeekBy(SeekStep);
                break;
            case Key.Up:
                ChangeVolume(VolumeStep);
                break;
            case Key.Down:
                ChangeVolume(-VolumeStep);
                break;
            case Key.F when SupportsFullScreen:
                FullScreenCommand.Execute(null);
                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>One present's work: get the surface, copy the frame into it, show it. A surface failure fails the page.</summary>
    public void DrawFrame(Func<VideoSurface?> ensureSurface, Action<VideoSurface> show, Action release)
    {
        VideoSurface? surface;
        try
        {
            surface = ensureSurface();
        }
#pragma warning disable CA1031 // Any GPU failure here fails the page; a narrower catch missed OutOfMemoryException.
        catch (Exception)
#pragma warning restore CA1031
        {
            release();
            ShowFailure();
            return;
        }

        if (surface is null)
        {
            return;
        }

        try
        {
            Player.CopyFrame(surface);
        }
#pragma warning disable CA1031 // A frame the player can't copy now (closing, device lost) is skipped, never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        show(surface);
    }

    /// <summary>Stops the player and releases the file; safe to call twice.</summary>
    public void Dispose()
    {
        if (IsClosed)
        {
            return;
        }

        // Leaving the page leaves full screen.
        IsFullScreen = false;
        IsClosed = true;
        PlaybackLoop = null;
        Player.Ended -= OnEnded;
        Player.Dispose();
        _stream?.Dispose();
        _stream = null;
    }

    // The plain words for a missing extension.
    private static string Missing(CodecExtension extension) =>
        $"This video is {extension.Codec}, which Windows can't play without the {extension.Name}. "
        + "Get it from the Microsoft Store, then open the file again. The file itself is fine.";

    private static string Describe(string codec) => string.IsNullOrEmpty(codec) ? "(none)" : codec;

    private void Fail((string Text, CodecExtension? Missing) why)
    {
        // Set before the state, whose change raises Message and the Store link's properties.
        (_failure, _missing) = why;
        State = PlayerState.Failed;
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

    // Read-only, shared for read, write and delete, so the engine's rename, delete and Cleanup never fail on it.
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
            if (LoopRange is { } loop && (_atEnd || Position < loop.Start || Position >= loop.End))
            {
                SeekTo(loop.Start);
            }
            // From the end, Play starts again from the beginning.
            else if (_atEnd)
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

        if (LoopRange is { } loop)
        {
            SeekTo(loop.Start);
            Player.Play();
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
