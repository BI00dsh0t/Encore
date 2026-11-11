using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A read-only AVIO over a file shared for read, write and delete, so a read never blocks a rename or delete.</summary>
internal sealed unsafe class SharedInput : IDisposable
{
    // AVERROR_EOF.
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
            var read = (delegate* unmanaged[Cdecl]<void*, byte*, int, int>)&ReadPacket;
            var seek = (delegate* unmanaged[Cdecl]<void*, long, int, long>)&Seek;
            _context = FileAvio.Allocate(
                _handle,
                writable: false,
                new avio_alloc_context_read_packet_func { Pointer = (nint)read },
                default,
                new avio_alloc_context_seek_func { Pointer = (nint)seek });
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
        FileAvio.Free(ref _context, ref _handle);
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
            return FileAvio.Eio;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            return FileAvio.Seek(Of(opaque)._file, offset, whence);
        }
        catch (Exception)
        {
            return FileAvio.Eio;
        }
    }
}
