namespace ReplayBuffer.Media;

/// <summary>
/// A fixed-capacity, allocation-free queue from one producer thread to one consumer thread.
/// The producer never blocks (<see cref="TryAdd"/> fails when full); the consumer blocks in
/// <see cref="TryTake"/> until an item arrives or the queue is completed and empty.
/// </summary>
public sealed class BoundedHandoff<T>
    where T : struct
{
    private readonly T[] _items;
    private readonly object _sync = new();
    private int _head;
    private int _count;
    private bool _completed;

    public BoundedHandoff(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>True when an add would fail. With one producer, a false result stays true until it adds.</summary>
    public bool IsFull => Count == _items.Length;

    /// <summary>Adds an item; false when the queue is full or completed.</summary>
    public bool TryAdd(T item)
    {
        lock (_sync)
        {
            if (_completed || _count == _items.Length)
            {
                return false;
            }

            _items[(_head + _count) % _items.Length] = item;
            _count++;
            Monitor.Pulse(_sync);
            return true;
        }
    }

    /// <summary>Takes the oldest item, waiting for one; false once completed and empty.</summary>
    public bool TryTake(out T item)
    {
        lock (_sync)
        {
            while (_count == 0)
            {
                if (_completed)
                {
                    item = default;
                    return false;
                }

                Monitor.Wait(_sync);
            }

            item = _items[_head];
            _items[_head] = default;
            _head = (_head + 1) % _items.Length;
            _count--;
            return true;
        }
    }

    /// <summary>
    /// Takes the oldest item without waiting: false when the queue is empty, and then
    /// <paramref name="drained"/> tells whether it is also completed (nothing will ever arrive).
    /// </summary>
    public bool TryTakeNow(out T item, out bool drained)
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                item = default;
                drained = _completed;
                return false;
            }

            item = _items[_head];
            _items[_head] = default;
            _head = (_head + 1) % _items.Length;
            _count--;
            drained = false;
            return true;
        }
    }

    /// <summary>No more adds; the consumer takes what is left, then <see cref="TryTake"/> returns false.</summary>
    public void Complete()
    {
        lock (_sync)
        {
            _completed = true;
            Monitor.PulseAll(_sync);
        }
    }
}
