using Microsoft.Extensions.Logging;
using Encore.Contracts.Settings;
using Encore.Core.Replay;
using Encore.Platform.Windows;

namespace Encore.Engine.Sessions;

/// <summary>Makes a session's Rewind rings: on disk when asked and the file can be made, else in memory.</summary>
internal static class ReplayRings
{
    /// <summary>AAC packets a second (48 kHz in 1024-sample frames), rounded up, for sizing the ring.</summary>
    private const int AacPacketsPerSecond = 47;

    /// <summary>The packets a ring of <paramref name="seconds"/> preallocates: the window and its slack, capped at 2^24.</summary>
    public static int Capacity(int fps, int tracks, int seconds)
    {
        var perSecond = fps + (tracks * AacPacketsPerSecond) + 1;
        return (int)Math.Min((seconds + 3L) * perSecond, 1 << 24);
    }

    /// <summary>Where a ring keeps its bytes, for the log.</summary>
    public static string StorageOf(PacketRing ring) => ring.File is { } file
        ? $"on disk in {file.Path} ({file.Bytes / (1024 * 1024)} MiB)"
        : "in memory";

    /// <summary>An empty ring for a Buffer Window: on disk when a folder is given, falling back to memory when the file fails.</summary>
    public static PacketRing Create(long windowUs, int capacity, string? diskFolder, long diskBytes, MemoryMargin? margin, ILogger log)
    {
        if (diskFolder is null)
        {
            return new PacketRing(windowUs, capacity, margin: margin);
        }

        try
        {
            return new PacketRing(windowUs, capacity, RingFile.Create(diskFolder, diskBytes, configure: FileIoPriority.SetLow), margin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Rewind's ring file couldn't be made in '{Folder}'; buffering in memory", diskFolder);
            return new PacketRing(windowUs, capacity, margin: margin);
        }
    }

    /// <summary>Whether a Disk-mode ring fits its drive now; unknown free space counts as fits.</summary>
    public static bool Fits(EffectiveSettings snapshot, string defaultFolder, Func<string, long?> freeDisk)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(freeDisk);
        var free = freeDisk(snapshot.RingPath ?? defaultFolder);
        return free is not { } bytes
            || ReplayStorageEstimate.Bytes(snapshot.Video, snapshot.TrackLayout.Tracks.Count, snapshot.BufferWindow) <= bytes;
    }
}
