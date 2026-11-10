using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Replay;

/// <summary>
/// The bytes a Rewind of a Buffer Window holds at worst (R3): the encoders' rate over the
/// window plus 3 s (<see cref="PacketRing.SlackUs"/> and one GOP), plus two slabs of headroom. VBR
/// counts at 1.5 times its bitrate. The Disk-mode ring file is sized from it.
/// </summary>
public static class ReplayStorageEstimate
{
    /// <summary>One Track's AAC bitrate, as the encoder sets it.</summary>
    public const int AacBitsPerSecond = 192_000;

    private const int ExtraSeconds = 3;

    /// <param name="video">The snapshot's video settings.</param>
    /// <param name="tracks">The session's Tracks.</param>
    /// <param name="windowSeconds">The Buffer Window, in s.</param>
    public static long Bytes(VideoSettings video, int tracks, int windowSeconds)
    {
        ArgumentNullException.ThrowIfNull(video);
        var videoBits = video.BitrateKbps * 1000L;
        if (video.RateControl == RateControl.Vbr)
        {
            videoBits = videoBits * 3 / 2;
        }

        var bitsPerSecond = videoBits + ((long)tracks * AacBitsPerSecond);
        return (bitsPerSecond / 8 * (windowSeconds + ExtraSeconds)) + (2L * PacketRing.DefaultSlabBytes);
    }

    /// <summary>The estimate of the Global settings <paramref name="global"/> (R2), against <paramref name="freeBytes"/>.</summary>
    /// <param name="global">The Global effective settings.</param>
    /// <param name="freeBytes">The free RAM for Memory, the ring drive's free space for Disk; null when unknown, which warns of nothing.</param>
    public static StorageEstimate For(EffectiveSettings global, long? freeBytes)
    {
        ArgumentNullException.ThrowIfNull(global);
        var bytes = Bytes(global.Video, global.TrackLayout.Tracks.Count, global.BufferWindow);
        return new StorageEstimate(
            global.ReplayStorage,
            bytes,
            WorstCase: global.Video.RateControl == RateControl.Vbr,
            freeBytes,
            ExceedsFree: freeBytes is { } free && bytes > free);
    }
}
