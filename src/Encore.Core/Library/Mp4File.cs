using System.Buffers.Binary;

namespace Encore.Core.Library;

/// <summary>The one MP4 box walk: is a file complete, where a Recording's last whole fragment ends, and the length patch.</summary>
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

    /// <summary>Whether the file is a complete MP4; throws IOException when it can't be read now, so retry later.</summary>
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

    /// <summary>The end of the last whole box, not counting a <c>moof</c> whose <c>mdat</c> isn't whole; 0 when it isn't an MP4.</summary>
    public static long PlayableLength(Stream stream) => Walk(stream).Length;

    /// <summary>Truncates the file to its playable length, flushed to disk; returns the whole fragments kept.</summary>
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

    /// <summary>Plans the patch that gives a finished fragmented Recording its length; null unless the layout is as expected.</summary>
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

    /// <summary>The first box of a type among those that tile the given byte range, or null.</summary>
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

    /// <summary>The whole box at a position of the span, or null when it runs past the end.</summary>
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

/// <summary>The patch's two writes, made in order and each flushed; the file is a valid box tree between them.</summary>
public sealed record FragmentDurationPatch(long MehdOffset, byte[] MehdAndFree, long MvexOffset, byte[] MvexSize, ulong Duration, uint Timescale);
