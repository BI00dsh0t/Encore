using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// Reads a media file's length with FFmpeg's demuxer (story 7.3): <c>avformat_open_input</c> and
/// <c>avformat_find_stream_info</c>, with no decode. A fragmented MP4 cut mid-fragment reads as the end
/// of its last complete fragment. The calling thread runs in background I/O mode for the read (AD-7).
/// </summary>
public static unsafe class MediaLength
{
    /// <summary>
    /// The length of <paramref name="path"/> in seconds: the container's duration when it has one, else
    /// its longest stream's. Null, with a warning naming the file, when FFmpeg can't read it.
    /// </summary>
    public static double? Read(string path, ILogger logger) => Read(path, logger, whileOpen: null);

    /// <summary>
    /// <see cref="Read(string, ILogger)"/>, with a test seam: <paramref name="whileOpen"/> runs once the
    /// file is open for the read, as a rename or delete from the user may land.
    /// </summary>
    internal static double? Read(string path, ILogger logger, Action? whileOpen)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            FFmpegLibrary.Initialize(logger);
        }
        catch (ErrorCodeException e)
        {
            logger.LogWarning("The length of {Path} couldn't be read: {Message}", path, e.Message);
            return null;
        }

        var background = PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_BEGIN);
        AVFormatContext* context = null;
        SharedInput? input = null;
        try
        {
            // Through a shared handle, not FFmpeg's file protocol: the read mustn't block a rename or delete.
            try
            {
                input = new SharedInput(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The length of {Path} couldn't be read: {Message}", path, e.Message);
                return null;
            }

            context = ffmpeg.avformat_alloc_context();
            context->pb = input.Context;
            context->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            // On failure it frees the context and sets it to null.
            var result = ffmpeg.avformat_open_input(&context, path, null, null);
            if (result < 0)
            {
                logger.LogWarning("The length of {Path} couldn't be read: avformat_open_input failed: {Error}", path, FFmpegLibrary.ErrorText(result));
                return null;
            }

            whileOpen?.Invoke();
            result = ffmpeg.avformat_find_stream_info(context, null);
            if (result < 0)
            {
                logger.LogWarning("The length of {Path} couldn't be read: avformat_find_stream_info failed: {Error}", path, FFmpegLibrary.ErrorText(result));
                return null;
            }

            if (Length(context) is { } length)
            {
                return length;
            }

            logger.LogWarning("The length of {Path} couldn't be read: it has no duration", path);
            return null;
        }
        finally
        {
            if (context is not null)
            {
                ffmpeg.avformat_close_input(&context);
            }

            input?.Dispose();

            if (background)
            {
                PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_END);
            }
        }
    }

    private static double? Length(AVFormatContext* context)
    {
        if (context->duration != ffmpeg.AV_NOPTS_VALUE && context->duration > 0)
        {
            return context->duration / (double)ffmpeg.AV_TIME_BASE;
        }

        double? longest = null;
        for (var i = 0; i < context->nb_streams; i++)
        {
            var stream = context->streams[i];
            if (stream->duration == ffmpeg.AV_NOPTS_VALUE || stream->duration <= 0 || stream->time_base.den == 0)
            {
                continue;
            }

            var seconds = stream->duration * stream->time_base.num / (double)stream->time_base.den;
            if (longest is null || seconds > longest)
            {
                longest = seconds;
            }
        }

        return longest;
    }
}
