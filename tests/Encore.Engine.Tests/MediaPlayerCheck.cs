using System.Globalization;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Encore.Engine.Tests;

/// <summary>A Recording opened in Windows' media player has ffprobe's length and plays to its end.</summary>
internal static class MediaPlayerCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The player's duration is within one frame of ffprobe's, and played from near the end it reaches the end.</summary>
    public static async Task AssertPlaysToTheEndAsync(string file, double fps, CancellationToken token)
    {
        var frame = 1.0 / fps;
        var probed = double.Parse(
            SessionTestKit.Probe(file, "-show_entries", "format=duration").GetProperty("format").GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var source = MediaSource.CreateFromStream(stream.AsRandomAccessStream(), "video/mp4");
        using var player = new MediaPlayer { AutoPlay = false, IsMuted = true };
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaEnded += (_, _) => ended.TrySetResult();
        player.MediaFailed += (_, e) =>
        {
            var failure = new InvalidOperationException($"MediaPlayer failed on {file}: {e.Error} {e.ErrorMessage}");
            opened.TrySetException(failure);
            ended.TrySetException(failure);
        };

        player.Source = source;
        await opened.Task.WaitAsync(Timeout, token);
        var length = player.PlaybackSession.NaturalDuration.TotalSeconds;
        Assert.InRange(length, probed - frame, probed + frame);

        player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, length - 1.5));
        player.Play();
        await ended.Task.WaitAsync(Timeout, token);
        Assert.InRange(player.PlaybackSession.Position.TotalSeconds, length - frame, length + frame);
    }
}
