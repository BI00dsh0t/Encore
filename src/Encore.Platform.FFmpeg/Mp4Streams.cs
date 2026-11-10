using FFmpeg.AutoGen.Abstractions;

namespace Encore.Platform.FFmpeg;

/// <summary>Adding a stream to an MP4 output: the same tag, time base, disposition and title for Clips and Recordings.</summary>
internal static unsafe class Mp4Streams
{
    // MKTAG('h', 'v', 'c', '1').
    private const uint HevcTag = 'h' | ('v' << 8) | ('c' << 16) | ((uint)'1' << 24);

    /// <summary>A new stream on the output; throws when FFmpeg couldn't add one.</summary>
    public static AVStream* New(AVFormatContext* context)
    {
        var stream = ffmpeg.avformat_new_stream(context, null);
        if (stream is null)
        {
            throw new InvalidOperationException("avformat_new_stream failed.");
        }

        return stream;
    }

    /// <summary>Tags, times and names a stream whose parameters are set; returns its index.</summary>
    public static int Describe(AVStream* stream, AVRational timeBase, string? title, bool isDefault)
    {
        if (stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
        {
            // hvc1 (parameter sets in the sample entry) rather than mp4's default hev1: Apple players need it.
            stream->codecpar->codec_tag = HevcTag;
        }

        stream->time_base = timeBase;
        stream->disposition = isDefault ? ffmpeg.AV_DISPOSITION_DEFAULT : 0;
        if (title is not null)
        {
            ffmpeg.av_dict_set(&stream->metadata, "title", title, 0);
            ffmpeg.av_dict_set(&stream->metadata, "handler_name", title, 0);
        }

        return stream->index;
    }
}
