using System.Buffers.Binary;

namespace Encore.Core.Library;

/// <summary>
/// The one MP4 box walk (R2, FR-12). The Library's partial-file check: a file is a complete MP4
/// when its top-level boxes start with <c>ftyp</c>, include a <c>moov</c>, and end exactly at the
/// file's length, so a copy that stopped, or a write cut off mid-box, fails the walk. And where a
/// fragmented Recording's last whole fragment ends, so one a dead engine left open can be trimmed
/// back to it. And the in-place <c>mehd</c> patch that gives a finalized one its length (R5, AD-27).
/// A box is a 32-bit big-endian size and a four-letter type; a size of 1 means a 64-bit
/// size follows, and 0 means the box runs to the end of the file.
/// </summary>
public static class Mp4File
{
    private const uint Ftyp = ('f' << 24) | ('t' << 16) | ('y' << 8) | 'p';
    private const uint Moov = ('m' << 24) | ('o' << 16) | ('o' << 8) | 'v';
    private const uint Moof = ('m' << 24) | ('o' << 16) | ('o' << 8) | 'f';
    private const uint Mdat = ('m' << 24) | ('d' << 16) | ('a' << 8) | 't';
    private const uint Mvhd = ('m' << 24) | ('v' << 16) | ('h' << 8) | 'd';
    private const uint Mvex = ('m' << 24) | ('v' << 16) | ('e' << 8) | 'x';
    private const uint Mehd = ('m' << 24) | ('e' << 16) | ('h' << 8) | 'd';
    private const uint Udta = ('u' << 24) | ('d' << 16) | ('t' << 8) | 'a';
    private const uint Free = ('f' << 24) | ('r' << 16) | ('e' << 8) | 'e';

    // A version-1 mehd: header, version and flags, a 64-bit fragment duration.
    private const int MehdSize = 20;

    /// <summary>Whether <paramref name="path"/> is a complete MP4.</summary>
    /// <exception cref="IOException">The file can't be read now, e.g. it is locked; retry later.</exception>
    public static bool IsComplete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        var length = stream.Length;
        long position = 0;
        var boxes = 0;
        var moov = false;
        while (position < length)
        {
            if (Next(stream, position, length) is not { } box || (boxes == 0 && box.Type != Ftyp))
            {
                return false;
            }

            moov |= box.Type == Moov;
            boxes++;
            position = box.End;
        }

