using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Replay;

namespace ReplayBuffer.Core.Tests;

public sealed class DiskRingTests : IDisposable
{
    private const long Second = 1_000_000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-disk-ring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ADiskRingCutsWhatAMemoryRingCutsAndDeletesItsFileOnceItsLastCutIsDisposed()
    {
        const int block = 64 * 1024;
        using var memory = new PacketRing(10 * Second, capacity: 64, slabBytes: block);
        var disk = new PacketRing(10 * Second, capacity: 64, RingFile.Create(_folder, 40L * block, block));
        var path = disk.File!.Path;
        Assert.StartsWith(RingFile.Prefix, Path.GetFileName(path));
        foreach (var ring in new[] { memory, disk })
        {
            var (video, audio) = PacketRingTests.Streams(ring);
            PacketRingTests.Feed(ring, video, audio, seconds: 30, videoBytes: 3000);
        }

        Assert.Equal(0, disk.MemorySlabs);
        var presses = new[] { (20 * Second) + 400_000, (29 * Second) + 100_000 };
        var cuts = new List<RingCut>();
        foreach (var press in presses)
        {
            using var expected = memory.Cut(press - (8 * Second), press)!;
            var actual = disk.Cut(press - (8 * Second), press)!;
            cuts.Add(actual);
            Assert.Equal(expected.StartUs, actual.StartUs);
            Assert.Equal(
                expected.Packets.Select(p => (p.Stream, p.Pts, p.Dts, p.Duration, p.IsKey, p.TimeUs, p.Length)),
                actual.Packets.Select(p => (p.Stream, p.Pts, p.Dts, p.Duration, p.IsKey, p.TimeUs, p.Length)));
            Assert.All(expected.Packets.Zip(actual.Packets), pair => Assert.Equal(pair.First.ToArray(), pair.Second.ToArray()));
        }

        // Disposed while cuts pin it: their bytes stay, and the file with them, until the last goes.
        disk.Dispose();
        Assert.True(File.Exists(path));
        var held = cuts[^1];
        Assert.All(held.Packets, p => Assert.Equal(PacketRingTests.Pattern(p.Stream, p.Pts, p.Length), p.ToArray()));
        cuts[0].Dispose();
        Assert.True(File.Exists(path));
        held.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ATenMinuteCbrRunStaysOnDiskAndWithinTheEstimate()
    {
        const int window = 30;
        var video = new VideoSettings(VideoCodec.H264, 1920, 1080, 30, 2000, RateControl.Cbr);
        var estimate = ReplayStorageEstimate.Bytes(video, tracks: 1, window);
        var ring = new PacketRing(window * Second, capacity: 2600, RingFile.Create(_folder, estimate));
        var path = ring.File!.Path;
        var (v, a) = PacketRingTests.Streams(ring);

        // 2 Mbps of video at 30 fps; the helper's audio is under the estimate's AAC rate.
        PacketRingTests.Feed(ring, v, a, seconds: 600, videoBytes: 2_000_000 / 8 / 30);

        Assert.Equal(0, ring.MemorySlabs);
        Assert.InRange(new FileInfo(path).Length, 1, estimate);
        ring.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RemoveStaleDeletesLeftRingFilesOnly()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "ring-7.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_folder, "other.bin"), [1, 2, 3]);

        Assert.Equal(1, RingFile.RemoveStale(_folder));

        Assert.False(File.Exists(Path.Combine(_folder, "ring-7.bin")));
        Assert.True(File.Exists(Path.Combine(_folder, "other.bin")));
    }
}
