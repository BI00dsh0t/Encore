using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A read-only AVIO over a file shared for read, write and delete, so a read never blocks a rename or delete.</summary>
internal sealed unsafe class SharedInput : IDisposable
{
    private const int BufferSize = 64 * 1024;

    // AVERROR(EIO) and AVERROR_EOF.
    private const int Eio = -5;
    private const int Eof = -541478725;

    private readonly FileStream _file;
    private GCHandle _handle;
    private AVIOContext* _context;

    /// <summary>Opens the file; throws IOException when it can't be opened.</summary>
    public SharedInput(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
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
                0,
                (void*)GCHandle.ToIntPtr(_handle),
                new avio_alloc_context_read_packet_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, byte*, int, int>)&ReadPacket },
                default,
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

    /// <summary>The AVIO to set as the format context's <c>pb</c>, with <c>AVFMT_FLAG_CUSTOM_IO</c>.</summary>
    public AVIOContext* Context => _context;

    /// <summary>Frees the AVIO; call after <c>avformat_close_input</c>, which leaves a custom one alone.</summary>
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

    private static SharedInput Of(void* opaque) => (SharedInput)GCHandle.FromIntPtr((nint)opaque).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadPacket(void* opaque, byte* buffer, int size)
    {
        try
        {
            var read = Of(opaque)._file.Read(new Span<byte>(buffer, size));
            return read > 0 ? read : Eof;
        }
        catch (Exception)
        {
            return Eio;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            var file = Of(opaque)._file;
            whence &= ~ffmpeg.AVSEEK_FORCE;
            if (whence == ffmpeg.AVSEEK_SIZE)
            {
                return file.Length;
            }

            // SEEK_SET, SEEK_CUR and SEEK_END are SeekOrigin's values.
            return whence is >= 0 and <= 2 ? file.Seek(offset, (SeekOrigin)whence) : Eio;
        }
        catch (Exception)
        {
            return Eio;
        }
    }
}
