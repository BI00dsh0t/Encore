namespace Encore.Core.Replay;

/// <summary>Rewind's ring: encoded packets of every stream in reference-counted slabs, trimmed to the Buffer Window.</summary>
public sealed partial class PacketRing : IDisposable
{
    /// <summary>The size of a shared slab; a larger packet gets a slab of its own size.</summary>
    public const int DefaultSlabBytes = 4 * 1024 * 1024;

    /// <summary>Kept beyond the window, so the first keyframe inside it is still held (the GOP is at most 1 s).</summary>
    public const long SlackUs = 2_000_000;

    private const long MicrosPerSecond = 1_000_000;

    /// <summary>How far before a cut's keyframe its copy reaches back, for audio that leads the video.</summary>
    private const long LookBackUs = MicrosPerSecond;

    /// <summary>The entries <see cref="CopyTailTo"/> copies per hold of the locks.</summary>
    private const int CopyBatch = 256;

    private readonly Lock _lock = new();
    private readonly long _windowUs;
    private readonly int _slabBytes;
    private readonly List<StreamInfo> _streams = [];

    // Every slab the ring itself holds a reference on: the current one, and those with live entries.
    private readonly List<Slab> _held = [];
    private RingEntry[] _entries;
    private int _head;
    private int _count;

    // The absolute sequence number of the entry at _head; entry i (from the head) is _headSeq + i.
    private long _headSeq;

    // The video keyframe entries' sequence numbers, oldest first: circular, as long as _entries.
    private long[] _keys;
    private int _keyHead;
    private int _keyCount;
    private int _videoStream = -1;
    private Slab? _current;
    private long _newestUs = long.MinValue;
    private int _liveSlabs;
    private int _memorySlabs;
    private bool _disposed;

    // Disk mode's file, and the bytes a copy out of it reads through, grown as needed.
    private readonly RingFile? _file;
    private byte[] _scratch = [];
    private readonly MemoryMargin? _margin;

