using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Core.Replay;
using static Encore.Platform.FFmpeg.Tests.LongRecordingTests;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>
/// A Clip's first frame is on the frame grid, as <see cref="RecordingStartTests"/> checks for a
/// Recording: the Clip is cut at the keyframe at 6 s, where each AAC Track has a packet that starts
/// 256 samples before it and is centred after it. A Clip is a plain MP4 with an edit list, so that
/// packet's lead is signalled there and the video is not stretched.
/// </summary>
public sealed class ClipStartTests : IDisposable
{
    private const int KeyFrame = 180;
    private const int Seconds = 8;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-clip-start-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AnAudioPacketStartingBeforeTheKeyLeavesTheClipsFirstFrameOnTheGrid()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")),
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");
        FFmpegLibrary.Initialize(NullLogger.Instance);
        var path = Path.Combine(_directory, "clip.mp4");
        using (var sink = new ReplayRingSink(new PacketRing(30_000_000, 16_384)))
        {
            AddStreams(sink);
            Feed(sink);
            using var source = sink.Cut(KeyFrame * 1_000_000L / Fps, long.MaxValue);
            Assert.NotNull(source);
            ClipWriter.Write(source, path, NullLogger.Instance);
        }

        var pts = RecordingStartTests.Packets(path, "v:0").Select(p => p.Pts).ToList();
        Assert.Equal((Seconds * Fps) - KeyFrame, pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            Assert.True(pts[i] == pts[0] + (i * VideoTicks), $"video packet {i} at {pts[i]}, not {pts[0] + (i * VideoTicks)}");
        }

        // The packet that starts before the key is in the Clip, 256 samples before the video.
        var audio = RecordingStartTests.Packets(path, "a:0");
        Assert.Equal(pts[0] * SampleRate / 90_000 - 256, audio[0].Pts);
        for (var i = 1; i < audio.Count; i++)
        {
            Assert.True(audio[i].Pts == audio[i - 1].Pts + audio[i - 1].Duration, $"audio packet {i} at {audio[i].Pts}");
        }
    }

    /// <summary>The streams of <see cref="LongRecordingTests.Streams"/>, added as unopened encoder contexts.</summary>
    private static unsafe void AddStreams(ReplayRingSink sink)
    {
        var video = ffmpeg.avcodec_alloc_context3(null);
        try
        {
            video->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            video->codec_id = AVCodecID.AV_CODEC_ID_MPEG4;
            video->width = 320;
            video->height = 240;
            video->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            video->time_base = new AVRational { num = 1, den = 90_000 };
            sink.AddStream(video, null, true);
        }
        finally
        {
            ffmpeg.avcodec_free_context(&video);
        }

        for (var i = 0; i < Tracks; i++)
        {
            var audio = ffmpeg.avcodec_alloc_context3(null);
            try
            {
                audio->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
                audio->codec_id = AVCodecID.AV_CODEC_ID_AAC;
                audio->sample_rate = SampleRate;
                audio->frame_size = AacSamples;
                audio->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                audio->time_base = new AVRational { num = 1, den = SampleRate };
                ffmpeg.av_channel_layout_default(&audio->ch_layout, 2);
                audio->extradata = (byte*)ffmpeg.av_mallocz(2 + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
                audio->extradata[0] = 0x11;
                audio->extradata[1] = 0x90;
                audio->extradata_size = 2;
                sink.AddStream(audio, $"Track {i + 1}", i == 0);
            }
            finally
            {
                ffmpeg.avcodec_free_context(&audio);
            }
        }
    }

    private static unsafe void Feed(ReplayRingSink sink)
    {
        long nextAudio = 0;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            for (long frame = 0; frame < Seconds * Fps; frame++)
            {
                Write(sink, packet, 0, frame * VideoTicks, VideoTicks, frame % KeyEvery == 0, 90_000);
                var before = (frame + 1) * SampleRate / Fps;
                while (nextAudio * AacSamples < before)
                {
                    for (var track = 1; track <= Tracks; track++)
                    {
                        Write(sink, packet, track, nextAudio * AacSamples, AacSamples, true, SampleRate);
                    }

                    nextAudio++;
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    private static unsafe void Write(ReplayRingSink sink, AVPacket* packet, int stream, long pts, long duration, bool key, int timeBaseDen)
    {
        FFmpegLibrary.Check(ffmpeg.av_new_packet(packet, 16), "av_new_packet");
        new Span<byte>(packet->data, 16).Clear(); // never an ADTS header by chance
        packet->pts = pts;
        packet->dts = pts;
        packet->duration = duration;
        packet->flags = key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
        sink.Write(packet, stream, new AVRational { num = 1, den = timeBaseDen });
    }
}
