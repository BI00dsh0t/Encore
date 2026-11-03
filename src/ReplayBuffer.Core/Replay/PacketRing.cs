using System.Runtime.InteropServices;

namespace ReplayBuffer.Core.Replay;

/// <summary>
/// The Memory-mode Replay Buffer (AD-8): encoded packets of every stream, newest last, their bytes
/// in reference-counted native slabs, trimmed to the Buffer Window plus <see cref="SlackUs"/> of GOP
/// slack. The entry array is preallocated and circular (it doubles only when full), and the bytes go
/// into the current slab, so an append allocates nothing managed. A slab is freed once the ring no
/// longer needs it and no <see cref="RingCut"/> pins it, so trimming or disposing the ring never frees
/// bytes a writer is reading. <see cref="Append"/> and <see cref="Cut"/> take one short lock; a cut's
/// bytes are read with none, since they are never rewritten.
/// </summary>
public sealed class PacketRing : IDisposable
{
    /// <summary>The size of a shared slab; a larger packet gets a slab of its own size.</summary>
    public const int DefaultSlabBytes = 4 * 1024 * 1024;

    /// <summary>Kept beyond the window, so the first keyframe inside it is still held (the GOP is at most 1 s).</summary>
    public const long SlackUs = 2_000_000;

    private const long MicrosPerSecond = 1_000_000;

    private readonly Lock _lock = new();
    private readonly long _windowUs;
    private readonly int _slabBytes;
    private readonly List<StreamInfo> _streams = [];

    // Every slab the ring itself holds a reference on: the current one, and those with live entries.
    private readonly List<Slab> _held = [];
    private RingEntry[] _entries;
    private int _head;
    private int _count;
    private Slab? _current;
    private long _newestUs = long.MinValue;
    private int _liveSlabs;
    private bool _disposed;

