using System.IO;
using Encore.Platform.Desktop;

namespace Encore.App.Library;

/// <summary>The in-app player's seam: Windows' media player in the App, a fake in tests. Events may arrive on any thread.</summary>
internal interface IPlayer : IDisposable
{
    /// <summary>Raised when a new frame can be copied; on any thread, possibly at the display's refresh rate.</summary>
    event Action? FrameAvailable;

    /// <summary>Raised when playback reaches the end; on any thread.</summary>
    event Action? Ended;

    bool IsPlaying { get; }

    TimeSpan Position { get; }

    /// <summary>The volume, 0 to 1.</summary>
    double Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>The audio Track that plays, by index in file order; one plays at a time.</summary>
    int SelectedTrack { get; set; }

    /// <summary>Opens the file from <paramref name="file"/>, paused; throws <see cref="PlayerException"/> when it can't.</summary>
    Task<PlayerMedia> OpenAsync(Stream file);

    /// <summary>Moves to <paramref name="position"/>, playing or paused; paused, that frame is delivered.</summary>
    void Seek(TimeSpan position);

    /// <summary>Shows the first frame while paused.</summary>
    void ShowFirstFrame();

    void Play();

    void Pause();

    /// <summary>Copies the current frame into <paramref name="target"/>, scaled to its size.</summary>
    void CopyFrame(VideoSurface target);
}