    /// <summary>Capacity is the entries preallocated; margin, when set, is checked before each new native-memory slab.</summary>
    public PacketRing(long windowUs, int capacity, int slabBytes = DefaultSlabBytes, MemoryMargin? margin = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowUs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slabBytes, 1);
        _windowUs = windowUs;
        _slabBytes = slabBytes;
        _entries = new RingEntry[capacity];
        _keys = new long[capacity];
        _margin = margin;
    }

    /// <summary>A Disk-mode ring: its slabs are the file's blocks, and it owns the file.</summary>
    public PacketRing(long windowUs, int capacity, RingFile file, MemoryMargin? margin = null)
        : this(windowUs, capacity, (file ?? throw new ArgumentNullException(nameof(file))).BlockBytes, margin)
    {
        _file = file;
    }

    /// <summary>Disk mode's ring file; null in Memory mode.</summary>
    public RingFile? File => _file;

    /// <summary>The Buffer Window it was sized for, in µs.</summary>
    public long WindowUs => _windowUs;

    /// <summary>The oldest indexed video keyframe's start, in µs since the anchor; MaxValue when there is none.</summary>
    internal long FirstKeyUs
    {
        get
        {
            lock (_lock)
            {
                return _keyCount == 0 ? long.MaxValue : EntryAt((int)(_keys[_keyHead] - _headSeq)).TimeUs;
            }
        }
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

    /// <summary>On a Disk-mode ring, the slabs that went to native memory instead of a block.</summary>
    internal int MemorySlabs
    {
        get
        {
            lock (_lock)
            {
                return _memorySlabs;
            }
        }
    }

    /// <summary>Registers a stream before its first packet and returns its index; the video stream's keyframes start a cut.</summary>
    public int AddStream(int timeBaseNum, int timeBaseDen, bool isVideo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBaseNum, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBaseDen, 1);
        lock (_lock)
        {
            _streams.Add(new StreamInfo(timeBaseNum, timeBaseDen, isVideo));
            var index = _streams.Count - 1;
            if (isVideo && _videoStream < 0)
            {
                _videoStream = index;
            }

            return index;
        }
    }

    /// <summary>The first video stream's index; -1 when there is none.</summary>
    public int VideoStream
    {
        get
        {
            lock (_lock)
            {
                return _videoStream;
            }
        }
    }

    /// <summary>Converts a stream's ticks to µs since the anchor, by the same formula that sets each entry's start.</summary>
    public long ToMicros(int stream, long ticks)
    {
        lock (_lock)
        {
            return ToMicros(ticks, _streams[stream]);
        }
    }

    /// <summary>Copies one packet in, then trims the oldest entries that fall outside the window plus its slack.</summary>
    public void Append(int stream, ReadOnlySpan<byte> data, long pts, long dts, long duration, bool isKey)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var info = _streams[stream];
            var slab = SlabFor(data.Length);
            var offset = slab.Used;
            slab.Write(offset, data);
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

            var sequence = _headSeq + _count;
            if (slab.FirstSeq < 0)
            {
                slab.FirstSeq = sequence;
            }

            slab.LastSeq = sequence;
            _entries[(_head + _count) % _entries.Length] = new RingEntry(slab, offset, data.Length, stream, pts, dts, duration, isKey, timeUs);
            _count++;
            if (isKey && info.IsVideo)
            {
                // Never full: it holds at most one sequence per entry, and grows with the entries.
                _keys[(_keyHead + _keyCount) % _keys.Length] = sequence;
                _keyCount++;
            }

            Trim();
        }
    }

    /// <summary>Whether every stream's newest packet ends at or after a time, so a cut there holds all it will get.</summary>
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

    /// <summary>Pins what a Clip of a time range holds, from the first video keyframe in it; null when there is none.</summary>
    public RingCut? Cut(long fromUs, long toUs)
    {
        // Under the lock only what pins; the filter, the sort and the projection run after it, on pinned bytes.
        RingEntry[] copied;
        StreamInfo[] streams;
        List<Slab> slabs;
        long[] origins;
        RingEntry keyEntry;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = KeyAtOrAfter(fromUs);
            if (key < 0)
            {
                return null;
            }

            keyEntry = EntryAt(key);
            if (keyEntry.TimeUs >= toUs)
            {
                return null;
            }

            var first = LookBack(key, keyEntry.TimeUs);

            copied = new RingEntry[_count - first];
            var start = (_head + first) % _entries.Length;
            var head = Math.Min(copied.Length, _entries.Length - start);
            Array.Copy(_entries, start, copied, 0, head);
            Array.Copy(_entries, 0, copied, head, copied.Length - head);

            // One pin per slab whose entries meet the copied range.
            var firstSeq = _headSeq + first;
            var lastSeq = _headSeq + _count - 1;
            slabs = [];
            foreach (var slab in _held)
            {
                if (slab.FirstSeq >= 0 && slab.FirstSeq <= lastSeq && slab.LastSeq >= firstSeq)
                {
                    slab.AddRef();
                    slabs.Add(slab);
                }
            }

            streams = [.. _streams];

            // Each stream is rebased to K in its own time base; the video exactly to K's PTS.
            origins = new long[streams.Length];
            for (var s = 0; s < origins.Length; s++)
            {
                origins[s] = s == keyEntry.Stream ? keyEntry.Pts : FromMicros(keyEntry.TimeUs, streams[s]);
            }
        }

        var keyUs = keyEntry.TimeUs;
        var picked = new List<RingEntry>(copied.Length);
        foreach (var entry in copied)
        {
            var info = streams[entry.Stream];
            var at = info.IsVideo ? entry.TimeUs : entry.TimeUs + (ToMicros(entry.Duration, info) / 2);
            if (at >= keyUs && at < toUs)
            {
                picked.Add(entry);
            }
        }

        var packets = picked
            .OrderBy(e => e.TimeUs)
            .Select(e => new RingPacket(e.Slab, e.Offset, e.Length, e.Stream, e.Pts, e.Dts, e.Duration, e.IsKey, e.TimeUs))
            .ToArray();
        return new RingCut(packets, slabs, origins, keyUs, toUs);
    }

    /// <summary>Seeds another ring with this ring's packets from a time on, in batches so appends go on.</summary>
    internal long CopyTailTo(PacketRing target, long fromUs)
    {
        ArgumentNullException.ThrowIfNull(target);
        long sequence;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = KeyAtOrAfter(fromUs);
            int first;
            if (key >= 0)
            {
                first = LookBack(key, EntryAt(key).TimeUs);
            }
            else
            {
                first = 0;
                while (first < _count && EntryAt(first).TimeUs < fromUs - LookBackUs)
                {
                    first++;
                }
            }

            sequence = _headSeq + first;
        }

        while (true)
        {
            var next = CopyBatchTo(target, sequence, CopyBatch);
            if (next - sequence < CopyBatch)
            {
                return next;
            }

            sequence = next;
        }
    }

    /// <summary>Copies every entry from a sequence number to the newest into the target, in one hold of both locks.</summary>
    internal void CopyFrom(PacketRing target, long sequence)
    {
        ArgumentNullException.ThrowIfNull(target);
        CopyBatchTo(target, sequence, int.MaxValue);
    }

    /// <summary>Copies up to max entries from a sequence number into the target; returns the next sequence number.</summary>
    private unsafe long CopyBatchTo(PacketRing target, long sequence, int max)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (target._lock)
            {
                if (target._streams.Count == 0)
                {
                    foreach (var stream in _streams)
                    {
                        target.AddStream(stream.Num, stream.Den, stream.IsVideo);
                    }
                }

                var first = (int)Math.Max(0, sequence - _headSeq);
                var end = (int)Math.Min(_count, (long)first + max);
                for (var i = first; i < end; i++)
                {
                    var entry = EntryAt(i);
                    ReadOnlySpan<byte> bytes;
                    if (entry.Slab.File is null)
                    {
                        // A memory slab is read in place.
                        bytes = new ReadOnlySpan<byte>(entry.Slab.Data + entry.Offset, entry.Length);
                    }
                    else
                    {
                        if (_scratch.Length < entry.Length)
                        {
                            _scratch = new byte[Math.Max(entry.Length, _scratch.Length * 2)];
                        }

                        var read = _scratch.AsSpan(0, entry.Length);
                        entry.Slab.Read(entry.Offset, read);
                        bytes = read;
                    }

                    target.Append(entry.Stream, bytes, entry.Pts, entry.Dts, entry.Duration, entry.IsKey);
                }

                return _headSeq + Math.Max(end, first);
            }
        }
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

            Volatile.Write(ref _disposed, true);
            _count = 0;
            _current = null;
            foreach (var slab in _held)
            {
                slab.Release();
            }

            _held.Clear();
            _entries = [];
            _keys = [];
            _keyCount = 0;
        }

        if (LiveSlabs == 0)
        {
            _file?.Close();
        }
    }

    private RingEntry EntryAt(int position) => _entries[(_head + position) % _entries.Length];

    /// <summary>The position of the first video keyframe at or after a time; -1 when there is none. Under the lock.</summary>
    private int KeyAtOrAfter(long fromUs)
    {
        for (var k = 0; k < _keyCount; k++)
        {
            var position = (int)(_keys[(_keyHead + k) % _keys.Length] - _headSeq);
            if (EntryAt(position).TimeUs >= fromUs)
            {
                return position;
            }
        }

        return -1;
    }

    /// <summary>The first position a copy from a keyframe takes; the look-back catches audio appended before the keyframe.</summary>
    private int LookBack(int key, long keyUs)
    {
        var first = key;
        while (first > 0 && EntryAt(first - 1).TimeUs > keyUs - LookBackUs)
        {
            first--;
        }

        return first;
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
        var slab = TryBlockSlab(capacity);
        if (slab is null && _margin?.Allows(capacity) == false)
        {
            Reclaim(capacity);
            slab = TryBlockSlab(capacity);
        }

        if (slab is null)
        {
            slab = new Slab(capacity, this);
            if (_file is not null)
            {
                _memorySlabs++;
            }
        }

        Interlocked.Increment(ref _liveSlabs);
        _held.Add(slab);
        return slab;
    }

    /// <summary>A Disk-mode slab in a free block of the file; null in Memory mode, for an oversize packet, or with none free.</summary>
    private Slab? TryBlockSlab(int capacity) =>
        _file is { } file && capacity <= file.BlockBytes && file.TryRent(out var block)
            ? new Slab(file.BlockBytes, this, file, block)
            : null;

    /// <summary>Inside the margin: drops the oldest entries until enough memory is freed for the new slab. Under the lock.</summary>
    private void Reclaim(long bytes)
    {
        var blockFits = _file is { } file && bytes <= file.BlockBytes;
        if (!AnyFreeable(blockFits))
        {
            return;
        }

        long freed = 0;
        var blockReturned = false;
        var dropped = false;
        while (freed < bytes && !blockReturned && _count > 0 && !ReferenceEquals(_entries[_head].Slab, _current))
        {
            dropped = true;
            if (DropHead() is { } released)
            {
                if (released.File is null)
                {
                    freed += released.Capacity;
                }
                else
                {
                    blockReturned = blockFits;
                }
            }
        }

        if (dropped && (_count == 0 || _newestUs - _entries[_head].TimeUs < RingGenerations.MinClipUs))
        {
            _margin!.ReportShortRing();
        }
    }

    /// <summary>Whether a trim could free anything: a slab only the ring holds, other than the current one. Under the lock.</summary>
    private bool AnyFreeable(bool blockFits)
    {
        var stop = _current is { FirstSeq: >= 0 } current ? current.FirstSeq : long.MaxValue;
        foreach (var slab in _held)
        {
            if (!ReferenceEquals(slab, _current) && slab.LastSeq < stop && slab.RingOnly && (slab.File is null || blockFits))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Releases the ring's reference on <paramref name="slab"/>; whether that freed it.</summary>
    private bool ReleaseHeld(Slab slab)
    {
        _held.Remove(slab);
        return slab.Release();
    }

    private void Trim()
    {
        var oldest = _newestUs - _windowUs - SlackUs;
        while (_count > 0 && _entries[_head].TimeUs < oldest)
        {
            DropHead();
        }
    }

    /// <summary>Drops the oldest entry; returns its slab when that freed it, else null. Under the lock.</summary>
    private Slab? DropHead()
    {
        var slab = _entries[_head].Slab;
        _entries[_head] = default;
        if (_keyCount > 0 && _keys[_keyHead] == _headSeq)
        {
            _keyHead = (_keyHead + 1) % _keys.Length;
            _keyCount--;
        }

        _head = (_head + 1) % _entries.Length;
        _headSeq++;
        _count--;
        slab.Live--;
        return slab.Live == 0 && !ReferenceEquals(slab, _current) && ReleaseHeld(slab) ? slab : null;
    }

    private void Grow()
    {
        var grown = new RingEntry[_entries.Length * 2];
        for (var i = 0; i < _count; i++)
        {
            grown[i] = _entries[(_head + i) % _entries.Length];
        }

        var keys = new long[grown.Length];
        for (var k = 0; k < _keyCount; k++)
        {
            keys[k] = _keys[(_keyHead + k) % _keys.Length];
        }

        _entries = grown;
        _head = 0;
        _keys = keys;
        _keyHead = 0;
    }

    private void OnFreed()
    {
        if (Interlocked.Decrement(ref _liveSlabs) == 0 && Volatile.Read(ref _disposed))
        {
            _file?.Close();
        }
    }

    private sealed class StreamInfo(int num, int den, bool isVideo)
    {
        public int Num { get; } = num;

        public int Den { get; } = den;

        public bool IsVideo { get; } = isVideo;

        public long NewestEndUs { get; set; } = long.MinValue;
    }

    private readonly record struct RingEntry(Slab Slab, int Offset, int Length, int Stream, long Pts, long Dts, long Duration, bool IsKey, long TimeUs);
}
