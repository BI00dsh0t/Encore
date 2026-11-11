using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Encore.Contracts;

namespace Encore.Platform.FFmpeg;

/// <summary>Reads a media file's length with FFmpeg's demuxer, without decoding; a cut-off file ends at its last whole fragment.</summary>
public static unsafe class MediaLength
{
    /// <summary>The file's length in seconds: the container's when it has one, else its longest stream's; null when unreadable.</summary>
    public static double? Read(string path, ILogger logger) => Read(path, logger, whileOpen: null);

    /// <summary>The same read with a test seam: whileOpen runs once the file is open, when a rename or delete may land.</summary>
    internal static double? Read(string path, ILogger logger, Action? whileOpen)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(logger);
        if (!TryInitialize(path, logger))
        {
            return null;
        }

        var background = BackgroundMode.TryEnter();
        AVFormatContext* context = null;
        SharedInput? input = null;
        try
        {
            input = TryOpenShared(path, logger);
            if (input is null || !TryOpenInput(&context, input, path, logger))
            {
                return null;
            }

            whileOpen?.Invoke();
            return ReadLength(context, path, logger);
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
                BackgroundMode.Leave();
            }
        }
    }

    private static bool TryInitialize(string path, ILogger logger)
    {
        try
        {
            FFmpegLibrary.Initialize(logger);
            return true;
        }
        catch (ErrorCodeException e)
        {
            logger.LogWarning("The length of {Path} couldn't be read: {Message}", path, e.Message);
            return false;
        }
    }

    // Through a shared handle, not FFmpeg's file protocol: the read mustn't block a rename or delete.
    private static SharedInput? TryOpenShared(string path, ILogger logger)
    {
        try
        {
            return new SharedInput(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The length of {Path} couldn't be read: {Message}", path, e.Message);
            return null;
        }
    }

    /// <summary>Opens the demuxer over the shared input into the caller's context; a failed open frees it and nulls it.</summary>
    private static bool TryOpenInput(AVFormatContext** context, SharedInput input, string path, ILogger logger)
    {
        *context = ffmpeg.avformat_alloc_context();
        (*context)->pb = input.Context;
        (*context)->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
        var result = ffmpeg.avformat_open_input(context, path, null, null);
        if (result >= 0)
        {
            return true;
        }

        logger.LogWarning(
            "The length of {Path} couldn't be read: avformat_open_input failed: {Error}", path, FFmpegLibrary.ErrorText(result));
        return false;
    }

    private static double? ReadLength(AVFormatContext* context, string path, ILogger logger)
    {
        var result = ffmpeg.avformat_find_stream_info(context, null);
        if (result < 0)
        {
            logger.LogWarning(
                "The length of {Path} couldn't be read: avformat_find_stream_info failed: {Error}", path, FFmpegLibrary.ErrorText(result));
            return null;
        }

        if (Length(context) is { } length)
        {
            return length;
        }

        logger.LogWarning("The length of {Path} couldn't be read: it has no duration", path);
        return null;
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
