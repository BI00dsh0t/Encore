using ReplayBuffer.Core.Replay;

namespace ReplayBuffer.Core.Tests;

public sealed class MemoryMarginTests
{
    private const long Second = 1_000_000;
    private const long GiB = 1024L * 1024 * 1024;
    private const int SlabBytes = 16 * 1024;
    private const int VideoBytes = 500;

    [Fact]
    public void InsideTheMarginTheRingStopsGrowingAndTrimsPastAPinnedCutToItsOldestUnpinnedSlabs()
    {
        var status = new FakeMemoryStatus { Total = 32 * GiB, Available = 20 * GiB };
        var margin = new MemoryMargin(status);
        var events = new List<MemoryMarginEvent>();
        margin.Raised += events.Add;
        using var ring = new PacketRing(60 * Second, capacity: 64, SlabBytes, margin);
        var (video, audio) = PacketRingTests.Streams(ring);

        PacketRingTests.Feed(ring, video, audio, seconds: 20, VideoBytes);
        using var cut = ring.Cut(10 * Second, 20 * Second)!;
        var oldestBefore = ring.OldestUs;

        // 4.5 GiB of 32: under the 4.8 GiB margin.
        status.Available = (long)(4.5 * GiB);
        var atDrop = ring.LiveSlabs;
        for (var second = 20; second < 40; second++)
        {
            PacketRingTests.Feed(ring, video, audio, seconds: 1, VideoBytes, fromSecond: second);
            Assert.True(ring.LiveSlabs <= atDrop, $"{ring.LiveSlabs} slabs live, {atDrop} at the drop");
        }

        Assert.All(cut.Packets, packet => Assert.True(packet.ToArray().AsSpan().SequenceEqual(PacketRingTests.Pattern(packet.Stream, packet.Pts, packet.Length)), $"packet {packet.Stream}@{packet.Pts} changed"));
        Assert.True(ring.OldestUs > oldestBefore + (15 * Second), $"the oldest entry is at {ring.OldestUs} µs");

        var newest = 40 * Second;
        using var clip = ring.Cut(newest - (60 * Second), newest)!;
        Assert.InRange(clip.Packets[^1].TimeUs, newest - (Second / 10), newest);
        Assert.True(clip.EndUs - clip.StartUs < 25 * Second, $"the Clip spans {clip.EndUs - clip.StartUs} µs");
        Assert.Equal([MemoryMarginEvent.Entered], events);
    }

    [Fact]
    public void TheMarginIsEnteredAgainOnlyAfterItIsLeftAndAShortRingIsReportedOncePerEntry()
    {
        var status = new FakeMemoryStatus { Total = 32 * GiB, Available = 20 * GiB };
        var margin = new MemoryMargin(status);
        var events = new List<MemoryMarginEvent>();
        margin.Raised += events.Add;
        using var ring = new PacketRing(60 * Second, capacity: 64, SlabBytes, margin);
        var (video, audio) = PacketRingTests.Streams(ring);
        PacketRingTests.Feed(ring, video, audio, seconds: 5, VideoBytes);

        status.Available = (long)(4.5 * GiB);
        PacketRingTests.Feed(ring, video, audio, seconds: 2, VideoBytes, fromSecond: 5);
        Assert.True(margin.Inside);

        // Past the margin and its slack: left.
        status.Available = MemoryMargin.MarginBytes(status.Total) + MemoryMargin.LeaveSlackBytes + (64L * 1024 * 1024);
        PacketRingTests.Feed(ring, video, audio, seconds: 2, VideoBytes, fromSecond: 7);
        Assert.False(margin.Inside);

        status.Available = (long)(4.5 * GiB);
        PacketRingTests.Feed(ring, video, audio, seconds: 1, VideoBytes, fromSecond: 9);
        Assert.Equal([MemoryMarginEvent.Entered, MemoryMarginEvent.Entered], events);

        // A keyframe bigger than every small slab held: its trim leaves the ring under 1 s.
        var keyPts = 10L * 90_000;
        ring.Append(video, PacketRingTests.Pattern(video, keyPts, 40 * SlabBytes), keyPts, keyPts, 3000, isKey: true);
        Assert.True(ring.OldestUs > (10 * Second) - Second, $"the oldest entry is at {ring.OldestUs} µs");
        PacketRingTests.Feed(ring, video, audio, seconds: 3, VideoBytes, fromSecond: 11);

        Assert.Equal([MemoryMarginEvent.Entered, MemoryMarginEvent.Entered, MemoryMarginEvent.ShortRing], events);
    }

    [Fact]
    public void InsideTheMarginAFullyPinnedRingTrimsNothing()
    {
        var status = new FakeMemoryStatus { Total = 32 * GiB, Available = 20 * GiB };
        var margin = new MemoryMargin(status);
        var events = new List<MemoryMarginEvent>();
        margin.Raised += events.Add;
        using var ring = new PacketRing(60 * Second, capacity: 64, SlabBytes, margin);
        var (video, audio) = PacketRingTests.Streams(ring);
        PacketRingTests.Feed(ring, video, audio, seconds: 5, VideoBytes);
        using var cut = ring.Cut(0, 5 * Second)!;

        // Every slab is pinned: the next new slab frees nothing, so nothing is dropped.
        status.Available = (long)(4.5 * GiB);
        var slabs = ring.LiveSlabs;
        for (var frame = 150L; ring.LiveSlabs == slabs; frame++)
        {
            var pts = frame * 3000;
            ring.Append(video, PacketRingTests.Pattern(video, pts, VideoBytes), pts, pts, 3000, isKey: false);
        }

        Assert.Equal(0, ring.OldestUs);
        Assert.Equal([MemoryMarginEvent.Entered], events);
    }

    private sealed class FakeMemoryStatus : IMemoryStatus
    {
        public long Available { get; set; }

        public long Total { get; set; }

        public MemoryStatus Read() => new(Available, Total);
    }
}
