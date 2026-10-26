namespace ReplayBuffer.Media.Tests;

public sealed class LatencyHistogramTests
{
    private const long Qpf = 10_000_000; // 1 tick = 0.1 µs

    private static long Ms(double ms) => (long)(ms * Qpf / 1000);

    [Fact]
    public void AnEmptyHistogramReadsZero()
    {
        var h = new LatencyHistogram(Qpf);

        Assert.Equal(0, h.Count);
        Assert.Equal(0, h.Percentile(99));
        Assert.Equal(0, h.MaxMilliseconds);
    }

    [Fact]
    public void PercentilesAreBucketUpperEdgesAndTheMaxIsExact()
    {
        var h = new LatencyHistogram(Qpf);
        for (var i = 1; i <= 100; i++)
        {
            h.Record(Ms(i)); // 1 ms … 100 ms
        }

        Assert.Equal(100, h.Count);
        Assert.Equal(50.1, h.Percentile(50), 6);
        Assert.Equal(99.1, h.Percentile(99), 6);
        Assert.Equal(100, h.MaxMilliseconds, 6);
        Assert.Equal(50.5, h.MeanMilliseconds, 6);
    }

    [Fact]
    public void ValuesPastTheRangeGoToOverflowAndPercentilesThereReadTheMax()
    {
        var h = new LatencyHistogram(Qpf);
        h.Record(Ms(10));
        h.Record(Ms(300));

        Assert.Equal(1, h.Overflow);
        Assert.Equal(300, h.Percentile(99), 6);
        Assert.Equal(10.1, h.Percentile(50), 6);
    }

    [Fact]
    public void NegativeValuesAreCountedAndRecordedAsZero()
    {
        var h = new LatencyHistogram(Qpf);
        h.Record(-5);

        Assert.Equal(1, h.Negative);
        Assert.Equal(0, h.Percentile(50));
    }

