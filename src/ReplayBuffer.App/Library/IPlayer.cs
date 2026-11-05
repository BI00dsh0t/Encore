using System.IO;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Library;

/// <summary>
/// The in-app player's seam (AD-27, story 14.2): Windows' media player behind it in the App, a fake in tests.
/// Events may arrive on any thread; everything else is called on the UI thread.
/// </summary>
internal interface IPlayer : IDisposable
{
    /// <summary>Raised when a new frame can be copied with <see cref="CopyFrame"/>; on any thread, possibly at the display's refresh rate.</summary>
    event Action? FrameAvailable;

    /// <summary>Raised when playback reaches the end; on any thread.</summary>
    event Action? Ended;

    /// <summary>Whether it's playing.</summary>
    bool IsPlaying { get; }

    /// <summary>The position in the file.</summary>
    TimeSpan Position { get; }

    /// <summary>The volume, 0 to 1.</summary>
    double Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>The audio Track that plays, by index in file order (AD-27: one at a time).</summary>
    int SelectedTrack { get; set; }

    /// <summary>Opens the file from <paramref name="file"/>, paused; throws <see cref="PlayerException"/> when it can't.</summary>
    Task<PlayerMedia> OpenAsync(Stream file);

    /// <summary>Moves to <paramref name="position"/>, playing or paused; paused, that frame is delivered (story 14.5).</summary>
    void Seek(TimeSpan position);

    /// <summary>Shows the first frame while paused.</summary>
    void ShowFirstFrame();

    void Play();

    void Pause();

    /// <summary>Copies the current frame into <paramref name="target"/>, scaled to its size.</summary>
    void CopyFrame(VideoSurface target);
}

/// <summary>What an opened file holds: its video size, its length when known, its Tracks' names, and whether Windows can decode its video.</summary>
internal sealed record PlayerMedia(int Width, int Height, TimeSpan? Duration, IReadOnlyList<string> TrackNames, bool VideoDecodable);

/// <summary>The file couldn't be opened for playback.</summary>
internal sealed class PlayerException : Exception
{
    public PlayerException()
    {
    }

    public PlayerException(string message)
        : base(message)
    {
    }

    public PlayerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
