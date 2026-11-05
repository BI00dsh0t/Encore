using System.IO;
using ReplayBuffer.Platform.Desktop;
using Windows.Media.Core;
using Windows.Media.Playback;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;

namespace ReplayBuffer.App.Library;

/// <summary>
/// Windows' own media player (WinRT <c>Windows.Media.Playback.MediaPlayer</c>, Media Foundation) in frame-server
/// mode (AD-27): it never draws itself; each frame is copied into the page's <see cref="VideoSurface"/>.
/// <para>
/// A Track switch while paused makes Media Foundation's next Play resume from the start, though a paused
/// seek after it shows the right frame (the 14.5 probe). So the first Track is chosen as the Tracks are
/// first listed, before the file opens, with no switch. And after a switch while paused, Play moves back
/// to the paused position.
/// </para>
/// </summary>
internal sealed class WindowsMediaPlayer : IPlayer
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    // The frame server is on before any source is set (AD-27).
    private readonly MediaPlayer _player = new() { AutoPlay = false, IsVideoFrameServerEnabled = true };
    private MediaPlaybackItem? _item;
    private TimeSpan? _resumeAt;
    private readonly string? _forcedUnsupported;
    private volatile bool _disposed;

    /// <param name="forcedUnsupported">
    /// A codec ("HEVC" or "AV1") to report as undecodable, whatever the file holds: the dev-instance switch
    /// <c>--player-unsupported</c>, for checking the page Windows shows without the extension (story 14.6). Null in a normal run.
    /// </param>
    public WindowsMediaPlayer(string? forcedUnsupported = null)
    {
        _forcedUnsupported = forcedUnsupported;
        _player.VideoFrameAvailable += OnFrameAvailable;
        _player.MediaEnded += OnMediaEnded;
    }

    public event Action? FrameAvailable;

    public event Action? Ended;

    public bool IsMuted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public bool IsPlaying => !_disposed && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public TimeSpan Position => _disposed ? TimeSpan.Zero : _player.PlaybackSession.Position;

    public int SelectedTrack
    {
        get => _item?.AudioTracks.SelectedIndex ?? -1;
        set
        {
            if (_item is not { } item || value < 0 || value >= item.AudioTracks.Count || value == item.AudioTracks.SelectedIndex)
            {
                return;
            }

            if (!IsPlaying)
            {
                _resumeAt = Position;
            }

            item.AudioTracks.SelectedIndex = value;
        }
    }

    public async Task<PlayerMedia> OpenAsync(Stream file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(MediaPlayer sender, object args) => opened.TrySetResult();
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
            opened.TrySetException(new PlayerException($"{args.Error}: {args.ErrorMessage}"));

        // The first Track, chosen as the Tracks are listed: no switch at open, which would break the next resume (AD-27).
        static void FirstTrack(MediaPlaybackItem item, Windows.Foundation.Collections.IVectorChangedEventArgs args)
        {
            if (item.AudioTracks.Count > 0 && item.AudioTracks.SelectedIndex != 0)
            {
                item.AudioTracks.SelectedIndex = 0;
            }
        }

        _player.MediaOpened += OnOpened;
        _player.MediaFailed += OnFailed;
        MediaPlaybackItem? item = null;
        try
        {
            // A stream, never a path: the App's own read-only handle, shared for delete (AD-27).
            item = new MediaPlaybackItem(MediaSource.CreateFromStream(file.AsRandomAccessStream(), "video/mp4"));
            item.AudioTracksChanged += FirstTrack;
            _item = item;
            _player.Source = item;
            await opened.Task.WaitAsync(OpenTimeout);
        }
        catch (TimeoutException e)
        {
            throw new PlayerException("The file didn't open in time.", e);
        }
        finally
        {
            _player.MediaOpened -= OnOpened;
            _player.MediaFailed -= OnFailed;
            if (item is not null)
            {
                item.AudioTracksChanged -= FirstTrack;
            }
        }

        var session = _player.PlaybackSession;
        var duration = session.NaturalDuration;
        var video = item.VideoTracks.Count > 0 ? item.VideoTracks[0] : null;
        static bool Playable(MediaDecoderStatus status) => status is MediaDecoderStatus.FullySupported or MediaDecoderStatus.Degraded;
        return new PlayerMedia(
            (int)session.NaturalVideoWidth,
            (int)session.NaturalVideoHeight,
            duration > TimeSpan.Zero && duration < TimeSpan.FromDays(1) ? duration : null,
            item.AudioTracks.Select(t => t.Name).ToList(),
            _forcedUnsupported is null && video is not null && Playable(video.SupportInfo.DecoderStatus),
            _forcedUnsupported ?? video?.GetEncodingProperties().Subtype ?? "",
            item.AudioTracks.Select(t => Playable(t.SupportInfo.DecoderStatus)).ToList());
    }

    // Paused, a position set delivers that frame through the frame server (the 14.2 proof, the 14.5 probe).
    public void Seek(TimeSpan position)
    {
        _player.PlaybackSession.Position = position;
        if (_resumeAt is not null)
        {
            _resumeAt = position;
        }
    }

    public void ShowFirstFrame() => Seek(TimeSpan.Zero);

    public void Play()
    {
        _player.Play();
        if (_resumeAt is { } at)
        {
            // A Track was switched while paused: without this, Media Foundation resumes from the start.
            _resumeAt = null;
            _player.PlaybackSession.Position = at;
        }
    }

    public void Pause() => _player.Pause();

    public void CopyFrame(VideoSurface target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_disposed)
        {
            _player.CopyFrameToVideoSurface(target.Direct3DSurface);
        }
    }

    // Frames stop first, then the source goes, then the player (the 14.2 proof's close order).
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.VideoFrameAvailable -= OnFrameAvailable;
        _player.MediaEnded -= OnMediaEnded;
        _player.IsVideoFrameServerEnabled = false;
        _player.Pause();
        _player.Source = null;
        _player.Dispose();
    }

    private void OnFrameAvailable(MediaPlayer sender, object args)
    {
        if (!_disposed)
        {
            FrameAvailable?.Invoke();
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (!_disposed)
        {
            Ended?.Invoke();
        }
    }
}
