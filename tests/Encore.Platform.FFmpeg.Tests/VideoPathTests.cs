using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Platform.Capture;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>The frame-rate and resize rows of the tracer matrix, on the real D3D11 + NVENC path.</summary>
public sealed class VideoPathTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("rb-video-").FullName;

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

    private static void RequireFFmpeg() => Assert.SkipUnless(
        FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0,
        "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");

    [Fact]
    public void AFrameWhosePtsIsNotAboveTheLastIsDropped()
    {
        RequireFFmpeg();
        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();

        using var device = D3D11Device.Create(0, log);
        using var muxer = new FragmentedMp4Muxer(Path.Combine(_directory, "drop.mp4"), log);
        using var frames = new D3D11VAFrames(device.NativeDevice, 320, 240, VideoEncoder.PoolSize);
        using var video = new VideoEncoder(frames, muxer, qpf, anchor, log);
        muxer.Start();

        using (var first = video.TryAcquire(anchor))
        {
            Assert.NotNull(first);
            video.Submit(first);
        }

        Assert.Null(video.TryAcquire(anchor));                // same PTS
        Assert.Null(video.TryAcquire(anchor - (qpf / 100)));  // earlier
        Assert.Null(video.TryAcquire(anchor + 1));            // a few ns later: still PTS 0 at 1/90000
        using (var next = video.TryAcquire(anchor + (qpf / 60)))
        {
            Assert.NotNull(next);
            video.Submit(next);
        }

        Assert.Equal(3, video.DroppedFrames);
        video.Flush();
        muxer.Finish();
    }

    [Fact]
    public void KeyframesComeAtLeastEverySecondEvenAtALowFrameRate()
    {
        RequireFFmpeg();
        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();

        using (var device = D3D11Device.Create(0, log))
        using (var muxer = new FragmentedMp4Muxer(Path.Combine(_directory, "gop.mp4"), log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, 320, 240, VideoEncoder.PoolSize))
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log))
        {
            muxer.Start();

            // 10 fps for 3.5 s: a 60-frame GOP alone would give a single keyframe.
            for (var i = 0; i < 35; i++)
            {
                using var surface = video.TryAcquire(anchor + (i * qpf / 10));
                Assert.NotNull(surface);
                device.Clear(surface.Texture, surface.Subresource, i / 35f, 0f, 0f);
                video.Submit(surface);
            }

            video.Flush();
            muxer.Finish();
        }

        var keyTimes = Probe("-v error -select_streams v -show_entries packet=pts_time,flags -of csv=p=0 gop.mp4")
            .Select(line => line.Split(','))
            .Where(parts => parts.Length >= 2 && parts[1].Contains('K'))
            .Select(parts => Math.Round(double.Parse(parts[0], CultureInfo.InvariantCulture), 1))
            .ToList();
        Assert.Equal([0.0, 1.0, 2.0, 3.0], keyTimes);
    }

    [Fact]
    public void AResizedFrameIsClippedAndTheRestOfTheTargetIsBlack()
    {
        RequireFFmpeg();
        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        var output = new VideoSize(320, 240);
        var path = Path.Combine(_directory, "resize.mp4");

        using (var device = D3D11Device.Create(0, log))
        using (var muxer = new FragmentedMp4Muxer(path, log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, output.Width, output.Height, VideoEncoder.PoolSize))
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log))
        {
            muxer.Start();

            // The window grew wider and shorter than the first frame: 400×120 into 320×240.
            var source = device.CreateTexture(new VideoSize(400, 120));
            try
            {
                device.Clear(source, 0, 1f, 1f, 1f);
                for (var i = 0; i < 30; i++)
                {
                    using var surface = video.TryAcquire(anchor + (i * qpf / 60));
                    Assert.NotNull(surface);
                    device.Clear(surface.Texture, surface.Subresource, 1f, 0f, 0f); // stale pool content
                    device.CopyFrame(source, new VideoSize(400, 120), surface.Texture, surface.Subresource, output);
                    video.Submit(surface);
                }
            }
            finally
            {
                Textures.Release(source);
            }

            video.Flush();
            muxer.Finish();
        }

        // Luma of the copied band (white) and of the cleared band below it (black); no red left.
        Assert.InRange(MeanLuma("crop=320:110:0:0"), 225, 255);
        Assert.InRange(MeanLuma("crop=320:100:0:135"), 0, 25);
    }

    private double MeanLuma(string crop)
    {
        var values = Probe($"-v error -f lavfi -i movie=resize.mp4,{crop},signalstats -show_entries frame_tags=lavfi.signalstats.YAVG -of csv=p=0")
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
            .ToList();
        Assert.NotEmpty(values);
        return values.Average();
    }

    private List<string> Probe(string arguments)
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
        Assert.True(process.ExitCode == 0, stderr);
        return [.. stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
