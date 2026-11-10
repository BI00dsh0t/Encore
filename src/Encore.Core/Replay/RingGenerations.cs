namespace Encore.Core.Replay;

/// <summary>
/// Rewind as numbered generations (R5, AD-8), oldest first, each a <see cref="PacketRing"/>
/// with its own streams; packets go into the newest, the current one.
/// <list type="bullet">
/// <item>A <see cref="Restart"/> (a forced encoder restart) opens an empty generation whose first
/// video keyframe is its start, a boundary that counts as a Reset. A Clip holds one generation: a
/// cut ending less than <see cref="MinClipUs"/> past a boundary comes from the previous generation,
/// ending at the boundary, if that holds at least <see cref="MinClipUs"/>.</item>
/// <item>A <see cref="Resize"/> (a Buffer Window change at a Reset) opens a generation with the same
/// streams, seeded with the old ring's packets from the Reset keyframe on, and retires the old one
/// at once, so the timeline stays continuous.</item>
/// </list>
/// A retired ring is disposed once no cut is reading it; slabs a cut pins outlive it. Routing a
/// packet is a field read under one short lock, so <see cref="Append"/> allocates nothing managed;
/// a cut's filter and sort run outside that lock.
/// </summary>
public sealed class RingGenerations : IDisposable
{
    /// <summary>The shortest Clip (R4), and how far past a boundary a cut must end to come from the new generation.</summary>
    public const long MinClipUs = 1_000_000;

    /// <summary>
    /// How long a restart's predecessor is kept past <see cref="MinClipUs"/> after the boundary:
    /// twice a waiting writer's save wait, so a press just after the boundary can still cut it.
    /// </summary>
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

    /// <summary>
    /// Opens an empty generation on <paramref name="next"/> (an encoder restart); its streams are
    /// added next, and its first video keyframe is its start. The current one becomes its
    /// predecessor, and any older is retired. Returns the new generation's number.
    /// Precondition: no save is waiting for its keyframe or its streams (the caller drains or waits
    /// for in-flight saves first), since <see cref="ToMicros"/> and <see cref="AllStreamsPast"/>
    /// read the new generation from here on, which has no streams until the new chain adds them.
    /// A restart while the current generation is itself a restart's with no keyframe yet (a
    /// failed recovery attempt after a GPU reset, say) replaces that empty generation instead:
    /// it is retired, and its predecessor stays the new one's, so the content before the first
    /// restart stays cuttable.
    /// </summary>
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

    /// <summary>
    /// Opens a generation on <paramref name="next"/> (a Buffer Window resize at a Reset), seeded
    /// with the current ring's packets from the keyframe at or after <paramref name="fromUs"/> on,
    /// and retires the old one at once. Returns the new generation's number. Most of the tail is
    /// copied in batches without this lock (<see cref="PacketRing.CopyTailTo"/>), so the encoders
    /// keep appending; only the remainder appended meanwhile is copied under it, then the switch.
    /// Called from one thread, never alongside <see cref="Restart"/> or another resize.
    /// </summary>
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

    /// <summary>
    /// Pins <c>[fromUs, toUs)</c> from one generation: the newest that starts before
    /// <paramref name="toUs"/>. Less than <see cref="MinClipUs"/> past its boundary, the previous
    /// generation up to the boundary instead, when that holds at least <see cref="MinClipUs"/>;
    /// otherwise nothing. Null when there is nothing to cut. The cut itself runs outside the lock.
    /// </summary>
    public (RingCut Cut, int Generation)? Cut(long fromUs, long toUs)
    {
        Generation chosen;
        long cutFrom;
        long cutTo;
        bool fallback;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Generation? current = null;
            Generation? previous = null;
            for (var i = _generations.Count - 1; i >= 0; i--)
            {
                if (_generations[i].StartUs < toUs)
                {
                    current = _generations[i];
                    previous = i > 0 ? _generations[i - 1] : null;
                    break;
                }
            }

            if (current is null)
            {
                return null;
            }

            fallback = previous is not null && toUs - current.StartUs < MinClipUs;
            if (fallback)
            {
                chosen = previous!;
                cutFrom = fromUs;
                cutTo = current.StartUs;
            }
            else
            {
                chosen = current;
                cutFrom = Math.Max(fromUs, current.StartUs);
                cutTo = toUs;
            }

            chosen.Users++;
        }

        RingCut? cut = null;
        try
        {
            cut = chosen.Ring.Cut(cutFrom, cutTo);
            if (fallback && cut is not null && cut.EndUs - cut.StartUs < MinClipUs)
            {
                cut.Dispose();
                cut = null;
            }
        }
        finally
        {
            lock (_lock)
            {
                chosen.Users--;
                if (chosen.Retired && chosen.Users == 0)
                {
                    chosen.Ring.Dispose();
                }
            }
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

    private sealed class Generation(int number, PacketRing ring, long startUs)
    {
        public int Number { get; } = number;

        public PacketRing Ring { get; } = ring;

        /// <summary>
        /// Its boundary in µs since the anchor: <see cref="long.MinValue"/> for generation 1, else
        /// its ring's first keyframe, <see cref="long.MaxValue"/> until there is one. The lock guards it.
        /// </summary>
        public long StartUs { get; set; } = startUs;

        /// <summary>Cuts reading its ring outside the lock. The lock guards it.</summary>
        public int Users { get; set; }

        /// <summary>The lock guards it.</summary>
        public bool Retired { get; set; }
    }
}
