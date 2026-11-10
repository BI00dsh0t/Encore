using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Media;
using Encore.Platform.Capture;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>Encodes 3 s of synthetic video and audio through the real stack and checks the file with ffprobe. Needs NVENC.</summary>
public sealed class SyntheticEncodeTests : IDisposable
{
    private const int Fps = 60;
    private const int Seconds = 3;
    private const int PacketFrames = 480; // 10 ms
    private const double EventSeconds = 1.0;
    private const double OneFrame = 1.0 / Fps;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rb-synthetic-" + Guid.NewGuid().ToString("N"));

    public SyntheticEncodeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("RB_KEEP_TEST_OUTPUT") == "1")
        {
            return;
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SyntheticCaptureEncodesToAlignedFragmentedMp4()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0,
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");

        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);

        var path = Path.Combine(_directory, "synthetic.mp4");
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        var latency = new LatencyHistogram(qpf);
        long encoded, flushed;

        using (var device = D3D11Device.Create(0, log))
        using (var muxer = new FragmentedMp4Muxer(path, log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, 1280, 720, VideoEncoder.PoolSize))
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log, latency))
        using (var output = new AacEncoder(muxer, "Synthetic Output", isDefault: true, log))
        using (var input = new AacEncoder(muxer, "Synthetic Input", isDefault: false, log))
        {
            muxer.Start();
            var outputTimeline = new AudioTimeline(qpf, anchor, output);
            var inputTimeline = new AudioTimeline(qpf, anchor, input);
            var packet = new float[PacketFrames * AudioTimeline.Channels];
            long audioFrames = 0;

            for (var i = 0; i < Fps * Seconds; i++)
            {
                var qpc = anchor + (i * qpf / Fps);
                using (var surface = video.TryAcquire(qpc))
                {
                    Assert.NotNull(surface);
                    var level = i == Fps * EventSeconds ? 1f : 0f;
                    device.Clear(surface.Texture, surface.Subresource, level, level, level);
                    video.Submit(surface);
                }

                // Audio up to the next frame, in 10 ms packets stamped like WASAPI's.
                var until = (long)((i + 1) * (double)AudioTimeline.SampleRate / Fps);
                while (audioFrames < until)
                {
                    var stamp = anchor + (audioFrames * qpf / AudioTimeline.SampleRate);
                    Fill(packet, audioFrames, 440);
                    outputTimeline.Push(stamp, PacketFrames, packet, AudioPacketFlags.None);
                    Fill(packet, audioFrames, 1000);
                    inputTimeline.Push(stamp, PacketFrames, packet, AudioPacketFlags.None);
                    audioFrames += PacketFrames;
                }
            }

            video.Flush();
            output.Flush();
            input.Flush();
            muxer.Finish();
            encoded = video.EncodedFrames;
            flushed = video.FlushedPackets;
        }

        // Every encoded packet has one latency sample; none is drained at flush, as NVENC returns each packet at once.
        Assert.Equal(Fps * Seconds, encoded);
        Assert.Equal(0, flushed);
        Assert.Equal(encoded, latency.Count + flushed);

        using var probe = JsonDocument.Parse(Ffprobe("-v error -show_streams -show_format -of json synthetic.mp4"));
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToList();
        Assert.Equal(3, streams.Count);

        var videoStream = Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "video");
        Assert.Equal("h264", videoStream.GetProperty("codec_name").GetString());

        var audioStreams = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToList();
        Assert.Equal(2, audioStreams.Count);
        Assert.All(audioStreams, s =>
        {
            Assert.Equal("aac", s.GetProperty("codec_name").GetString());
            Assert.Equal("48000", s.GetProperty("sample_rate").GetString());
        });
        Assert.Equal("Synthetic Output", Title(audioStreams[0]));
        Assert.Equal("Synthetic Input", Title(audioStreams[1]));
        Assert.Equal(1, audioStreams[0].GetProperty("disposition").GetProperty("default").GetInt32());
        Assert.Equal(0, audioStreams[1].GetProperty("disposition").GetProperty("default").GetInt32());

        Assert.All(streams, s => Assert.InRange(ParseSeconds(s.GetProperty("start_time").GetString()), -OneFrame, OneFrame));
        Assert.True(CountTopLevelBoxes(path, "moof") >= 3, "expected at least 3 fragments");

        var flash = FlashTime();
        Assert.InRange(flash, EventSeconds - OneFrame, EventSeconds + OneFrame);
        foreach (var index in new[] { audioStreams[0], audioStreams[1] }.Select(s => s.GetProperty("index").GetInt32()))
        {
            var click = ClickTime(index);
            Assert.InRange(click, EventSeconds - OneFrame, EventSeconds + OneFrame);
            Assert.InRange(click - flash, -OneFrame, OneFrame);
        }
    }

    /// <summary>A quiet tone, with a near-full-scale 1 ms click at <see cref="EventSeconds"/>.</summary>
    private static void Fill(float[] packet, long firstFrame, double hz)
    {
        var clickStart = (long)(EventSeconds * AudioTimeline.SampleRate);
        for (var i = 0; i < PacketFrames; i++)
        {
            var n = firstFrame + i;
            var sample = n >= clickStart && n < clickStart + 48
                ? 0.9f
                : (float)(0.1 * Math.Sin(2 * Math.PI * hz * n / AudioTimeline.SampleRate));
            packet[2 * i] = sample;
            packet[(2 * i) + 1] = sample;
        }
    }

    private double FlashTime()
    {
        var csv = Ffprobe("-v error -f lavfi -i movie=synthetic.mp4,signalstats "
            + "-show_entries frame=pts_time:frame_tags=lavfi.signalstats.YAVG -of csv=p=0");
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && ParseSeconds(parts[1]) > 128)
            {
                return ParseSeconds(parts[0]);
            }
        }

        throw new InvalidOperationException("No flash frame found:\n" + csv);
    }

    private double ClickTime(int streamIndex)
    {
        var csv = Ffprobe($"-v error -f lavfi -i amovie=synthetic.mp4:si={streamIndex},asetnsamples=n=48:p=0,astats=metadata=1:reset=1 "
            + "-show_entries frame=pts_time:frame_tags=lavfi.astats.Overall.Peak_level -of csv=p=0");
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var peak) && peak > -6)
            {
                return ParseSeconds(parts[0]);
            }
        }

        throw new InvalidOperationException($"No click found in stream {streamIndex}:\n" + csv);
    }

    private string Ffprobe(string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"), arguments)
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ffprobe {arguments} failed: {stderr}");
        return stdout.Result;
    }

    /// <summary>The stream title as FFmpeg reads it back, from the track's <c>name</c> box.</summary>
    private static string? Title(JsonElement stream)
    {
        var tags = stream.GetProperty("tags");
        Assert.Equal(tags.GetProperty("name").GetString(), tags.GetProperty("handler_name").GetString());
        return tags.GetProperty("name").GetString();
    }

    private static double ParseSeconds(string? text) => double.Parse(text!, CultureInfo.InvariantCulture);

    private static int CountTopLevelBoxes(string path, string type)
    {
        var bytes = File.ReadAllBytes(path);
        var count = 0;
        long offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)offset));
            var name = System.Text.Encoding.ASCII.GetString(bytes, (int)offset + 4, 4);
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan((int)offset + 8));
            }
            else if (size == 0)
            {
                size = bytes.Length - offset;
            }

            if (name == type)
            {
                count++;
            }

            offset += size;
        }

        return count;
    }
}
