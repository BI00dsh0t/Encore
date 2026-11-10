namespace Encore.Media;

/// <summary>
/// Counts missed frame slots from consecutive capture stamps: a step longer than 1.5 slots
/// counts as <c>round(Δ × fps) − 1</c> missed slots. Allocation-free; one thread records.
/// </summary>
public sealed class FrameCadence
{
    private readonly long _qpcFrequency;
    private readonly int _fps;
    private long _first;
    private long _last;
    private long _maxStepTicks;

    public FrameCadence(long qpcFrequency, int fps = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        _qpcFrequency = qpcFrequency;
        _fps = fps;
    }

    /// <summary>Stamps recorded.</summary>
    public long Frames { get; private set; }

    /// <summary>Slots that had no frame.</summary>
    public long MissedSlots { get; private set; }

    /// <summary>Steps that missed at least one slot.</summary>
    public long Gaps { get; private set; }

    /// <summary>Steps whose stamp was not after the previous one.</summary>
    public long NonIncreasing { get; private set; }

    public double MaxStepMilliseconds => _maxStepTicks * 1000.0 / _qpcFrequency;

    /// <summary>Seconds from the first stamp to the last.</summary>
    public double SpanSeconds => Frames < 2 ? 0 : (double)(_last - _first) / _qpcFrequency;

    public void Record(long qpc)
    {
        if (Frames++ == 0)
        {
            _first = qpc;
            _last = qpc;
            return;
        }

        var step = qpc - _last;
        if (step <= 0)
        {
            NonIncreasing++;
            return;
        }

        _last = qpc;
        if (step > _maxStepTicks)
        {
            _maxStepTicks = step;
        }

        // step > 1.5 / fps s, kept in integers: 2 × step × fps > 3 × QPF.
        if ((Int128)step * _fps * 2 > (Int128)_qpcFrequency * 3)
        {
            var slots = (long)(((Int128)step * _fps * 2 + _qpcFrequency) / (_qpcFrequency * 2)); // round(Δ × fps)
            MissedSlots += slots - 1;
            Gaps++;
        }
    }
}
