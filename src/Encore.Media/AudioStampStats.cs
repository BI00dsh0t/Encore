namespace Encore.Media;

/// <summary>
/// The health of one device's packet stamps (PLAN.md §7.4): bad-stamp counts, jitter of each
/// stamp against the previous stamp plus its frames, stamp age when the packet was read,
/// and the long-run drift of stamps against the frame count. Allocation-free; the device
/// thread records, and the readout happens after it has stopped.
/// </summary>
public sealed class AudioStampStats
{
    /// <summary>A stamp this far off its prediction starts a new drift segment (a gap, not jitter).</summary>
    public const double SegmentBreakMilliseconds = 5;

    private readonly long _qpcFrequency;
    private readonly long _segmentBreakTicks;

    private bool _hasGood;
    private long _lastGoodStamp;
    private int _lastGoodFrames;
    private bool _previousWasGood;

    private bool _inSegment;
    private long _segmentStartStamp;
    private long _segmentFrames;
    private double _bestSegmentSeconds;
    private double _bestSegmentPpm;

    public AudioStampStats(long qpcFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _qpcFrequency = qpcFrequency;
        _segmentBreakTicks = (long)(SegmentBreakMilliseconds * qpcFrequency / 1000);
        Jitter = new LatencyHistogram(qpcFrequency, bucketMicroseconds: 10, rangeMicroseconds: 100_000);
        StampAge = new LatencyHistogram(qpcFrequency, bucketMicroseconds: 50, rangeMicroseconds: 500_000);
    }

    public long Packets { get; private set; }

    public long Frames { get; private set; }

    public long SilentPackets { get; private set; }

    public long TimestampErrors { get; private set; }

    public long ZeroStamps { get; private set; }

    /// <summary>Stamps not after the previous good stamp.</summary>
    public long BackwardStamps { get; private set; }

    public long Discontinuities { get; private set; }

    /// <summary>Drift segments started (the first one included).</summary>
    public long Segments { get; private set; }

    /// <summary>|stamp − (previous stamp + its frames)| for consecutive good packets.</summary>
    public LatencyHistogram Jitter { get; }

    /// <summary>QPC when the packet was read minus its stamp.</summary>
    public LatencyHistogram StampAge { get; }

    /// <summary>The longest unbroken run of good, predictable stamps, in seconds.</summary>
    public double DriftSegmentSeconds => _bestSegmentSeconds;

    /// <summary>
    /// Over <see cref="DriftSegmentSeconds"/>: (stamp time − frame time) / frame time × 10⁶.
    /// Positive means the device delivers fewer frames per QPC second than 48 kHz.
    /// </summary>
    public double DriftPpm => _bestSegmentPpm;

    /// <summary>Records one packet as <c>GetBuffer</c> returned it, read at <paramref name="readQpc"/>.</summary>
    public void Record(long stampQpc, int frames, AudioPacketFlags flags, long readQpc)
    {
        Packets++;
        Frames += frames;
        if ((flags & AudioPacketFlags.Silent) != 0)
        {
            SilentPackets++;
        }

        var discontinuity = (flags & AudioPacketFlags.DataDiscontinuity) != 0;
        if (discontinuity)
        {
            Discontinuities++;
        }

        if ((flags & AudioPacketFlags.TimestampError) != 0)
        {
            TimestampErrors++;
            Break();
            return;
        }

        if (stampQpc == 0)
        {
            ZeroStamps++;
            Break();
            return;
        }

        if (_hasGood && stampQpc <= _lastGoodStamp)
        {
            BackwardStamps++;
            Break();
            return;
        }

        StampAge.Record(readQpc - stampQpc);

        var predictable = false;
        if (_previousWasGood && !discontinuity)
        {
            var expected = _lastGoodStamp + QpcClock.ToUnits(_lastGoodFrames, AudioTimeline.SampleRate, _qpcFrequency);
            var error = Math.Abs(stampQpc - expected);
            Jitter.Record(error);
            predictable = error <= _segmentBreakTicks;
        }

        if (_inSegment && predictable)
        {
            _segmentFrames += _lastGoodFrames;
            var stampSeconds = (double)(stampQpc - _segmentStartStamp) / _qpcFrequency;
            if (stampSeconds > _bestSegmentSeconds)
            {
                var frameSeconds = (double)_segmentFrames / AudioTimeline.SampleRate;
                _bestSegmentSeconds = stampSeconds;
                _bestSegmentPpm = (stampSeconds - frameSeconds) / frameSeconds * 1e6;
            }
        }
        else
        {
            _inSegment = true;
            _segmentStartStamp = stampQpc;
            _segmentFrames = 0;
            Segments++;
        }

        _hasGood = true;
        _previousWasGood = true;
        _lastGoodStamp = stampQpc;
        _lastGoodFrames = frames;
    }

    private void Break()
    {
        _inSegment = false;
        _previousWasGood = false;
    }
}
