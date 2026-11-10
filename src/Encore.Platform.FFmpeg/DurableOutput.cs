using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>
/// A custom AVIO over a <see cref="FileStream"/> it owns (R3, AD-16): FFmpeg writes into a 64 KB
/// buffer, and <see cref="Flush"/> pushes that buffer through to the file and the file to the disk
/// (<c>FlushFileBuffers</c>). A write or seek that fails returns <c>AVERROR(EIO)</c> to FFmpeg and
/// keeps the first exception, which <see cref="ThrowIfFailed"/> rethrows as it was. Every call comes
/// from one thread, the writer's.
/// </summary>
internal sealed unsafe class DurableOutput : IDisposable
{
    private const int BufferSize = 64 * 1024;

    // AVERROR(EIO).
    private const int Eio = -5;

    private readonly FileStream _file;
    private readonly bool _flushToDisk;
    private readonly long? _capacity;
    private GCHandle _handle;
    private AVIOContext* _context;
    private Exception? _failure;

    // The bytes written before the first flush, until it seals them.
    private MemoryStream? _header = new();
    private byte[] _sealedHeader = [];

    /// <summary>Creates (or truncates) <paramref name="path"/>, shared for reading only.</summary>
    /// <param name="path">The file.</param>
    /// <param name="flushToDisk">Whether <see cref="Flush"/> also flushes the file to the disk; false only for tests.</param>
    /// <param name="capacity">
    /// A test seam: the file's room in bytes, past which a write fills it up to that and fails as a
    /// full disk does (<c>ERROR_DISK_FULL</c>); null for none.
    /// </param>
    public DurableOutput(string path, bool flushToDisk = true, long? capacity = null)
    {
        _flushToDisk = flushToDisk;
        _capacity = capacity;
        // Unbuffered: FFmpeg's own buffer is the only one, so a flush reaches the OS at once.
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);
        try
        {
            _handle = GCHandle.Alloc(this);
            var buffer = (byte*)ffmpeg.av_malloc(BufferSize);
            if (buffer is null)
            {
                throw new InvalidOperationException("av_malloc failed.");
            }

            _context = ffmpeg.avio_alloc_context(
                buffer,
                BufferSize,
                1,
                (void*)GCHandle.ToIntPtr(_handle),
                default,
                new avio_alloc_context_write_packet_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, byte*, int, int>)&WritePacket },
                new avio_alloc_context_seek_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, long, int, long>)&Seek });
            if (_context is null)
            {
                ffmpeg.av_free(buffer);
                throw new InvalidOperationException("avio_alloc_context failed.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The AVIO to set as the format context's <c>pb</c>.</summary>
    public AVIOContext* Context => _context;

    /// <summary>The bytes that have reached the file so far.</summary>
    public long Length => _file.Length;

    /// <summary>
    /// A copy of the bytes written before the first <see cref="Flush"/> (the muxer's header: <c>ftyp</c>
    /// and <c>moov</c>), from file offset 0; empty before that flush.
    /// </summary>
    public ReadOnlySpan<byte> Header => _header is null ? _sealedHeader : [];

    /// <summary>Rethrows the first write or seek failure, if any, as it was thrown.</summary>
    public void ThrowIfFailed()
    {
        if (_failure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Writes FFmpeg's buffer to the file, then the file to the disk.</summary>
    public void Flush()
    {
        ffmpeg.avio_flush(_context);
        ThrowIfFailed();
        FFmpegLibrary.Check(_context->error, "avio_flush");
        _file.Flush(_flushToDisk);
        if (_header is not null)
        {
            _sealedHeader = _header.ToArray();
            _header = null;
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> over the file at <paramref name="offset"/> in one write, flushes
    /// the file to the disk, and goes back to the end. Only after a <see cref="Flush"/> that left
    /// FFmpeg's buffer empty, as the trailer's does.
    /// </summary>
    /// <exception cref="IOException">The write or the flush failed.</exception>
    public void Overwrite(long offset, ReadOnlySpan<byte> bytes)
    {
        _file.Seek(offset, SeekOrigin.Begin);
        try
        {
            _file.Write(bytes);
            _file.Flush(_flushToDisk);
        }
        finally
        {
            _file.Seek(0, SeekOrigin.End);
        }
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            // FFmpeg may have replaced the buffer it was given; free the one it holds.
            ffmpeg.av_freep(&_context->buffer);
            var context = _context;
            ffmpeg.avio_context_free(&context);
            _context = null;
        }

        if (_handle.IsAllocated)
        {
            _handle.Free();
        }

        _file.Dispose();
    }

    private static DurableOutput Of(void* opaque) => (DurableOutput)GCHandle.FromIntPtr((nint)opaque).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WritePacket(void* opaque, byte* buffer, int size)
    {
        var output = Of(opaque);
        if (output._failure is not null)
        {
            return Eio;
        }

        try
        {
            var bytes = new ReadOnlySpan<byte>(buffer, size);
            if (output._capacity is { } capacity && output._file.Position + size > capacity)
            {
                output._file.Write(bytes[..(int)Math.Max(0, capacity - output._file.Position)]);
                throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
            }

            if (output._header is { } header)
            {
                header.Position = output._file.Position;
                header.Write(bytes);
            }

            output._file.Write(bytes);
            return size;
        }
        catch (Exception e)
        {
            output._failure = e;
            return Eio;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long offset, int whence)
    {
        var output = Of(opaque);
        if (output._failure is not null)
        {
            return Eio;
        }

        try
        {
            whence &= ~ffmpeg.AVSEEK_FORCE;
            if (whence == ffmpeg.AVSEEK_SIZE)
            {
                return output._file.Length;
            }

            // SEEK_SET, SEEK_CUR and SEEK_END are SeekOrigin's values.
            return whence is >= 0 and <= 2 ? output._file.Seek(offset, (SeekOrigin)whence) : Eio;
        }
        catch (Exception e)
        {
            output._failure = e;
            return Eio;
        }
    }
}
