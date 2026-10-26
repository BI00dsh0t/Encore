namespace ReplayBuffer.Media;

/// <summary>
/// A fixed-size histogram of QPC intervals: equal-width buckets up to a range, plus one
/// overflow bucket. Recording never allocates. Not thread-safe: one thread records, and
/// the readout happens once that thread has stopped.
/// </summary>
public sealed class LatencyHistogram
{
    /// <summary>The capture→encoded default: 0.1 ms buckets to 250 ms.</summary>
    public const long DefaultBucketMicroseconds = 100;

    public const long DefaultRangeMicroseconds = 250_000;

    private readonly long _qpcFrequency;
    private readonly long _bucketMicroseconds;
    private readonly long[] _buckets;
    private long _overflow;
    private long _negative;
    private long _count;
    private long _maxTicks;
    private Int128 _sumTicks;

    public LatencyHistogram(long qpcFrequency, long bucketMicroseconds = DefaultBucketMicroseconds, long rangeMicroseconds = DefaultRangeMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketMicroseconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(rangeMicroseconds, bucketMicroseconds);
        _qpcFrequency = qpcFrequency;
        _bucketMicroseconds = bucketMicroseconds;
        _buckets = new long[(int)(rangeMicroseconds / bucketMicroseconds)];
    }

    /// <summary>Values recorded, overflow and negatives included.</summary>
    public long Count => _count;

    /// <summary>Values at or past the range.</summary>
    public long Overflow => _overflow;

    /// <summary>Negative values, recorded as zero.</summary>
    public long Negative => _negative;

    public double BucketMilliseconds => _bucketMicroseconds / 1000.0;

    /// <summary>The largest value recorded, exactly, in milliseconds.</summary>
    public double MaxMilliseconds => ToMilliseconds(_maxTicks);

    public double MeanMilliseconds => _count == 0 ? 0 : ToMilliseconds((long)(_sumTicks / _count));

    /// <summary>Records one interval in QPC ticks.</summary>
    public void Record(long ticks)
    {
        _count++;
        if (ticks < 0)
        {
            _negative++;
            ticks = 0;
        }

        _sumTicks += ticks;
        if (ticks > _maxTicks)
        {
            _maxTicks = ticks;
        }

        var microseconds = (Int128)ticks * 1_000_000 / _qpcFrequency;
        var bucket = microseconds / _bucketMicroseconds;
        if (bucket >= _buckets.Length)
        {
            _overflow++;
        }
        else
        {
            _buckets[(int)bucket]++;
        }
    }

    /// <summary>
    /// The upper edge, in milliseconds, of the bucket holding the <paramref name="percentile"/>
    /// (0–100) value; the exact maximum when that value is in the overflow; 0 when empty.
    /// </summary>
    public double Percentile(double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);
        if (_count == 0)
        {
            return 0;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(percentile / 100 * _count));
        long seen = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            seen += _buckets[i];
            if (seen >= rank)
            {
                return Math.Min(Math.Round((i + 1) * BucketMilliseconds, 6), MaxMilliseconds);
            }
        }

        return MaxMilliseconds;
    }

    private double ToMilliseconds(long ticks) => ticks * 1000.0 / _qpcFrequency;
}
