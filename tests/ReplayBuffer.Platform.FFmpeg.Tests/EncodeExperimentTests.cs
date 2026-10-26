using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Capture;

namespace ReplayBuffer.Platform.FFmpeg.Tests;

/// <summary>
/// The story 1.2 encode-stage experiments through the real stack: the X2 colour path
/// (VideoProcessor BGRA → NV12), NVENC preset/tune, and the encoder thread. Needs an NVIDIA
/// GPU and the fetched FFmpeg.
/// </summary>
public sealed class EncodeExperimentTests : IDisposable
{
    private const int Fps = 60;
    private const int Width = 1280;
    private const int Height = 720;
    private const double OneFrame = 1.0 / Fps;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rb-experiment-" + Guid.NewGuid().ToString("N"));

    public EncodeExperimentTests() => Directory.CreateDirectory(_directory);

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
    public void X2ConvertsToNv12TaggedBt709AndKeepsTheFlashWhite()
    {
        RequireFFmpeg();
        var log = NullLogger.Instance;
        var path = Path.Combine(_directory, "x2.mp4");
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        var size = new VideoSize(Width, Height);

        using (var device = D3D11Device.Create(0, log))
        using (var muxer = new FragmentedMp4Muxer(path, log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, Width, Height, VideoEncoder.PoolSize, nv12: true))
        using (var converter = new Nv12Converter(device, size, 2 * VideoEncoder.PoolSize))
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log))
        using (var output = new AacEncoder(muxer, "Synthetic Output", isDefault: true, log))
        using (var input = new AacEncoder(muxer, "Synthetic Input", isDefault: false, log))
        {
            muxer.Start();
            for (var i = 0; i < Fps * 2; i++)
            {
                using var surface = video.TryAcquire(anchor + (i * qpf / Fps));
                Assert.NotNull(surface);
                var level = i == Fps ? 1f : 0f;
                device.Clear(converter.InputTexture, 0, level, level, level);
                converter.Convert(surface.Texture, surface.Subresource);
                video.Submit(surface);
            }

            output.WriteSilence(2 * AudioTimeline.SampleRate);
            input.WriteSilence(2 * AudioTimeline.SampleRate);
            video.Flush();
            output.Flush();
            input.Flush();
            muxer.Finish();
        }

        using var probe = JsonDocument.Parse(Ffprobe("-v error -show_streams -of json x2.mp4"));
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToList();
        Assert.Equal(["h264", "aac", "aac"], streams.Select(s => s.GetProperty("codec_name").GetString()));
        var videoStream = streams[0];
        Assert.Equal("tv", videoStream.GetProperty("color_range").GetString());
        Assert.Equal("bt709", videoStream.GetProperty("color_space").GetString());

        var (time, luma) = BrightestFrame("x2.mp4");
        Assert.True(luma >= 230, $"the white flash decoded with Y {luma}");
        Assert.InRange(time, 1 - OneFrame, 1 + OneFrame);
    }

    [Fact]
    public void TheEncoderThreadEncodesEveryPostedFrameWithAFastPreset()
    {
        RequireFFmpeg();
        var log = NullLogger.Instance;
        var path = Path.Combine(_directory, "thread.mp4");
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        var latency = new LatencyHistogram(qpf);
        long encoded, flushed;
        var posted = 0;

        using (var device = D3D11Device.Create(0, log))
        using (var muxer = new FragmentedMp4Muxer(path, log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, Width, Height, VideoEncoder.PoolSize))
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log, latency, new VideoEncoderOptions("p1", "ll")))
        using (var output = new AacEncoder(muxer, "Synthetic Output", isDefault: true, log))
        using (var input = new AacEncoder(muxer, "Synthetic Input", isDefault: false, log))
        {
            muxer.Start();
            using (var thread = new EncoderThread(video, log))
            {
                for (var i = 0; i < Fps; i++)
                {
                    // Paced like capture, so the hand-off rarely fills.
                    while (!thread.TryReserve())
                    {
                        Thread.Sleep(1);
                    }

                    Assert.True(video.TryAcquire(anchor + (i * qpf / Fps), out var frame));
                    device.Clear(frame.Texture, frame.Subresource, 0.5f, 0.5f, 0.5f);
                    thread.Post(frame);
                    posted++;
                }

                thread.Complete();
                Assert.Null(thread.Fault);
            }

            output.WriteSilence(AudioTimeline.SampleRate);
            input.WriteSilence(AudioTimeline.SampleRate);
            video.Flush();
            output.Flush();
            input.Flush();
            muxer.Finish();
            encoded = video.EncodedFrames;
            flushed = video.FlushedPackets;
        }

        Assert.Equal(Fps, posted);
        Assert.Equal(posted, encoded);
        Assert.Equal(encoded, latency.Count + flushed);

        var count = Ffprobe("-v error -count_packets -select_streams v:0 -show_entries stream=nb_read_packets -of csv=p=0 thread.mp4");
        Assert.Equal(Fps, int.Parse(count.Trim(), CultureInfo.InvariantCulture));
    }

    private static void RequireFFmpeg()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0,
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");
        FFmpegLibrary.Initialize(NullLogger.Instance);
    }

    private (double Time, double Luma) BrightestFrame(string file)
    {
        var csv = Ffprobe($"-v error -f lavfi -i movie={file},signalstats "
            + "-show_entries frame=pts_time:frame_tags=lavfi.signalstats.YAVG -of csv=p=0");
        var best = (Time: double.NaN, Luma: double.MinValue);
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var luma) && luma > best.Luma)
            {
                best = (double.Parse(parts[0], CultureInfo.InvariantCulture), luma);
            }
        }

        return best;
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
}
