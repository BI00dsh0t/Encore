using System.Buffers.Binary;

namespace ReplayBuffer.Core.Library;

/// <summary>
/// The one MP4 box walk (R2, FR-12). The Library's partial-file check: a file is a complete MP4
/// when its top-level boxes start with <c>ftyp</c>, include a <c>moov</c>, and end exactly at the
/// file's length, so a copy that stopped, or a write cut off mid-box, fails the walk. And where a
/// fragmented Recording's last whole fragment ends, so one a dead engine left open can be trimmed
/// back to it. A box is a 32-bit big-endian size and a four-letter type; a size of 1 means a 64-bit
/// size follows, and 0 means the box runs to the end of the file.
/// </summary>
public static class Mp4File
{
    private const uint Ftyp = ('f' << 24) | ('t' << 16) | ('y' << 8) | 'p';
    private const uint Moov = ('m' << 24) | ('o' << 16) | ('o' << 8) | 'v';
    private const uint Moof = ('m' << 24) | ('o' << 16) | ('o' << 8) | 'f';
    private const uint Mdat = ('m' << 24) | ('d' << 16) | ('a' << 8) | 't';

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
