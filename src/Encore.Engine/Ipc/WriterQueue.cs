namespace Encore.Engine.Ipc;

/// <summary>
/// One connection's outgoing frames (AD-3), bounded. A frame with a coalescing key replaces the
/// queued frame with the same key, moving to the tail so frames stay in <c>seq</c> order. Enqueuing
/// never blocks: past <see cref="MaxQueued"/> it fails and closes the queue, and the connection is
/// dropped. <see cref="Lag"/> is the age of the oldest frame not yet written, the in-flight one included.
/// </summary>
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

    /// <summary>Queues a frame. <c>false</c> when the queue is closed or would exceed <see cref="MaxQueued"/>; the latter closes it.</summary>
    public bool TryEnqueue(byte[] frame, string? coalesceKey = null)
    {
        TaskCompletionSource? waiter;
        bool accepted;
        lock (_lock)
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
                accepted = false;
            }
            else
            {
                var node = _items.AddLast(new Item(frame, coalesceKey, time.GetTimestamp()));
                if (coalesceKey is not null)
                {
                    _coalescing[coalesceKey] = node;
                }

                accepted = true;
            }

            waiter = TakeWaiter();
        }

        waiter?.TrySetResult();
        return accepted;
    }

    /// <summary>
    /// The next frame to write, waiting for one; <c>null</c> once the queue is closed, or completed
    /// and empty. Call <see cref="Written"/> after writing it.
    /// </summary>
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

                if (_items.First is { } first)
                {
                    _items.RemoveFirst();
                    if (first.Value.CoalesceKey is { } key)
                    {
                        _coalescing.Remove(key);
                    }

                    _inFlightSince = first.Value.EnqueuedAt;
                    return first.Value.Frame;
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
    public void Complete()
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            _completed = true;
            waiter = TakeWaiter();
        }

        waiter?.TrySetResult();
    }

    /// <summary>Discards every frame and refuses new ones.</summary>
    public void Close()
    {
        TaskCompletionSource? waiter;
        lock (_lock)
        {
            CloseLocked();
            waiter = TakeWaiter();
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

    private TaskCompletionSource? TakeWaiter()
    {
        var waiter = _waiter;
        _waiter = null;
        return waiter;
    }

    private readonly record struct Item(byte[] Frame, string? CoalesceKey, long EnqueuedAt);
}
