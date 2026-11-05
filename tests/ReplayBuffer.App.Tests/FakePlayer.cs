using System.IO;
using ReplayBuffer.App.Library;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

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

    public TimeSpan Position { get; set; }

    public double Volume { get; set; } = 1.0;

    public bool IsMuted { get; set; }

    /// <summary>Every seek the page asked for, in order.</summary>
    public List<TimeSpan> Seeks { get; } = [];

    public void Seek(TimeSpan position)
    {
        Seeks.Add(position);
        Position = position;
    }

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

    /// <summary>CopyFrame throws this, as a lost device does.</summary>
    public Exception? CopyFails { get; set; }

    public void CopyFrame(VideoSurface target)
    {
        if (CopyFails is { } failure)
        {
            throw failure;
        }
    }

    public void Dispose() => Disposed = true;

    public void RaiseFrame() => FrameAvailable?.Invoke();

    public void RaiseEnded() => Ended?.Invoke();
}
