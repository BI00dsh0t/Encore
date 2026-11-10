using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A custom AVIO over a file it owns; Flush pushes FFmpeg's buffer to the file and the file to the disk.</summary>
internal sealed unsafe class DurableOutput : IDisposable
{
    private const int Eio = FileAvio.Eio;

    private readonly FileStream _file;
    private readonly bool _flushToDisk;
    private readonly long? _capacity;
    private GCHandle _handle;
    private AVIOContext* _context;
    private Exception? _failure;

    // The bytes written before the first flush, until it seals them.
    private MemoryStream? _header = new();
    private byte[] _sealedHeader = [];

    /// <summary>Creates or truncates the file, shared for reading only; capacity is a test seam that fails like a full disk.</summary>
    public DurableOutput(string path, bool flushToDisk = true, long? capacity = null)
    {
        _flushToDisk = flushToDisk;
        _capacity = capacity;
        // Unbuffered: FFmpeg's own buffer is the only one, so a flush reaches the OS at once.
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);
        try
        {
            _handle = GCHandle.Alloc(this);
            var write = (delegate* unmanaged[Cdecl]<void*, byte*, int, int>)&WritePacket;
            var seek = (delegate* unmanaged[Cdecl]<void*, long, int, long>)&Seek;
            _context = FileAvio.Allocate(
                _handle,
                writable: true,
                default,
                new avio_alloc_context_write_packet_func { Pointer = (nint)write },
                new avio_alloc_context_seek_func { Pointer = (nint)seek });
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

    /// <summary>A copy of the bytes written before the first <see cref="Flush"/> (the muxer's header); empty before that.</summary>
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

    /// <summary>Overwrites bytes at an offset, flushes to the disk and returns to the end; only while FFmpeg's buffer is empty.</summary>
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
        FileAvio.Free(ref _context, ref _handle);
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
            return FileAvio.Seek(output._file, offset, whence);
        }
        catch (Exception e)
        {
            output._failure = e;
            return Eio;
        }
    }
}
