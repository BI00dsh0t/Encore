namespace Encore.Media;

/// <summary>The health of one device's packet stamps: bad stamps, jitter, stamp age and drift. Read after the thread stops.</summary>
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

    /// <summary>Drift over the best segment, in parts per million; positive means the device delivers fewer frames than 48 kHz.</summary>
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

        if (IsBadStamp(stampQpc, flags))
        {
            Break();
            return;
        }

        StampAge.Record(readQpc - stampQpc);
        TrackSegment(stampQpc, IsPredictable(stampQpc, discontinuity));
        _hasGood = true;
        _previousWasGood = true;
        _lastGoodStamp = stampQpc;
        _lastGoodFrames = frames;
    }

    /// <summary>Counts a flagged, zero or backward stamp; such a stamp breaks the drift segment.</summary>
    private bool IsBadStamp(long stampQpc, AudioPacketFlags flags)
    {
        if ((flags & AudioPacketFlags.TimestampError) != 0)
        {
            TimestampErrors++;
            return true;
        }

        if (stampQpc == 0)
        {
            ZeroStamps++;
            return true;
        }

        if (_hasGood && stampQpc <= _lastGoodStamp)
        {
            BackwardStamps++;
            return true;
        }

        return false;
    }

    /// <summary>Whether the stamp follows the last good one by its frames, within the break; records the jitter when it can tell.</summary>
    private bool IsPredictable(long stampQpc, bool discontinuity)
    {
        if (!_previousWasGood || discontinuity)
        {
            return false;
        }

        var expected = _lastGoodStamp + QpcClock.ToUnits(_lastGoodFrames, AudioTimeline.SampleRate, _qpcFrequency);
        var error = Math.Abs(stampQpc - expected);
        Jitter.Record(error);
        return error <= _segmentBreakTicks;
    }

    /// <summary>Extends the drift segment with a predictable stamp, keeping the longest; any other stamp starts a new one.</summary>
    private void TrackSegment(long stampQpc, bool predictable)
    {
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
    }

    private void Break()
    {
        _inSegment = false;
        _previousWasGood = false;
    }
}
