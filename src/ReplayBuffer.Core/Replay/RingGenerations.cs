namespace ReplayBuffer.Core.Replay;

/// <summary>
/// The Replay Buffer as numbered generations (R5, AD-8), each a <see cref="PacketRing"/>; one is
/// live, the current one, and packets go into it. A <see cref="Resize"/> (a Buffer Window change at
/// a Reset) opens a generation with the same streams, seeded with the old ring's packets from the
/// Reset keyframe on, and retires the old one at once, so the timeline stays continuous; a Clip is
/// cut from one generation. A retired ring is disposed once no cut is reading it; slabs a cut pins
/// outlive it. Routing a packet is a field read under one short lock, so <see cref="Append"/>
/// allocates nothing managed; a cut's filter and sort run outside that lock.
/// </summary>
public sealed class RingGenerations : IDisposable
{
    /// <summary>The shortest Clip (R4).</summary>
    public const long MinClipUs = 1_000_000;

    private readonly Lock _lock = new();
    private Generation _current;
    private bool _disposed;

    /// <param name="first">Generation 1's ring; this owns it.</param>
    public RingGenerations(PacketRing first)
    {
        ArgumentNullException.ThrowIfNull(first);
        _current = new Generation(1, first, long.MinValue);
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

    /// <summary>Appends one packet to the current generation.</summary>
    public void Append(int stream, ReadOnlySpan<byte> data, long pts, long dts, long duration, bool isKey)
    {
        lock (_lock)
        {
            _current.Ring.Append(stream, data, pts, dts, duration, isKey);
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
    /// Opens a generation on <paramref name="next"/> (a Buffer Window resize at a Reset), seeded
    /// with the current ring's packets from the keyframe at or after <paramref name="fromUs"/> on,
    /// and retires the old one at once. Returns the new generation's number. Most of the tail is
    /// copied in batches without this lock (<see cref="PacketRing.CopyTailTo"/>), so the encoders
    /// keep appending; only the remainder appended meanwhile is copied under it, then the switch.
    /// Called from one thread, never alongside another resize.
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
            _current = new Generation(previous.Number + 1, next, next.FirstKeyUs);
            Retire(previous);
            return _current.Number;
        }
    }

    /// <summary>
    /// Pins <c>[fromUs, toUs)</c> from the current generation, from its start on; null when it
    /// starts at or after <paramref name="toUs"/>, or when there is nothing to cut. The cut itself
    /// runs outside the lock.
    /// </summary>
    public (RingCut Cut, int Generation)? Cut(long fromUs, long toUs)
    {
        Generation chosen;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            chosen = _current;
            if (chosen.StartUs >= toUs)
            {
                return null;
            }

            chosen.Users++;
        }

        RingCut? cut;
        try
        {
            cut = chosen.Ring.Cut(Math.Max(fromUs, chosen.StartUs), toUs);
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

    /// <summary>Disposes the ring; one a cut is still reading goes when that cut has pinned its slabs.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Retire(_current);
        }
    }

    /// <summary>Drops a generation: it is never cut again, and its ring is disposed once no cut reads it. Under the lock.</summary>
    private void Retire(Generation generation)
    {
        if (generation.Retired)
        {
            return;
        }

        generation.Retired = true;
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
        /// the first keyframe its resize seeded its ring with.
        /// </summary>
        public long StartUs { get; } = startUs;

        /// <summary>Cuts reading its ring outside the lock. The lock guards it.</summary>
        public int Users { get; set; }

        /// <summary>The lock guards it.</summary>
        public bool Retired { get; set; }
    }
}
