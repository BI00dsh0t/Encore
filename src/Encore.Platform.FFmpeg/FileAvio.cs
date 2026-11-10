using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>A custom AVIO over a managed file: its buffer and context, their release, and the seek they all share.</summary>
internal static unsafe class FileAvio
{
    /// <summary><c>AVERROR(EIO)</c>.</summary>
    public const int Eio = -5;

    private const int BufferSize = 64 * 1024;

    /// <summary>Allocates the buffer and the context over the owner's handle; on failure the buffer is freed and it throws.</summary>
    public static AVIOContext* Allocate(
        GCHandle owner,
        bool writable,
        avio_alloc_context_read_packet_func read,
        avio_alloc_context_write_packet_func write,
        avio_alloc_context_seek_func seek)
    {
        var buffer = (byte*)ffmpeg.av_malloc(BufferSize);
        if (buffer is null)
        {
            throw new InvalidOperationException("av_malloc failed.");
        }

        var context = ffmpeg.avio_alloc_context(
            buffer, BufferSize, writable ? 1 : 0, (void*)GCHandle.ToIntPtr(owner), read, write, seek);
        if (context is null)
        {
            ffmpeg.av_free(buffer);
            throw new InvalidOperationException("avio_alloc_context failed.");
        }

        return context;
    }

    /// <summary>Frees the context, then the owner's handle; both fields are cleared.</summary>
    public static void Free(ref AVIOContext* context, ref GCHandle owner)
    {
        if (context is not null)
        {
            // FFmpeg may have replaced the buffer it was given; free the one it holds.
            ffmpeg.av_freep(&context->buffer);
            var freed = context;
            ffmpeg.avio_context_free(&freed);
            context = null;
        }

        if (owner.IsAllocated)
        {
            owner.Free();
        }
    }

    /// <summary>Seeks a file as AVIO asks: <c>AVSEEK_SIZE</c> reads its length, and the C origins are SeekOrigin's values.</summary>
    public static long Seek(FileStream file, long offset, int whence)
    {
        whence &= ~ffmpeg.AVSEEK_FORCE;
        if (whence == ffmpeg.AVSEEK_SIZE)
        {
            return file.Length;
        }

        return whence is >= 0 and <= 2 ? file.Seek(offset, (SeekOrigin)whence) : Eio;
    }
}
