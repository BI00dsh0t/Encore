using System.Buffers.Binary;

namespace ReplayBuffer.Core.Library;

/// <summary>
/// The Library's partial-file check (R2): a file is a complete MP4 when its top-level boxes start
/// with <c>ftyp</c>, include a <c>moov</c>, and end exactly at the file's length. A copy that
/// stopped, or a write cut off mid-box, fails the walk.
/// </summary>
public static class Mp4File
{
    /// <summary>Whether <paramref name="path"/> is a complete MP4.</summary>
    /// <exception cref="IOException">The file can't be read now, e.g. it is locked; retry later.</exception>
    public static bool IsComplete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        var length = stream.Length;
        Span<byte> header = stackalloc byte[16];
        long position = 0;
        var boxes = 0;
        var moov = false;
        while (position < length)
        {
            if (length - position < 8)
            {
                return false;
            }

            stream.Position = position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header.Slice(4, 4);
            long headerSize = 8;
            if (size == 1)
            {
                if (length - position < 16)
                {
                    return false;
                }

                stream.ReadExactly(header.Slice(8, 8));
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
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
                return false;
            }

            if (boxes == 0 && !type.SequenceEqual("ftyp"u8))
            {
                return false;
            }

            moov |= type.SequenceEqual("moov"u8);
            boxes++;
            position += size;
        }

        return boxes > 0 && moov && position == length;
    }
}
