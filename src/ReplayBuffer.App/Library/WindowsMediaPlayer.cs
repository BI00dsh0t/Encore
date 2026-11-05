using System.IO;
using ReplayBuffer.Platform.Desktop;
using Windows.Media.Core;
using Windows.Media.Playback;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;

namespace ReplayBuffer.App.Library;

/// <summary>
/// Windows' own media player (WinRT <c>Windows.Media.Playback.MediaPlayer</c>, Media Foundation) in frame-server
/// mode (AD-27): it never draws itself; each frame is copied into the page's <see cref="VideoSurface"/>.
/// </summary>
internal sealed class WindowsMediaPlayer : IPlayer
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    // The frame server is on before any source is set (AD-27).
    private readonly MediaPlayer _player = new() { AutoPlay = false, IsVideoFrameServerEnabled = true };
    private MediaPlaybackItem? _item;
    private volatile bool _disposed;

    public WindowsMediaPlayer()
    {
        _player.VideoFrameAvailable += OnFrameAvailable;
        _player.MediaEnded += OnMediaEnded;
    }

    public event Action? FrameAvailable;

    public event Action? Ended;

    /// <summary>Mutes the player: the Heavy playback tests never make a sound.</summary>
    public bool IsMuted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }

    public bool IsPlaying => !_disposed && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public TimeSpan Position => _disposed ? TimeSpan.Zero : _player.PlaybackSession.Position;

    public int SelectedTrack
    {
        get => _item?.AudioTracks.SelectedIndex ?? -1;
        set
        {
            if (_item is { } item && value >= 0 && value < item.AudioTracks.Count)
            {
                item.AudioTracks.SelectedIndex = value;
            }
        }
    }

    public async Task<PlayerMedia> OpenAsync(Stream file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(MediaPlayer sender, object args) => opened.TrySetResult();
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
            opened.TrySetException(new PlayerException($"{args.Error}: {args.ErrorMessage}"));

        _player.MediaOpened += OnOpened;
        _player.MediaFailed += OnFailed;
        try
        {
            // A stream, never a path: the App's own read-only handle, shared for delete (AD-27).
            _item = new MediaPlaybackItem(MediaSource.CreateFromStream(file.AsRandomAccessStream(), "video/mp4"));
            _player.Source = _item;
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
        }

        var session = _player.PlaybackSession;
        var duration = session.NaturalDuration;
        var video = _item.VideoTracks.Count > 0 ? _item.VideoTracks[0] : null;
        return new PlayerMedia(
            (int)session.NaturalVideoWidth,
            (int)session.NaturalVideoHeight,
            duration > TimeSpan.Zero && duration < TimeSpan.FromDays(1) ? duration : null,
            _item.AudioTracks.Select(t => t.Name).ToList(),
            video?.SupportInfo.DecoderStatus == MediaDecoderStatus.FullySupported);
    }

    // Paused, a position set delivers that frame through the frame server (the 14.2 proof).
    public void ShowFirstFrame() => _player.PlaybackSession.Position = TimeSpan.Zero;

    public void Play() => _player.Play();

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
