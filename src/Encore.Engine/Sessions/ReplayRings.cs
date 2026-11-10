using Microsoft.Extensions.Logging;
using Encore.Contracts.Settings;
using Encore.Core.Replay;
using Encore.Platform.Windows;

namespace Encore.Engine.Sessions;

/// <summary>Makes a session's Rewind rings: on disk when asked and the file can be made, else in memory.</summary>
internal static class ReplayRings
{
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
