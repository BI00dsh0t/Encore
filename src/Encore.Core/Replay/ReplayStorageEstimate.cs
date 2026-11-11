using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Replay;

/// <summary>The bytes a Rewind holds at worst: the encoders' rate over the window plus 3 s and two slabs.</summary>
public static class ReplayStorageEstimate
{
    /// <summary>One Track's AAC bitrate, as the encoder sets it.</summary>
    public const int AacBitsPerSecond = 192_000;

    private const int ExtraSeconds = 3;

    /// <summary>The bytes for these video settings, this many Tracks and a Buffer Window in seconds.</summary>
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

    /// <summary>The estimate of the Global settings against the free bytes; unknown free bytes warn of nothing.</summary>
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
