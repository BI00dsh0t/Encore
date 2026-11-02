using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Capture;

namespace ReplayBuffer.Platform.FFmpeg.Tests;

/// <summary>
/// The encode-stage options through the real stack: the X2 colour path (the scaler's VideoProcessor
/// blit writing NV12), NVENC preset/tune, and the paced encoder thread. Needs an NVIDIA GPU and the
/// fetched FFmpeg.
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
        using (var scaler = new FrameScaler(device, size, nv12: true, 2 * VideoEncoder.PoolSize, log))
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log))
        using (var output = new AacEncoder(muxer, "Synthetic Output", isDefault: true, log))
        using (var input = new AacEncoder(muxer, "Synthetic Input", isDefault: false, log))
        {
            muxer.Start();
            var source = device.CreateTexture(size);
            try
            {
                for (var i = 0; i < Fps * 2; i++)
                {
                    using var surface = video.TryAcquire(anchor + (i * qpf / Fps));
                    Assert.NotNull(surface);
                    var level = i == Fps ? 1f : 0f;
                    device.Clear(source, 0, level, level, level);
                    scaler.Draw(source, size, surface.Texture, surface.Subresource);
                    video.Submit(surface);
                }
            }
            finally
            {
                D3D11Device.ReleaseTexture(source);
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
    public void ThePacedEncoderEncodesEverySlotWithAFastPreset()
    {
        RequireFFmpeg();
        RealTimeAttempts.Run(PacedEncoderAttempt);
    }

    private void PacedEncoderAttempt(int attempt)
    {
        var log = NullLogger.Instance;
        var file = $"thread-{attempt}.mp4";
        var path = Path.Combine(_directory, file);
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        var latency = new LatencyHistogram(qpf);
        long encoded, flushed, fresh, repeated, emitted;

        using (var device = D3D11Device.Create(0, log))
        using (var muxer = new FragmentedMp4Muxer(path, log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, Width, Height, VideoEncoder.PoolSize))
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log, latency, new VideoEncoderOptions("p1", "ll")))
        using (var output = new AacEncoder(muxer, "Synthetic Output", isDefault: true, log))
        using (var input = new AacEncoder(muxer, "Synthetic Input", isDefault: false, log))
        {
            muxer.Start();
            // The pacer's anchor is the first frame's capture QPC, after the setup above.
            var start = Stopwatch.GetTimestamp();
            using (var paced = new PacedVideoEncoder(video, new FramePacer(qpf, Fps, start), log))
            {
                // A new frame every slot for ½ s, posted at its capture time; then nothing for ½ s.
                for (var i = 0; i < Fps / 2; i++)
                {
                    var due = start + (i * qpf / Fps);
                    // Spun, not slept: a 15.6 ms sleep would count as latency.
                    while (Stopwatch.GetTimestamp() < due)
                    {
                        Thread.Yield();
                    }

                    Assert.True(paced.TryReserve());
                    Assert.True(video.TryAcquireFrame(out var frame));
                    device.Clear(frame.Texture, frame.Subresource, 0.5f, 0.5f, 0.5f);
                    paced.Post(frame, due);
                }

                while (Stopwatch.GetTimestamp() < start + qpf)
                {
                    Thread.Sleep(5);
                }

                paced.Complete();
                Assert.Null(paced.Fault);
                fresh = paced.Pacer.FreshSlots;
                repeated = paced.Pacer.RepeatedSlots;
                emitted = paced.Pacer.EmittedSlots;
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

        // One second of slots, each encoded once: the static half by repeats, in real time.
        Assert.InRange(emitted, Fps - 2, Fps + 2);
        Assert.Equal(emitted, encoded);
        Assert.InRange(fresh, (Fps / 2) - 2, Fps / 2);
        Assert.InRange(repeated, (Fps / 2) - 2, (Fps / 2) + 2);

        // Latency is recorded for new frames only; with delay 0 none waits for the flush.
        Assert.Equal(fresh, latency.Count + flushed);
        Assert.Equal(0, flushed);
        // The median, not p99: other GPU tests run beside this one (the p99 bound is the stats verify's).
        // A median missed under that contention is retried by RealTimeAttempts; the bound is unchanged.
        Assert.True(latency.Percentile(50) < 1000.0 / Fps, $"latency p50 {latency.Percentile(50)} ms");

        var pts = Ffprobe($"-v error -select_streams v:0 -show_entries packet=pts -of csv=p=0 {file}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => long.Parse(p, CultureInfo.InvariantCulture))
            .Order()
            .ToList();
        Assert.Equal(encoded, pts.Count);
        for (var i = 1; i < pts.Count; i++)
        {
            Assert.Equal(pts[0] + (i * (pts[1] - pts[0])), pts[i]);
        }
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