    /// <param name="windowUs">The Buffer Window, in µs.</param>
    /// <param name="capacity">The entries preallocated: the packets the window and its slack hold.</param>
    /// <param name="slabBytes">The shared slab size.</param>
    public PacketRing(long windowUs, int capacity, int slabBytes = DefaultSlabBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowUs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slabBytes, 1);
        _windowUs = windowUs;
        _slabBytes = slabBytes;
        _entries = new RingEntry[capacity];
    }

    /// <summary>The packets held.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>The oldest packet's start, in µs since the anchor; <see cref="long.MaxValue"/> when empty.</summary>
    internal long OldestUs
    {
        get
        {
            lock (_lock)
            {
                return _count == 0 ? long.MaxValue : _entries[_head].TimeUs;
            }
        }
    }

    /// <summary>Slabs not yet freed, whether the ring or a cut holds them.</summary>
    internal int LiveSlabs => Volatile.Read(ref _liveSlabs);

    /// <summary>Registers a stream before its first packet; returns its index.</summary>
    /// <param name="timeBaseNum">The numerator of the time base its timestamps are in.</param>
    /// <param name="timeBaseDen">The denominator.</param>
    /// <param name="isVideo">Whether it is the video stream, whose keyframes start a cut.</param>
    public int AddStream(int timeBaseNum, int timeBaseDen, bool isVideo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBaseNum, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBaseDen, 1);
        lock (_lock)
        {
            _streams.Add(new StreamInfo(timeBaseNum, timeBaseDen, isVideo));
            return _streams.Count - 1;
        }
    }

    /// <summary>
    /// Copies one packet in, then trims from the oldest while it starts more than the window plus
    /// <see cref="SlackUs"/> before the newest.
    /// </summary>
    public unsafe void Append(int stream, ReadOnlySpan<byte> data, long pts, long dts, long duration, bool isKey)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var info = _streams[stream];
            var slab = SlabFor(data.Length);
            var offset = slab.Used;
            data.CopyTo(new Span<byte>(slab.Data + offset, data.Length));
            slab.Used += data.Length;
            slab.Live++;

            var timeUs = ToMicros(pts, info);
            var endUs = timeUs + ToMicros(duration, info);
            info.NewestEndUs = Math.Max(info.NewestEndUs, endUs);
            _newestUs = Math.Max(_newestUs, timeUs);

            if (_count == _entries.Length)
            {
                Grow();
            }

            _entries[(_head + _count) % _entries.Length] = new RingEntry(slab, offset, data.Length, stream, pts, dts, duration, isKey, timeUs);
            _count++;
            Trim();
        }
    }

    /// <summary>
    /// Whether every stream's newest packet ends at or after <paramref name="us"/> (µs since the
    /// anchor), so a cut there holds all it will get.
    /// </summary>
    public bool AllStreamsPast(long us)
    {
        lock (_lock)
        {
            foreach (var stream in _streams)
            {
                if (stream.NewestEndUs < us)
                {
                    return false;
                }
            }

            return _streams.Count > 0;
        }
    }

    /// <summary>
    /// Pins what a Clip of <c>[startUs, endUs]</c> (µs since the anchor) holds: from K, the first
    /// video keyframe at or after <paramref name="startUs"/> and not after <paramref name="endUs"/>,
    /// every video packet up to <paramref name="endUs"/>, and every other packet that overlaps
    /// <c>[K, endUs]</c>, in time order. Null when there is no such keyframe.
    /// </summary>
    public RingCut? Cut(long startUs, long endUs)
    {
        // Under the lock only what pins: the sort and the projection run after it, on pinned bytes.
        List<RingEntry> picked;
        List<Slab> slabs;
        long[] origins;
        long keyUs;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = -1;
            for (var i = 0; i < _count; i++)
            {
                ref var entry = ref _entries[(_head + i) % _entries.Length];
                if (entry.IsKey && _streams[entry.Stream].IsVideo && entry.TimeUs >= startUs && entry.TimeUs <= endUs)
                {
                    key = i;
                    break;
                }
            }

            if (key < 0)
            {
                return null;
            }

            var keyEntry = _entries[(_head + key) % _entries.Length];
            keyUs = keyEntry.TimeUs;
            picked = new List<RingEntry>();
            for (var i = 0; i < _count; i++)
            {
                var entry = _entries[(_head + i) % _entries.Length];
                var info = _streams[entry.Stream];
                var keep = info.IsVideo
                    ? entry.TimeUs >= keyUs && entry.TimeUs <= endUs
                    : entry.TimeUs < endUs && entry.TimeUs + ToMicros(entry.Duration, info) > keyUs;
                if (keep)
                {
                    picked.Add(entry);
                }
            }

            // One pin per slab the cut reads.
            var pinned = new HashSet<Slab>(ReferenceEqualityComparer.Instance);
            slabs = new List<Slab>();
            foreach (var entry in picked)
            {
                if (pinned.Add(entry.Slab))
                {
                    slabs.Add(entry.Slab);
                    entry.Slab.AddRef();
                }
            }

            // Each stream is rebased to K in its own time base; the video exactly to K's PTS.
            origins = new long[_streams.Count];
            for (var s = 0; s < origins.Length; s++)
            {
                origins[s] = s == keyEntry.Stream ? keyEntry.Pts : FromMicros(keyUs, _streams[s]);
            }
        }

        var packets = picked
            .OrderBy(e => e.TimeUs)
            .Select(e => new RingPacket(e.Slab, e.Offset, e.Length, e.Stream, e.Pts, e.Dts, e.Duration, e.IsKey, e.TimeUs))
            .ToArray();
        return new RingCut(packets, slabs, origins, keyUs, endUs);
    }

    /// <summary>Drops every packet and the ring's own references; slabs a cut pins live until it is disposed.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _count = 0;
            _current = null;
            foreach (var slab in _held)
            {
                slab.Release();
            }

            _held.Clear();
            _entries = [];
        }
    }

    private static long ToMicros(long ticks, StreamInfo stream) =>
        (long)((Int128)ticks * MicrosPerSecond * stream.Num / stream.Den);

    private static long FromMicros(long us, StreamInfo stream) =>
        (long)((Int128)us * stream.Den / ((Int128)MicrosPerSecond * stream.Num));

    /// <summary>The slab a packet of <paramref name="length"/> bytes goes into: the current one, a new one, or its own.</summary>
    private Slab SlabFor(int length)
    {
        if (_current is { } current && current.Capacity - current.Used >= length)
        {
            return current;
        }

        if (length > _slabBytes)
        {
            var own = NewSlab(length);
            return own;
        }

        var next = NewSlab(_slabBytes);
        if (_current is { Live: 0 } old)
        {
            // No live entries and no longer current: the ring is done with it.
            ReleaseHeld(old);
        }

        _current = next;
        return next;
    }

    private Slab NewSlab(int capacity)
    {
        var slab = new Slab(capacity, this);
        Interlocked.Increment(ref _liveSlabs);
        _held.Add(slab);
        return slab;
    }

    private void ReleaseHeld(Slab slab)
    {
        _held.Remove(slab);
        slab.Release();
    }

    private void Trim()
    {
        var oldest = _newestUs - _windowUs - SlackUs;
        while (_count > 0 && _entries[_head].TimeUs < oldest)
        {
            var slab = _entries[_head].Slab;
            _entries[_head] = default;
            _head = (_head + 1) % _entries.Length;
            _count--;
            slab.Live--;
            if (slab.Live == 0 && !ReferenceEquals(slab, _current))
            {
                ReleaseHeld(slab);
            }
        }
    }

    private void Grow()
    {
        var grown = new RingEntry[_entries.Length * 2];
        for (var i = 0; i < _count; i++)
        {
            grown[i] = _entries[(_head + i) % _entries.Length];
        }

        _entries = grown;
        _head = 0;
    }

    private void OnFreed() => Interlocked.Decrement(ref _liveSlabs);

    private sealed class StreamInfo(int num, int den, bool isVideo)
    {
        public int Num { get; } = num;

        public int Den { get; } = den;

        public bool IsVideo { get; } = isVideo;

        public long NewestEndUs { get; set; } = long.MinValue;
    }

    private readonly record struct RingEntry(Slab Slab, int Offset, int Length, int Stream, long Pts, long Dts, long Duration, bool IsKey, long TimeUs);

    /// <summary>
    /// One native block of packet bytes. Its count is the ring's one reference (while it is current
    /// or holds live entries) plus one per cut that reads it; it is freed at 0.
    /// </summary>
    internal sealed unsafe class Slab
    {
        private readonly PacketRing _owner;
        private int _references = 1;

        public Slab(int capacity, PacketRing owner)
        {
            _owner = owner;
            Capacity = capacity;
            Data = (byte*)NativeMemory.Alloc((nuint)capacity);
        }

        public byte* Data { get; private set; }

        public int Capacity { get; }

        /// <summary>Bytes written so far; the ring's lock guards it.</summary>
        public int Used { get; set; }

        /// <summary>Ring entries in it; the ring's lock guards it.</summary>
        public int Live { get; set; }

        public void AddRef() => Interlocked.Increment(ref _references);

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                NativeMemory.Free(Data);
                Data = null;
                _owner.OnFreed();
            }
        }
    }
}

