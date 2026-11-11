using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace Encore.Platform.FFmpeg;

/// <summary>Writes a Clip as a complete MP4, to a part file that is then renamed over the final path. docs/code-notes.md.</summary>
public static unsafe class ClipWriter
{
    /// <summary>What a Clip is written to before its rename.</summary>
    public const string PartExtension = ".rbpart";

    /// <summary>Writes the source to the final path; on failure both files are deleted and the failure is rethrown.</summary>
    public static void Write(ClipSource source, string finalPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(finalPath);
        ArgumentNullException.ThrowIfNull(logger);

        var part = finalPath + PartExtension;
        var background = BackgroundMode.Enter(logger);
        try
        {
            WritePart(source, part);
            File.Move(part, finalPath, overwrite: true);
        }
        catch
        {
            OutputFiles.TryDelete(part, logger);
            OutputFiles.TryDelete(finalPath, logger);
            throw;
        }
        finally
        {
            if (background)
            {
                BackgroundMode.Leave();
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
            foreach (var stream in source.Streams)
            {
                AddStream(context, stream);
            }

            FFmpegLibrary.Check(ffmpeg.avio_open(&context->pb, part, ffmpeg.AVIO_FLAG_WRITE), $"avio_open({part})");
            WriteHeader(context);
            packet = ffmpeg.av_packet_alloc();
            if (packet is null)
            {
                throw new InvalidOperationException("av_packet_alloc failed.");
            }

            WritePackets(context, packet, source);
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
                CloseOutput(context);
            }
        }
    }

    private static void AddStream(AVFormatContext* context, ClipStream source)
    {
        var stream = Mp4Streams.New(context);
        FFmpegLibrary.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, source.Parameters), "avcodec_parameters_copy");

        // Only HEVC is tagged; any other codec takes the muxer's own tag.
        stream->codecpar->codec_tag = 0;
        Mp4Streams.Describe(stream, source.TimeBase, source.Title, source.IsDefault);
    }

    private static void WriteHeader(AVFormatContext* context)
    {
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
    }

    /// <summary>Copies each of the cut's packets into the one reused packet, rebased to the cut, and writes it.</summary>
    private static void WritePackets(AVFormatContext* context, AVPacket* packet, ClipSource source)
    {
        var streams = source.Streams;
        var cut = source.Cut;
        foreach (var ring in cut.Packets)
        {
            FFmpegLibrary.Check(ffmpeg.av_new_packet(packet, ring.Length), "av_new_packet");
            ring.CopyTo(new Span<byte>(packet->data, ring.Length));

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
    }

    private static void CloseOutput(AVFormatContext* context)
    {
        if (context->pb is not null)
        {
            ffmpeg.avio_closep(&context->pb);
        }

        ffmpeg.avformat_free_context(context);
    }
}