    [Fact]
    public void RecordingDoesNotAllocate()
    {
        var h = new LatencyHistogram(Qpf);
        h.Record(Ms(1));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            h.Record(Ms(i % 400));
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

public sealed class FrameCadenceTests
{
    private const long Qpf = 10_000_000;

    private static long Slot(double slots) => (long)(slots * Qpf / 60);

    [Fact]
    public void ASteadySixtyFpsMissesNothing()
    {
        var c = new FrameCadence(Qpf);
        for (var i = 0; i < 600; i++)
        {
            c.Record(1_000 + Slot(i));
        }

        Assert.Equal(600, c.Frames);
        Assert.Equal(0, c.MissedSlots);
        Assert.Equal(0, c.Gaps);
        Assert.Equal(599.0 / 60, c.SpanSeconds, 3);
    }

    [Theory]
    [InlineData(1.4, 0)] // jitter, not a miss
    [InlineData(1.6, 1)] // round(1.6) − 1
    [InlineData(2.0, 1)]
    [InlineData(2.4, 1)]
    [InlineData(2.6, 2)]
    [InlineData(10.0, 9)]
    public void AStepPastOneAndAHalfSlotsCountsRoundedMissedSlots(double step, long missed)
    {
        var c = new FrameCadence(Qpf);
        c.Record(0);
        c.Record(Slot(step));

        Assert.Equal(missed, c.MissedSlots);
        Assert.Equal(missed > 0 ? 1 : 0, c.Gaps);
        Assert.Equal(step * 1000 / 60, c.MaxStepMilliseconds, 3);
    }

    [Fact]
    public void ARepeatedStampIsCountedNotTreatedAsAStep()
    {
        var c = new FrameCadence(Qpf);
        c.Record(Slot(0));
        c.Record(Slot(1));
        c.Record(Slot(1));
        c.Record(Slot(2));

        Assert.Equal(4, c.Frames);
        Assert.Equal(1, c.NonIncreasing);
        Assert.Equal(0, c.MissedSlots);
    }
}

public sealed class AudioStampStatsTests
{
    private const long Qpf = 10_000_000;
    private const int Packet = 480; // 10 ms
    private const long Start = 1_000_000_000;

    private static long Frames(long frames) => frames * Qpf / AudioTimeline.SampleRate;

    private static long Ms(double ms) => (long)(ms * Qpf / 1000);

    /// <summary>Records <paramref name="count"/> contiguous packets from frame <paramref name="first"/>, read 2 ms after their stamp.</summary>
    private static long Run(AudioStampStats s, long first, int count, double ppm = 0)
    {
        for (var i = 0; i < count; i++)
        {
            var frame = first + ((long)i * Packet);
            var stamp = Start + (long)(Frames(frame) * (1 + (ppm / 1e6)));
            s.Record(stamp, Packet, AudioPacketFlags.None, stamp + Ms(2));
        }

        return first + ((long)count * Packet);
    }

    [Fact]
    public void ContiguousStampsHaveNoJitterAndTheirAgeIsMeasured()
    {
        var s = new AudioStampStats(Qpf);
        Run(s, 0, 1000);

        Assert.Equal(1000, s.Packets);
        Assert.Equal(1000 * Packet, s.Frames);
        Assert.Equal(999, s.Jitter.Count);
        Assert.True(s.Jitter.MaxMilliseconds < 0.01);
        Assert.Equal(2, s.StampAge.MaxMilliseconds, 3);
        Assert.Equal(1, s.Segments);
        Assert.Equal(9.99, s.DriftSegmentSeconds, 3);
        Assert.InRange(s.DriftPpm, -1, 1);
    }

    [Fact]
    public void LongRunDriftIsReportedInPpm()
    {
        var s = new AudioStampStats(Qpf);
        Run(s, 0, 6000, ppm: 100); // 60 s, stamps run 100 ppm fast against 48 kHz

        Assert.InRange(s.DriftPpm, 99, 101);
        Assert.InRange(s.DriftSegmentSeconds, 59.9, 60.1);
    }

    [Fact]
    public void BadStampsAreCountedByKind()
    {
        var s = new AudioStampStats(Qpf);
        var next = Run(s, 0, 10);
        s.Record(0, Packet, AudioPacketFlags.None, Start);
        s.Record(Start + Frames(next), Packet, AudioPacketFlags.TimestampError, Start);
        s.Record(Start, Packet, AudioPacketFlags.None, Start); // not after the last good stamp
        s.Record(Start + Frames(next + 10_000), Packet, AudioPacketFlags.DataDiscontinuity | AudioPacketFlags.Silent, Start + Frames(next + 10_000));

        Assert.Equal(1, s.ZeroStamps);
        Assert.Equal(1, s.TimestampErrors);
        Assert.Equal(1, s.BackwardStamps);
        Assert.Equal(1, s.Discontinuities);
        Assert.Equal(1, s.SilentPackets);
        Assert.Equal(14, s.Packets);

        // Only consecutive good packets measure jitter: 9 in the first run, none across the bad ones.
        Assert.Equal(9, s.Jitter.Count);
        Assert.Equal(2, s.Segments);
    }

    [Fact]
    public void JitterIsTheDistanceFromThePrediction()
    {
        var s = new AudioStampStats(Qpf);
        s.Record(Start, Packet, AudioPacketFlags.None, Start);
        s.Record(Start + Frames(Packet) + Ms(0.5), Packet, AudioPacketFlags.None, Start);
        s.Record(Start + Frames(2 * Packet), Packet, AudioPacketFlags.None, Start);

        Assert.Equal(2, s.Jitter.Count);
        Assert.Equal(0.5, s.Jitter.MaxMilliseconds, 3);
    }

    [Fact]
    public void AGapStartsANewDriftSegmentAndTheLongestIsKept()
    {
        var s = new AudioStampStats(Qpf);
        var next = Run(s, 0, 500);           // 5 s
        Run(s, next + 48_000, 1000);         // 1 s of silence (no packets), then 10 s

        Assert.Equal(2, s.Segments);
        Assert.InRange(s.DriftSegmentSeconds, 9.98, 10);
        Assert.Equal(1000.0, s.Jitter.MaxMilliseconds, 0);
    }

    [Fact]
    public void RecordingDoesNotAllocate()
    {
        var s = new AudioStampStats(Qpf);
        Run(s, 0, 2);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run(s, 960, 5000);

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

public sealed class AudioTimelineCorrectionTests
{
    private const long Qpf = 10_000_000;
    private const long Anchor = 5_000_000_000;

    private readonly AudioTimeline _timeline;

    public AudioTimelineCorrectionTests() => _timeline = new AudioTimeline(Qpf, Anchor, new RecordingSink());

    private static long At(long frames) => Anchor + (frames * Qpf / AudioTimeline.SampleRate);

    private void Push(long frame, int frames, AudioPacketFlags flags = AudioPacketFlags.Silent) =>
        _timeline.Push(At(frame), frames, ReadOnlySpan<float>.Empty, flags);

    [Fact]
    public void GapsPastTheToleranceAreCountedWithTheirFill()
    {
        Push(0, 480);
        Push(480 + 960, 480); // 20 ms late

        Assert.Equal(1, _timeline.GapFills);
        Assert.Equal(960, _timeline.GapFillFrames);
        Assert.Equal(0, _timeline.Trims);
    }

    [Fact]
    public void DriftInsideTheToleranceIsNotACorrection()
    {
        Push(0, 480);
        Push(480 + 100, 480);
        Push(960 - 100, 480);

        Assert.Equal(0, _timeline.GapFills);
        Assert.Equal(0, _timeline.Trims);
    }

    [Fact]
    public void OverlapsPastTheToleranceAreCountedWithTheirTrim()
    {
        Push(0, 960);
        Push(960 - 480, 960); // 10 ms early

        Assert.Equal(1, _timeline.Trims);
        Assert.Equal(480, _timeline.TrimmedFrames);
    }

    [Fact]
    public void ExactPlacementAtTheStartAndOnIdleResumeIsNotACorrection()
    {
        Push(100, 480);              // first packet, placed exactly 100 frames after the anchor
        _timeline.FillTo(At(4800));  // idle
        Push(4800 - 100, 480);       // resume, placed exactly over 100 frames of fill
        Push(5180 + 200, 480);       // 200 frames late: inside the tolerance

        Assert.Equal(0, _timeline.GapFills);
        Assert.Equal(0, _timeline.GapFillFrames);
        Assert.Equal(0, _timeline.Trims);
        Assert.Equal(0, _timeline.TrimmedFrames);
    }

    [Fact]
    public void IdleFillAndResolvedStampsAreCounted()
    {
        Push(0, 480);
        _timeline.FillTo(At(4800));
        _timeline.Push(0, 480, ReadOnlySpan<float>.Empty, AudioPacketFlags.Silent);

        Assert.Equal(4320, _timeline.IdleFillFrames);
        Assert.Equal(1, _timeline.ResolvedStamps);
    }
}