/// <summary>One packet of a <see cref="RingCut"/>; its bytes are valid until the cut is disposed.</summary>
public readonly struct RingPacket
{
    private readonly PacketRing.Slab _slab;
    private readonly int _offset;

    internal RingPacket(PacketRing.Slab slab, int offset, int length, int stream, long pts, long dts, long duration, bool isKey, long timeUs)
    {
        _slab = slab;
        _offset = offset;
        Length = length;
        Stream = stream;
        Pts = pts;
        Dts = dts;
        Duration = duration;
        IsKey = isKey;
        TimeUs = timeUs;
    }

    public int Stream { get; }

    /// <summary>In the stream's time base, as the encoder stamped it.</summary>
    public long Pts { get; }

    public long Dts { get; }

    public long Duration { get; }

    public bool IsKey { get; }

    /// <summary>Its start, in µs since the anchor.</summary>
    public long TimeUs { get; }

    public int Length { get; }

    public unsafe ReadOnlySpan<byte> Bytes => new(_slab.Data + _offset, Length);
}

/// <summary>
/// A pinned slice of a <see cref="PacketRing"/> for one Clip. Its slabs stay allocated, whatever
/// the ring does, until it is disposed.
/// </summary>
public sealed class RingCut : IDisposable
{
    private readonly RingPacket[] _packets;
    private readonly List<PacketRing.Slab> _slabs;
    private readonly long[] _origins;
    private int _disposed;

    internal RingCut(RingPacket[] packets, List<PacketRing.Slab> slabs, long[] origins, long startUs, long endUs)
    {
        _packets = packets;
        _slabs = slabs;
        _origins = origins;
        StartUs = startUs;
        EndUs = endUs;
    }

    /// <summary>The packets, in time order.</summary>
    public IReadOnlyList<RingPacket> Packets => _packets;

    /// <summary>The keyframe the Clip starts at, in µs since the anchor.</summary>
    public long StartUs { get; }

    /// <summary>Where it was cut, in µs since the anchor.</summary>
    public long EndUs { get; }

    /// <summary>Stream <paramref name="stream"/>'s time 0 in the Clip, in its own time base.</summary>
    public long Origin(int stream) => _origins[stream];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var slab in _slabs)
        {
            slab.Release();
        }
    }
}
