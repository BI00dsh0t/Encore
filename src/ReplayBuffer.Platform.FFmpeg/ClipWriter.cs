using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace ReplayBuffer.Platform.FFmpeg;

/// <summary>
/// Writes a Clip (AD-9): a complete MP4 with the index first (<c>movflags +faststart</c>), written to
/// <c>&lt;final&gt;.rbpart</c> beside the final path and then renamed over it, the empty file the
/// allocator reserved (AD-21). Each stream is rebased to the Clip's first keyframe in its own time
/// base, and the packets go through <c>av_interleaved_write_frame</c> in time order. The calling thread
/// runs in background I/O mode for the write (AD-7).
/// </summary>
public static unsafe class ClipWriter
{
    /// <summary>What a Clip is written to before its rename.</summary>
    public const string PartExtension = ".rbpart";

    // MKTAG('h', 'v', 'c', '1'), as the fMP4 muxer tags HEVC.
    private const uint HevcTag = 'h' | ('v' << 8) | ('c' << 16) | ((uint)'1' << 24);

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="finalPath"/>. On failure both the
    /// <c>.rbpart</c> and <paramref name="finalPath"/> are deleted, and the failure is rethrown.
    /// </summary>
    public static void Write(ClipSource source, string finalPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(finalPath);
        ArgumentNullException.ThrowIfNull(logger);

        var part = finalPath + PartExtension;
        var background = EnterBackgroundMode(logger);
        try
        {
            WritePart(source, part);
            File.Move(part, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(part, logger);
            TryDelete(finalPath, logger);
            throw;
        }
        finally
        {
            if (background)
            {
                PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_END);
            }
        }
    }

    private static void WritePart(ClipSource source, string part)
    {
        AVFormatContext* context = null;
        AVPacket* packet = null;
        try
        {
            FFmpegLibrary.Check(ffmpeg.avformat_alloc_output_context2(&context, null, "mp4", part), "avformat_alloc_output_context2");
            var streams = source.Streams;
            for (var i = 0; i < streams.Count; i++)
            {
                AddStream(context, streams[i]);
            }

            FFmpegLibrary.Check(ffmpeg.avio_open(&context->pb, part, ffmpeg.AVIO_FLAG_WRITE), $"avio_open({part})");
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            try
            {
                FFmpegLibrary.Check(ffmpeg.avformat_write_header(context, &options), "avformat_write_header");
            }
            finally
            {
                ffmpeg.av_dict_free(&options);
            }

            packet = ffmpeg.av_packet_alloc();
            if (packet is null)
            {
                throw new InvalidOperationException("av_packet_alloc failed.");
            }

            var cut = source.Cut;
            foreach (var ring in cut.Packets)
            {
                var bytes = ring.Bytes;
                FFmpegLibrary.Check(ffmpeg.av_new_packet(packet, bytes.Length), "av_new_packet");
                bytes.CopyTo(new Span<byte>(packet->data, bytes.Length));

                var origin = cut.Origin(ring.Stream);
                packet->stream_index = ring.Stream;
                packet->pts = ring.Pts - origin;
                packet->dts = ring.Dts - origin;
                packet->duration = ring.Duration;
                packet->flags = ring.IsKey ? ffmpeg.AV_PKT_FLAG_KEY : 0;
                ffmpeg.av_packet_rescale_ts(packet, streams[ring.Stream].TimeBase, context->streams[ring.Stream]->time_base);

                // Takes the packet's reference whether it succeeds or not.
                FFmpegLibrary.Check(ffmpeg.av_interleaved_write_frame(context, packet), "av_interleaved_write_frame");
            }

            FFmpegLibrary.Check(ffmpeg.av_interleaved_write_frame(context, null), "av_interleaved_write_frame(flush)");
            FFmpegLibrary.Check(ffmpeg.av_write_trailer(context), "av_write_trailer");
            FFmpegLibrary.Check(ffmpeg.avio_closep(&context->pb), "avio_closep");
        }
        finally
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (context is not null)
            {
                if (context->pb is not null)
                {
                    ffmpeg.avio_closep(&context->pb);
                }

                ffmpeg.avformat_free_context(context);
            }
        }
    }

    private static void AddStream(AVFormatContext* context, ClipStream source)
    {
        var stream = ffmpeg.avformat_new_stream(context, null);
        if (stream is null)
        {
            throw new InvalidOperationException("avformat_new_stream failed.");
        }

        FFmpegLibrary.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, source.Parameters), "avcodec_parameters_copy");
        stream->codecpar->codec_tag = source.Parameters->codec_id == AVCodecID.AV_CODEC_ID_HEVC ? HevcTag : 0;
        stream->time_base = source.TimeBase;
        stream->disposition = source.IsDefault ? ffmpeg.AV_DISPOSITION_DEFAULT : 0;
        if (source.Title is not null)
        {
            ffmpeg.av_dict_set(&stream->metadata, "title", source.Title, 0);
            ffmpeg.av_dict_set(&stream->metadata, "handler_name", source.Title, 0);
        }
    }

    /// <summary>Background I/O and memory priority for the calling thread (AD-7); false when Windows refused it.</summary>
    internal static bool EnterBackgroundMode(ILogger logger)
    {
        if (PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_MODE_BACKGROUND_BEGIN))
        {
            return true;
        }

        logger.LogDebug("The Clip writer could not enter background mode; writing at normal priority");
        return false;
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete {Path}: {Message}", path, e.Message);
        }
    }
}
