using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Platform.Capture;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>
/// Story 1.11's scaler on the real D3D11 VideoProcessor + NVENC path: content letterboxed into the
/// fixed output, bars black over stale pool content, BGRA (X1) and NV12 (X2), and the copy-in fallback.
/// </summary>
public sealed class FrameScalerTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-scaler-").FullName;

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

    public static TheoryData<bool, bool> Paths => new() { { false, false }, { true, false }, { false, true } };

    [Theory]
    [MemberData(nameof(Paths))]
    public void NarrowerContentGetsBlackBarsLeftAndRight(bool nv12, bool copyIn)
    {
        // 4:3 into 16:9: 480×360 at x = 80.
        var file = Encode("narrow.mp4", new VideoSize(480, 360), new VideoSize(480, 360), nv12, copyIn);

        Assert.InRange(MeanLuma(file, "crop=70:360:0:0"), 0, 25);
        Assert.InRange(MeanLuma(file, "crop=70:360:570:0"), 0, 25);
        Assert.InRange(MeanLuma(file, "crop=400:300:120:30"), 225, 255);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void WiderContentGetsBlackBarsTopAndBottom(bool nv12, bool copyIn)
    {
        // 2.37:1 into 16:9: 640×270 at y = 44.
        var file = Encode("wide.mp4", new VideoSize(640, 270), new VideoSize(640, 270), nv12, copyIn);

        Assert.InRange(MeanLuma(file, "crop=640:36:0:0"), 0, 25);
        Assert.InRange(MeanLuma(file, "crop=640:36:0:324"), 0, 25);
        Assert.InRange(MeanLuma(file, "crop=600:220:20:70"), 225, 255);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void ContentEqualToTheOutputFillsItWithNoBars(bool nv12, bool copyIn)
    {
        var file = Encode("equal.mp4", new VideoSize(Width, Height), new VideoSize(Width, Height), nv12, copyIn);

        Assert.InRange(MeanLuma(file, "crop=640:20:0:0"), 225, 255);
        Assert.InRange(MeanLuma(file, "crop=20:360:0:0"), 225, 255);
    }

    [Fact]
    public void OnlyTheContentOfALargerTextureIsScaled()
    {
        // WGC's pool texture outlives a shrink until it's recreated: 480×360 of content in a 640×360 texture.
        var file = Encode("larger.mp4", new VideoSize(640, 360), new VideoSize(480, 360), nv12: false, copyIn: false);

        Assert.InRange(MeanLuma(file, "crop=70:360:570:0"), 0, 25);
        Assert.InRange(MeanLuma(file, "crop=400:300:120:30"), 225, 255);
    }

    private static void RequireFFmpeg()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0,
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");
        FFmpegLibrary.Initialize(NullLogger.Instance);
    }

    /// <summary>Encodes 10 frames of a white <paramref name="content"/> drawn through the scaler into stale red pool textures.</summary>
    private string Encode(string name, VideoSize texture, VideoSize content, bool nv12, bool copyIn)
    {
        RequireFFmpeg();
        var log = NullLogger.Instance;
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        var output = new VideoSize(Width, Height);
        var path = Path.Combine(_directory, name);

        using (var device = D3D11Device.Create(0, log))
        using (var muxer = new FragmentedMp4Muxer(path, log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, Width, Height, VideoEncoder.PoolSize, nv12))
        using (var scaler = new FrameScaler(device, output, nv12, 2 * VideoEncoder.PoolSize, log) { ForceCopyIn = copyIn })
        using (var video = new VideoEncoder(frames, muxer, qpf, anchor, log))
        {
            muxer.Start();
            var source = device.CreateTexture(texture);
            var white = device.CreateTexture(content);
            try
            {
                // White content at the top left; a larger texture's remainder is black, never part of the frame.
                device.Clear(white, 0, 1f, 1f, 1f);
                device.CopyFrame(white, content, source, 0, texture);

                for (var i = 0; i < 10; i++)
                {
                    using var surface = video.TryAcquire(anchor + (i * qpf / 60));
                    Assert.NotNull(surface);
                    if (!nv12)
                    {
                        device.Clear(surface.Texture, surface.Subresource, 1f, 0f, 0f); // stale pool content
                    }

                    scaler.Draw(source, content, surface.Texture, surface.Subresource);
                    video.Submit(surface);
                }
            }
            finally
            {
                D3D11Device.ReleaseTexture(white);
                D3D11Device.ReleaseTexture(source);
            }

            video.Flush();
            muxer.Finish();
        }

        return name;
    }

    private double MeanLuma(string file, string crop)
    {
        var values = Probe($"-v error -f lavfi -i movie={file},{crop},signalstats -show_entries frame_tags=lavfi.signalstats.YAVG -of csv=p=0")
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
