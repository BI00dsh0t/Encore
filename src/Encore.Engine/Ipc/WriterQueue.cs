namespace Encore.Engine.Ipc;

/// <summary>One connection's outgoing frames, bounded; enqueuing never blocks, and a full queue drops the connection.</summary>
internal sealed class WriterQueue(int maxQueued, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<Item> _items = new();
    private readonly Dictionary<string, LinkedListNode<Item>> _coalescing = new(StringComparer.Ordinal);
    private TaskCompletionSource? _waiter;
    private long? _inFlightSince;
    private bool _closed;
    private bool _completed;

    public int MaxQueued { get; } = maxQueued;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>The age of the oldest frame not yet fully written; zero when there is none.</summary>
    public TimeSpan Lag
    {
        get
        {
            lock (_lock)
            {
                var oldest = _inFlightSince ?? _items.First?.Value.EnqueuedAt;
                return oldest is { } since ? time.GetElapsedTime(since) : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Queues a frame; false when the queue is closed or full, and a full queue is then closed.</summary>
    public bool TryEnqueue(byte[] frame, string? coalesceKey = null)
    {
        var accepted = false;
        WakeAfter(() => accepted = EnqueueLocked(frame, coalesceKey));
        return accepted;
    }

    /// <summary>The next frame to write, waiting for one; null once closed. Call <see cref="Written"/> after writing it.</summary>
    public async ValueTask<byte[]?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_closed)
                {
                    return null;
                }

                if (TakeFirstLocked() is { } frame)
                {
                    return frame;
                }

                if (_completed)
                {
                    return null;
                }

                _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The frame from the last <see cref="DequeueAsync"/> is fully written.</summary>
    public void Written()
    {
        lock (_lock)
        {
            _inFlightSince = null;
        }
    }

    /// <summary>Refuses new frames; the queued ones are still handed out, then <see cref="DequeueAsync"/> returns <c>null</c>.</summary>
    public void Complete() => WakeAfter(() => _completed = true);

    /// <summary>Discards every frame and refuses new ones.</summary>
    public void Close() => WakeAfter(CloseLocked);

    /// <summary>Takes the oldest frame and marks it in flight; null when none is queued.</summary>
    private byte[]? TakeFirstLocked()
    {
        if (_items.First is not { } first)
        {
            return null;
        }

        _items.RemoveFirst();
        if (first.Value.CoalesceKey is { } key)
        {
            _coalescing.Remove(key);
        }

        _inFlightSince = first.Value.EnqueuedAt;
        return first.Value.Frame;
    }

    private bool EnqueueLocked(byte[] frame, string? coalesceKey)
    {
        if (_closed || _completed)
        {
            return false;
        }

        if (coalesceKey is not null && _coalescing.Remove(coalesceKey, out var stale))
        {
            _items.Remove(stale);
        }

        if (_items.Count >= MaxQueued)
        {
            CloseLocked();
            return false;
        }

        var node = _items.AddLast(new Item(frame, coalesceKey, time.GetTimestamp()));
        if (coalesceKey is not null)
        {
            _coalescing[coalesceKey] = node;
        }

        return true;
    }

    /// <summary>Makes a change under the lock, then wakes a waiting writer outside it.</summary>
    private void WakeAfter(Action changeLocked)
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            changeLocked();
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }

    private void CloseLocked()
    {
        _closed = true;
        _items.Clear();
        _coalescing.Clear();
        _inFlightSince = null;
    }

    private readonly record struct Item(byte[] Frame, string? CoalesceKey, long EnqueuedAt);
}
