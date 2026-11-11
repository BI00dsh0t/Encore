namespace Encore.Core.Tests;

/// <summary>A thread-safe clock that moves only when told; Advance fires the timers that come due, outside its lock.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset until;
        lock (_lock)
        {
            until = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.DueAt is { } due && due <= until).MinBy(t => t.DueAt);
                if (next is null)
                {
                    _now = until;
                    return;
                }

                _now = next.DueAt!.Value;
                next.DueAt = next.Period is { } period ? _now + period : null;
            }

            next.Fire();
        }
    }

    /// <summary>Waits up to 2 s until a timer is armed to fire <paramref name="dueIn"/> from now.</summary>
    public void WaitUntilArmed(TimeSpan dueIn)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            lock (_lock)
            {
                if (_timers.Any(t => t.DueAt == _now + dueIn))
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < until, $"No timer was armed for {dueIn} within 2 s.");
            Thread.Sleep(10);
        }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        // Guarded by the owner's lock.
        public DateTimeOffset? DueAt { get; set; }

        public TimeSpan? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? null : period;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
