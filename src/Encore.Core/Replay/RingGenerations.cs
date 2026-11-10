namespace Encore.Core.Replay;

/// <summary>Rewind as numbered generations, each a ring with its own streams; packets go into the newest. docs/code-notes.md.</summary>
public sealed partial class RingGenerations : IDisposable
{
    /// <summary>The shortest Clip, and how far past a boundary a cut must end to come from the new generation.</summary>
    public const long MinClipUs = 1_000_000;

    /// <summary>How long a restart's predecessor is kept after the boundary, so a press just after it can still cut it.</summary>
    public const long RetireSlackUs = 5_000_000;

    private readonly Lock _lock = new();
    private readonly List<Generation> _generations = [];
    private Generation _current;

    // A restart's predecessor, kept for the previous-generation fallback until the retire check.
    private Generation? _predecessor;
    private bool _disposed;

    /// <param name="first">Generation 1's ring; this owns it.</param>
    public RingGenerations(PacketRing first)
    {
        ArgumentNullException.ThrowIfNull(first);
        _current = new Generation(1, first, long.MinValue);
        _generations.Add(_current);
    }

    /// <summary>The current generation's number, counting from 1.</summary>
    public int Current
    {
        get
        {
            lock (_lock)
            {
                return _current.Number;
            }
        }
    }

    /// <summary>The current generation's ring.</summary>
    public PacketRing Ring
    {
        get
        {
            lock (_lock)
            {
                return _current.Ring;
            }
        }
    }

    /// <summary>Registers a stream on the current generation's ring; returns its index there.</summary>
    public int AddStream(int timeBaseNum, int timeBaseDen, bool isVideo)
    {
        lock (_lock)
        {
            return _current.Ring.AddStream(timeBaseNum, timeBaseDen, isVideo);
        }
    }

    /// <summary>Appends one packet to the current generation, then retires a restart's predecessor once it is past need.</summary>
    public void Append(int stream, ReadOnlySpan<byte> data, long pts, long dts, long duration, bool isKey)
    {
        lock (_lock)
        {
            var current = _current;
            current.Ring.Append(stream, data, pts, dts, duration, isKey);
            if (current.StartUs == long.MaxValue && isKey)
            {
                current.StartUs = current.Ring.FirstKeyUs;
            }

            if (_predecessor is { } predecessor
                && current.StartUs != long.MaxValue
                && current.Ring.ToMicros(stream, pts) >= current.StartUs + MinClipUs + RetireSlackUs)
            {
                Retire(predecessor);
            }
        }
    }

    /// <summary>Whether every stream of the current generation has a packet ending at or after <paramref name="us"/>.</summary>
    public bool AllStreamsPast(long us)
    {
        lock (_lock)
        {
            return _current.Ring.AllStreamsPast(us);
        }
    }

    /// <summary><paramref name="ticks"/> in the current generation's stream <paramref name="stream"/> as µs since the anchor.</summary>
    public long ToMicros(int stream, long ticks)
    {
        lock (_lock)
        {
            return _current.Ring.ToMicros(stream, ticks);
        }
    }

    /// <summary>Opens an empty generation for an encoder restart; no save may be waiting. Returns its number.</summary>
    public int Restart(PacketRing next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _current;
            if (_predecessor is { } kept && previous.StartUs == long.MaxValue)
            {
                Retire(previous);
                Open(new Generation(previous.Number + 1, next, long.MaxValue));
                _predecessor = kept;
                return _current.Number;
            }

            Open(new Generation(previous.Number + 1, next, long.MaxValue));
            _predecessor = previous;
            return _current.Number;
        }
    }

    /// <summary>Opens a generation for a Buffer Window resize, seeded from the keyframe at fromUs; the old one is retired.</summary>
    public int Resize(PacketRing next, long fromUs)
    {
        ArgumentNullException.ThrowIfNull(next);
        PacketRing ring;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ring = _current.Ring;
        }

        var sequence = ring.CopyTailTo(next, fromUs);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _current;
            if (!ReferenceEquals(previous.Ring, ring))
            {
                throw new InvalidOperationException("The ring generation changed during a resize.");
            }

            ring.CopyFrom(next, sequence);
            Open(new Generation(previous.Number + 1, next, next.FirstKeyUs));
            Retire(previous);
            return _current.Number;
        }
    }

    /// <summary>Pins a time range from one generation: the newest that starts before its end. Null when there is nothing.</summary>
    public (RingCut Cut, int Generation)? Cut(long fromUs, long toUs)
    {
        CutRange range;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (RangeFor(fromUs, toUs) is not { } found)
            {
                return null;
            }

            range = found;
            range.Generation.Users++;
        }

        var chosen = range.Generation;
        RingCut? cut = null;
        try
        {
            cut = chosen.Ring.Cut(range.FromUs, range.ToUs);
            if (range.Fallback && cut is not null && cut.EndUs - cut.StartUs < MinClipUs)
            {
                cut.Dispose();
                cut = null;
            }
        }
        finally
        {
            ReleaseUser(chosen);
        }

        return cut is null ? null : (cut, chosen.Number);
    }

    /// <summary>Disposes every ring; one a cut is still reading goes when that cut has pinned its slabs.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = _generations.Count - 1; i >= 0; i--)
            {
                Retire(_generations[i]);
            }
        }
    }

    /// <summary>The generation a time range is cut from, and the range cut there; null when none starts before its end. Under the lock.</summary>
    private CutRange? RangeFor(long fromUs, long toUs)
    {
        for (var i = _generations.Count - 1; i >= 0; i--)
        {
            if (_generations[i].StartUs < toUs)
            {
                return RangeIn(_generations[i], i > 0 ? _generations[i - 1] : null, fromUs, toUs);
            }
        }

        return null;
    }

    // A range that ends too soon after a generation's start is cut from the one before, up to that start.
    private static CutRange RangeIn(Generation current, Generation? previous, long fromUs, long toUs) =>
        previous is not null && toUs - current.StartUs < MinClipUs
            ? new CutRange(previous, fromUs, current.StartUs, Fallback: true)
            : new CutRange(current, Math.Max(fromUs, current.StartUs), toUs, Fallback: false);

    /// <summary>A cut is done reading a generation's ring; a retired one's ring is disposed with its last reader.</summary>
    private void ReleaseUser(Generation generation)
    {
        lock (_lock)
        {
            generation.Users--;
            if (generation.Retired && generation.Users == 0)
            {
                generation.Ring.Dispose();
            }
        }
    }

    /// <summary>Makes <paramref name="next"/> current and retires every generation before its predecessor. Under the lock.</summary>
    private void Open(Generation next)
    {
        while (_generations.Count > 1)
        {
            Retire(_generations[0]);
        }

        _predecessor = null;
        _generations.Add(next);
        _current = next;
    }

    /// <summary>Drops a generation: it is never cut again, and its ring is disposed once no cut reads it. Under the lock.</summary>
    private void Retire(Generation generation)
    {
        if (generation.Retired)
        {
            return;
        }

        generation.Retired = true;
        _generations.Remove(generation);
        if (ReferenceEquals(generation, _predecessor))
        {
            _predecessor = null;
        }

        if (generation.Users == 0)
        {
            generation.Ring.Dispose();
        }
    }
}
