using System.Runtime.InteropServices;

namespace Encore.Core.Replay;

public sealed partial class PacketRing
{
    /// <summary>One block of packet bytes, in memory or in the ring file; freed when the ring and every cut have let go.</summary>
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

        public Slab(int capacity, PacketRing owner, RingFile file, long fileOffset)
        {
            _owner = owner;
            Capacity = capacity;
            File = file;
            FileOffset = fileOffset;
        }

        /// <summary>A memory slab's bytes; null for a file slab.</summary>
        public byte* Data { get; private set; }

        /// <summary>A file slab's ring file; null for a memory slab.</summary>
        public RingFile? File { get; }

        /// <summary>Where a file slab's block starts in the file.</summary>
        public long FileOffset { get; }

        public int Capacity { get; }

        /// <summary>Bytes written so far; the ring's lock guards it.</summary>
        public int Used { get; set; }

        /// <summary>Ring entries in it; the ring's lock guards it.</summary>
        public int Live { get; set; }

        /// <summary>The sequence number of the first entry appended into it; -1 before one. The ring's lock guards it.</summary>
        public long FirstSeq { get; set; } = -1;

        /// <summary>The sequence number of the last entry appended into it. The ring's lock guards it.</summary>
        public long LastSeq { get; set; } = -1;

        /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/> into the slab; the ring's lock guards it.</summary>
        public void Write(int offset, ReadOnlySpan<byte> data)
        {
            if (File is { } file)
            {
                file.Write(FileOffset + offset, data);
            }
            else
            {
                data.CopyTo(new Span<byte>(Data + offset, data.Length));
            }
        }

        /// <summary>Reads bytes from an offset into the destination; bytes once written never change.</summary>
        public void Read(int offset, Span<byte> destination)
        {
            if (File is { } file)
            {
                file.Read(FileOffset + offset, destination);
            }
            else
            {
                new ReadOnlySpan<byte>(Data + offset, destination.Length).CopyTo(destination);
            }
        }

        /// <summary>Whether the ring's is its only reference: no cut pins it.</summary>
        public bool RingOnly => Volatile.Read(ref _references) == 1;

        public void AddRef() => Interlocked.Increment(ref _references);

        /// <summary>Drops one reference; whether that was the last, so the slab was freed.</summary>
        public bool Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
            {
                return false;
            }

            if (File is { } file)
            {
                file.Return(FileOffset);
            }
            else
            {
                NativeMemory.Free(Data);
                Data = null;
            }

            _owner.OnFreed();
            return true;
        }
    }
}
