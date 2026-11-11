using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts.Settings;
using Encore.Core.Replay;
using Encore.Core.Settings;
using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

public sealed class ReplayRingsTests
{
    [Fact]
    public void ARingPreallocatesItsWindowAndSlackOfVideoAndAudioPackets()
    {
        // 60 video and 2 x 47 AAC packets a second, plus one, over the 30 s window and 3 s of slack.
        Assert.Equal(33 * (60 + 94 + 1), ReplayRings.Capacity(fps: 60, tracks: 2, seconds: 30));
    }

    [Fact]
    public void ARingFolderThatCantBeMadeGivesAMemoryRing()
    {
        // A file where the folder should be: the ring file can't be created under it.
        var blocker = Path.GetTempFileName();
        try
        {
            using var ring = ReplayRings.Create(30_000_000, 64, blocker, 16L * 1024 * 1024, margin: null, NullLogger.Instance);

            Assert.Null(ring.File);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void ARingFitsWhenItsDriveHasRoomForItsEstimateOrTheRoomIsUnknown()
    {
        var snapshot = SettingsResolver.Resolve(SettingsDocument.Empty, null);
        var bytes = ReplayStorageEstimate.Bytes(snapshot.Video, snapshot.TrackLayout.Tracks.Count, snapshot.BufferWindow);
        var asked = new List<string>();

        Assert.True(ReplayRings.Fits(snapshot, "default", folder =>
        {
            asked.Add(folder);
            return bytes;
        }));
        Assert.False(ReplayRings.Fits(snapshot, "default", _ => bytes - 1));
        Assert.True(ReplayRings.Fits(snapshot, "default", _ => null));
        Assert.Equal(["default"], asked);
    }
}
