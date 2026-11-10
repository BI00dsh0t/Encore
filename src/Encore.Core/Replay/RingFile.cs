using Microsoft.Win32.SafeHandles;

namespace Encore.Core.Replay;

/// <summary>The Disk-mode ring's file, preallocated in fixed blocks and deleted when its handle closes. docs/code-notes.md.</summary>
public sealed class RingFile
{
    public const string Prefix = "ring-";

    public const string Extension = ".bin";

    private static long s_next;

    private readonly SafeFileHandle _handle;
    private readonly Lock _lock = new();

    // The free blocks' offsets, oldest freed first; preallocated to hold every block.
    private readonly Queue<long> _free;
    private int _closed;

    private RingFile(string path, SafeFileHandle handle, int blocks, int blockBytes)
    {
        Path = path;
        _handle = handle;
        BlockBytes = blockBytes;
        Bytes = (long)blocks * blockBytes;
        _free = new Queue<long>(blocks);
        for (var i = 0; i < blocks; i++)
        {
            _free.Enqueue((long)i * blockBytes);
        }
    }

    /// <summary>The file's full path.</summary>
    public string Path { get; }

    /// <summary>Its preallocated size: the blocks times <see cref="BlockBytes"/>.</summary>
    public long Bytes { get; }

    /// <summary>One block's size, the slab size of the ring on it.</summary>
    public int BlockBytes { get; }

    /// <summary>Creates a new preallocated ring file of the whole blocks that fit in the given size; throws when it can't.</summary>
    public static RingFile Create(
        string folder,
        long bytes,
        int blockBytes = PacketRing.DefaultSlabBytes,
        Action<SafeFileHandle>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockBytes, 1);
        Directory.CreateDirectory(folder);
        var blocks = (int)Math.Clamp(bytes / blockBytes, 2, int.MaxValue);
        var path = System.IO.Path.Combine(folder, Prefix + Interlocked.Increment(ref s_next) + Extension);
        var handle = File.OpenHandle(
            path,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.DeleteOnClose,
            preallocationSize: (long)blocks * blockBytes);
        try
        {
            configure?.Invoke(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new RingFile(path, handle, blocks, blockBytes);
    }

    /// <summary>Deletes the ring files a killed engine left in the folder; others are skipped. Returns how many went.</summary>
    public static int RemoveStale(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, Prefix + "*" + Extension))
            {
                if (!IsRingName(System.IO.Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // In use by a live ring, or not ours to delete.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder itself can't be listed.
        }

        return removed;
    }

    /// <summary>Whether <paramref name="name"/> is one <see cref="Create"/> gives: <c>ring-</c>, digits, <c>.bin</c>.</summary>
    private static bool IsRingName(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = name.AsSpan(Prefix.Length, name.Length - Prefix.Length - Extension.Length);
        return !digits.IsEmpty && !digits.ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>Takes a free block; false when every block is in use.</summary>
    internal bool TryRent(out long offset)
    {
        lock (_lock)
        {
            return _free.TryDequeue(out offset);
        }
    }

    /// <summary>Gives a block back once no slab uses it.</summary>
    internal void Return(long offset)
    {
        lock (_lock)
        {
            _free.Enqueue(offset);
        }
    }

    internal void Write(long offset, ReadOnlySpan<byte> data) => RandomAccess.Write(_handle, data, offset);

    internal void Read(long offset, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = RandomAccess.Read(_handle, destination[read..], offset + read);
            if (count == 0)
            {
                throw new EndOfStreamException($"The ring file '{Path}' ended at {offset + read}.");
            }

            read += count;
        }
    }

    /// <summary>Closes the handle, which deletes the file; idempotent.</summary>
    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _handle.Dispose();
        try
        {
            File.Delete(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // DeleteOnClose has it, or it is not ours to delete.
        }
    }
}
