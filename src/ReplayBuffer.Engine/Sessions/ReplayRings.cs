using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>Makes a session's Replay Buffer rings: on disk (R3) when asked and the file can be made, else in memory.</summary>
internal static class ReplayRings
{
    /// <summary>
    /// An empty ring of a <paramref name="windowUs"/> Buffer Window. With a
    /// <paramref name="diskFolder"/>, a Disk-mode ring on a new <see cref="RingFile"/> there of
    /// <paramref name="diskBytes"/> at most, at low I/O priority; when that file can't be made, a
    /// warning and a Memory-mode ring. Null <paramref name="diskFolder"/> is Memory mode. Either way
    /// its native-memory slabs are checked against <paramref name="margin"/>, if set.
    /// </summary>
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
            log.LogWarning(e, "The Replay Buffer's ring file couldn't be made in '{Folder}'; buffering in memory", diskFolder);
            return new PacketRing(windowUs, capacity, margin: margin);
        }
    }

    /// <summary>
    /// Whether a Disk-mode ring of <paramref name="snapshot"/> fits its drive now (R5): its
    /// <see cref="ReplayStorageEstimate"/> against the free bytes of its folder's drive
    /// (<see cref="EffectiveSettings.RingPath"/>, else <paramref name="defaultFolder"/>). Unknown free
    /// space counts as fits.
    /// </summary>
    public static bool Fits(EffectiveSettings snapshot, string defaultFolder, Func<string, long?> freeDisk)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(freeDisk);
        var free = freeDisk(snapshot.RingPath ?? defaultFolder);
        return free is not { } bytes
            || ReplayStorageEstimate.Bytes(snapshot.Video, snapshot.TrackLayout.Tracks.Count, snapshot.BufferWindow) <= bytes;
    }
}