        return boxes > 0 && moov;
    }

    /// <summary>
    /// The end of the last whole box from the start of <paramref name="stream"/>, not counting a
    /// <c>moof</c> whose <c>mdat</c> isn't whole after it; 0 when it has no <c>ftyp</c> first and a
    /// <c>moov</c>.
    /// </summary>
    public static long PlayableLength(Stream stream) => Walk(stream).Length;

    /// <summary>
    /// Truncates the file to <see cref="PlayableLength"/>, flushed to the disk, and returns how many
    /// whole fragments (<c>moof</c> + <c>mdat</c>) it kept.
    /// </summary>
    /// <exception cref="IOException">The file can't be opened, read or truncated.</exception>
    public static int TrimToLastFragment(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var (length, fragments) = Walk(stream);
        if (length < stream.Length)
        {
            stream.SetLength(length);
            stream.Flush(flushToDisk: true);
        }

        return fragments;
    }

    private static (long Length, int Fragments) Walk(Stream stream)
    {
        var length = stream.Length;
        var position = 0L;
        var end = 0L;
        var fragments = 0;
        var ftyp = false;
        var moov = false;
        var moof = false;
        while (position < length && Next(stream, position, length) is { } box)
        {
            if (position == 0 && box.Type != Ftyp)
            {
                return (0, 0);
            }

            if (box.Type == Moof)
            {
                if (moof)
                {
                    // Two moofs with no mdat between: not a fragment.
                    break;
                }

                moof = true;
            }
            else if (box.Type == Mdat)
            {
                fragments += moof ? 1 : 0;
                moof = false;
                end = box.End;
            }
            else if (moof)
            {
                // A moof must be followed by its mdat.
                break;
            }
            else
            {
                ftyp |= box.Type == Ftyp;
                moov |= box.Type == Moov;
                end = box.End;
            }

            position = box.End;
        }

        return ftyp && moov ? (end, fragments) : (0, 0);
    }

    /// <summary>
    /// The patch that gives a finalized fragmented Recording its length (R5, AD-27): Media
    /// Foundation otherwise stops at the last <c>tfra</c> entry. <paramref name="header"/> is the
    /// file's first bytes, its <c>ftyp</c> and <c>moov</c>; the patch turns the first 20 bytes of the
    /// <c>udta</c> right after <c>mvex</c> into a version-1 <c>mehd</c> of
    /// <paramref name="durationUs"/> in the <c>mvhd</c> timescale, rounded up, and the rest of it into
    /// a <c>free</c> box, then grows <c>mvex</c> by 20 to take the <c>mehd</c> in as its last child.
    /// No byte after the <c>moov</c> moves. Null unless the layout is exactly that: a <c>moov</c>
    /// with an <c>mvhd</c>, and an <c>mvex</c> with no <c>mehd</c> directly followed by a
    /// <c>udta</c> of 20 bytes or at least 28, both with 32-bit sizes.
    /// </summary>
    /// <param name="header">The file's bytes from offset 0 through at least its <c>moov</c>.</param>
    /// <param name="durationUs">The Recording's length in µs; positive.</param>
    public static FragmentDurationPatch? PlanFragmentDuration(ReadOnlySpan<byte> header, long durationUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationUs);
        if (FindChild(header, 0, header.Length, Moov) is not { } moov)
        {
            return null;
        }

        (int Start, int End)? mvhd = null;
        (int Start, int End)? mvex = null;
        (int Start, int End)? afterMvex = null;
        var position = moov.Start + moov.HeaderSize;
        while (position < moov.End)
        {
            if (Box(header, position, moov.End) is not { } child)
            {
                return null;
            }

            if (mvex is { } previous && afterMvex is null && previous.End == child.Start)
            {
                if (child.Type != Udta || child.HeaderSize != 8)
                {
                    return null;
                }

                afterMvex = (child.Start, child.End);
            }

            if (child.Type == Mvhd)
            {
                if (child.HeaderSize != 8)
                {
                    return null;
                }

                mvhd ??= (child.Start, child.End);
            }
            else if (child.Type == Mvex)
            {
                if (mvex is not null || child.HeaderSize != 8 || FindChild(header, child.Start + 8, child.End, Mehd) is not null)
                {
                    return null;
                }

                mvex = (child.Start, child.End);
            }

            position = child.End;
        }

        if (mvhd is not { } m || mvex is not { } x || afterMvex is not { } udta)
        {
            return null;
        }

        // mvhd: version and flags, then creation and modification times (32 or 64 bits), then the timescale.
        var timescaleAt = m.End - m.Start > 8 ? m.Start + (header[m.Start + 8] == 1 ? 28 : 20) : m.End;
        if (timescaleAt + 4 > m.End)
        {
            return null;
        }

        var timescale = BinaryPrimitives.ReadUInt32BigEndian(header[timescaleAt..]);
        var udtaSize = udta.End - udta.Start;
        if (timescale == 0 || udtaSize < MehdSize || udtaSize is > MehdSize and < MehdSize + 8)
        {
            return null;
        }

        var duration = (ulong)(((UInt128)(ulong)durationUs * timescale + 999_999) / 1_000_000);
        var mehdAndFree = new byte[udtaSize == MehdSize ? MehdSize : MehdSize + 8];
        BinaryPrimitives.WriteUInt32BigEndian(mehdAndFree, MehdSize);
        BinaryPrimitives.WriteUInt32BigEndian(mehdAndFree.AsSpan(4), Mehd);
        mehdAndFree[8] = 1; // version 1, flags 0
        BinaryPrimitives.WriteUInt64BigEndian(mehdAndFree.AsSpan(12), duration);
        if (mehdAndFree.Length > MehdSize)
        {
            BinaryPrimitives.WriteUInt32BigEndian(mehdAndFree.AsSpan(MehdSize), (uint)(udtaSize - MehdSize));
            BinaryPrimitives.WriteUInt32BigEndian(mehdAndFree.AsSpan(MehdSize + 4), Free);
        }

        var mvexSize = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(mvexSize, (uint)(x.End - x.Start + MehdSize));
        return new FragmentDurationPatch(udta.Start, mehdAndFree, x.Start, mvexSize, duration, timescale);
    }

    /// <summary>The first box of <paramref name="type"/> among those that tile [<paramref name="start"/>, <paramref name="end"/>), or null.</summary>
    private static (uint Type, int Start, int HeaderSize, int End)? FindChild(ReadOnlySpan<byte> span, int start, int end, uint type)
    {
        var position = start;
        while (position < end && Box(span, position, end) is { } box)
        {
            if (box.Type == type)
            {
                return box;
            }

            position = box.End;
        }

        return null;
    }

    /// <summary>The whole box at <paramref name="position"/> of <paramref name="span"/>, or null when it runs past <paramref name="end"/>.</summary>
    private static (uint Type, int Start, int HeaderSize, int End)? Box(ReadOnlySpan<byte> span, int position, int end)
    {
        if (end - position < 8)
        {
            return null;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
        var type = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
        var headerSize = 8;
        if (size == 1)
        {
            if (end - position < 16)
            {
                return null;
            }

            var large = BinaryPrimitives.ReadUInt64BigEndian(span[(position + 8)..]);
            size = large > int.MaxValue ? -1 : (long)large;
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = end - position;
        }

        return size < headerSize || size > end - position ? null : (type, position, headerSize, position + (int)size);
    }

    /// <summary>The whole box at <paramref name="position"/>, or null when its header or body runs past the end.</summary>
    private static (uint Type, long End)? Next(Stream stream, long position, long length)
    {
        Span<byte> header = stackalloc byte[16];
        if (length - position < 8)
        {
            return null;
        }

        stream.Position = position;
        stream.ReadExactly(header[..8]);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        var headerSize = 8L;
        if (size == 1)
        {
            if (length - position < 16)
            {
                return null;
            }

            stream.ReadExactly(header[8..16]);
            var large = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            size = large > long.MaxValue ? -1 : (long)large;
            headerSize = 16;
        }
        else if (size == 0)
        {
            // The last box runs to the end of the file.
            size = length - position;
        }

        if (size < headerSize || size > length - position)
        {
            return null;
        }

        return (type, position + size);
    }
}

/// <summary>
/// <see cref="Mp4File.PlanFragmentDuration"/>'s two writes, to be made in order, each flushed to the
/// disk before the next: the <c>mehd</c> and the <c>free</c> header over the <c>udta</c>'s start,
/// then <c>mvex</c>'s new size. The file is a valid box tree before, between and after them.
/// </summary>
/// <param name="MehdOffset">The file offset of the first write: the <c>udta</c>'s start.</param>
/// <param name="MehdAndFree">The <c>mehd</c> (20 bytes), then the <c>free</c> box's header unless the <c>udta</c> was exactly 20 bytes.</param>
/// <param name="MvexOffset">The file offset of the second write: <c>mvex</c>'s size field.</param>
/// <param name="MvexSize">The <c>mvex</c>'s new 32-bit big-endian size, 20 more than before.</param>
/// <param name="Duration">The <c>mehd</c>'s fragment duration, in <paramref name="Timescale"/> units.</param>
/// <param name="Timescale">The <c>mvhd</c> timescale.</param>
public sealed record FragmentDurationPatch(long MehdOffset, byte[] MehdAndFree, long MvexOffset, byte[] MvexSize, ulong Duration, uint Timescale);
