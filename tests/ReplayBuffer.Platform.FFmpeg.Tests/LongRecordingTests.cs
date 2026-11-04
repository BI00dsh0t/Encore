using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReplayBuffer.Platform.FFmpeg.Tests;

/// <summary>
/// FR-13: a Recording stays one file however long it runs. Four hours of synthetic packets (an
/// MPEG-4 Part 2 video stream at 30 fps, a keyframe every 2 s, and three AAC Tracks) go through a
/// <see cref="RecordingWriter"/> far faster than real time, its fragments not flushed to the disk,
/// and ffprobe reads the one file back. Needs the fetched FFmpeg.
/// </summary>
public sealed class LongRecordingTests : IDisposable
{
    internal const int Fps = 30;
    private const int Hours = 4;
    internal const int KeyEvery = 60;
    internal const int VideoTicks = 90_000 / Fps;
    internal const int AacSamples = 1024;
    internal const int SampleRate = 48_000;
    internal const int Tracks = 3;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-long-").FullName;

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
    public async Task FourHoursOfThreeTracksAreOneFileWithEveryTrackTheVideosLength()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")),
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");
        FFmpegLibrary.Initialize(NullLogger.Instance);
        var path = Path.Combine(_directory, "long.mp4");
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter(path, Streams(), () => 0, 0, NullLogger.Instance, e => done.TrySetResult(e)) { FlushToDisk = false };
        writer.Start();

        Feed(writer);
        writer.OutputEnded();

        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        Assert.Equal([path], Directory.GetFiles(_directory));

        var (format, durations) = Probe(path);
        var length = Hours * 3600.0;
        Assert.InRange(format, length - (1.0 / Fps), length + (1.0 / Fps));
        Assert.Equal(1 + Tracks, durations.Count);
        Assert.InRange(durations[0], length - (1.0 / Fps), length + (1.0 / Fps));
        foreach (var audio in durations.Skip(1))
        {
            Assert.InRange(audio, durations[0] - ((double)AacSamples / SampleRate), durations[0] + ((double)AacSamples / SampleRate));
        }
    }

    /// <summary>The video's parameters (MPEG-4 Part 2, 320x240, 90 kHz) and three AAC-LC 48 kHz stereo Tracks.</summary>
    internal static unsafe RecordingStreams Streams()
    {
        var streams = new RecordingStreams();
        var video = ffmpeg.avcodec_parameters_alloc();
        video->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        video->codec_id = AVCodecID.AV_CODEC_ID_MPEG4;
        video->width = 320;
        video->height = 240;
        video->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        Add(streams, video, null, true, new AVRational { num = 1, den = 90_000 });
        for (var i = 0; i < Tracks; i++)
        {
            var audio = ffmpeg.avcodec_parameters_alloc();
            audio->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
            audio->codec_id = AVCodecID.AV_CODEC_ID_AAC;
            audio->sample_rate = SampleRate;
            audio->frame_size = AacSamples;
            audio->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            ffmpeg.av_channel_layout_default(&audio->ch_layout, 2);

            // AudioSpecificConfig: AAC-LC (2), 48 kHz (index 3), 2 channels.
            audio->extradata = (byte*)ffmpeg.av_mallocz(2 + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            audio->extradata[0] = 0x11;
            audio->extradata[1] = 0x90;
            audio->extradata_size = 2;
            Add(streams, audio, $"Track {i + 1}", i == 0, new AVRational { num = 1, den = SampleRate });
        }

        return streams;
    }

    private static unsafe void Add(RecordingStreams streams, AVCodecParameters* parameters, string? title, bool isDefault, AVRational timeBase)
    {
        try
        {
            streams.Add(new ClipStream(parameters, title, isDefault, timeBase));
        }
        finally
        {
            ffmpeg.avcodec_parameters_free(&parameters);
        }
    }

    /// <summary>Every packet of the four hours in time order, as the encoders would hand them over, a few thousand queued at most.</summary>
    private static unsafe void Feed(RecordingWriter writer)
    {
        var frames = (long)Hours * 3600 * Fps;
        var audioPackets = (long)Hours * 3600 * SampleRate / AacSamples;
        long nextAudio = 0;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            FFmpegLibrary.Check(ffmpeg.av_new_packet(packet, 16), "av_new_packet");
            new Span<byte>(packet->data, 16).Clear(); // never an ADTS header by chance
            for (long frame = 0; frame < frames; frame++)
            {
                Offer(writer, packet, 0, frame * VideoTicks, VideoTicks, frame % KeyEvery == 0);

                // Each Track's packets that start before the next frame.
                var before = (frame + 1) * SampleRate / Fps;
                while (nextAudio < audioPackets && nextAudio * AacSamples < before)
                {
                    for (var track = 1; track <= Tracks; track++)
                    {
                        Offer(writer, packet, track, nextAudio * AacSamples, AacSamples, true);
                    }

                    nextAudio++;
                }

                while (writer.Queued > 4096)
                {
                    Thread.Yield();
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    internal static unsafe void Offer(RecordingWriter writer, AVPacket* packet, int stream, long pts, long duration, bool key)
    {
        packet->pts = pts;
        packet->dts = pts;
        packet->duration = duration;
        packet->flags = key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
        writer.Offer(packet, stream);
    }

    /// <summary>ffprobe's container duration and each stream's, in seconds.</summary>
    private static (double Format, List<double> Streams) Probe(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration:stream=duration", "-of", "json", path })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);

        using var json = JsonDocument.Parse(stdout.Result);
        var root = json.RootElement;
        var format = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var streams = root.GetProperty("streams").EnumerateArray()
            .Select(s => double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture))
            .ToList();
        return (format, streams);
    }
}
