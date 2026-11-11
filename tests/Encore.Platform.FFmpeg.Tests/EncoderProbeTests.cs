using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts.Settings;
using Encore.Platform.Capture;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>The encoder probe against the oracle, plus the libx264 path and the HEVC tag, on the real D3D11 device.</summary>
public sealed class EncoderProbeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("rb-probe-").FullName;

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
    public void TheEncodersThatWorkAreTheOnesFFmpegAccepts()
    {
        Gpu.Require();
        RequireFFmpeg();
        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        using var device = D3D11Device.Create(0, log);

        var oracle = EncoderOracle.Accepted(AppContext.BaseDirectory);
        var working = EncoderProbe.HardwareCandidates.Where(name => EncoderProbe.Works(device.NativeDevice, name, log)).ToList();

        Assert.Equal(oracle, working);
    }

    [Fact]
    public void EachCodecIsOfferedWithItsFirstWorkingEncoderAndOnlyH264FallsBackToLibx264()
    {
        Gpu.Require();
        RequireFFmpeg();
        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        using var device = D3D11Device.Create(0, log);
        var oracle = EncoderOracle.Accepted(AppContext.BaseDirectory);

        var offers = EncoderProbe.Probe(device.NativeDevice, log);

        var expected = EncoderProbe.Codecs
            .Select(codec => (codec, encoder: EncoderProbe.Candidates(codec).FirstOrDefault(oracle.Contains)
                ?? (codec == VideoCodec.H264 ? EncoderProbe.SoftwareH264 : null)))
            .Where(e => e.encoder is not null)
            .Select(e => (e.codec, e.encoder!, e.codec != VideoCodec.H264))
            .ToList();
        Assert.Equal(expected, offers.Select(o => (o.Codec, o.Encoder, o.MayNotPlayEverywhere)));

        var h264 = Assert.Single(offers, o => o.Codec == VideoCodec.H264);
        Assert.Equal(!EncoderProbe.Candidates(VideoCodec.H264).Any(oracle.Contains), h264.Encoder == EncoderProbe.SoftwareH264);
    }

    [Theory]
    [InlineData(VideoCodec.H264, new[] { "h264_amf", "h264_qsv", "libx264" }, "h264_amf")]
    [InlineData(VideoCodec.H264, new[] { "h264_qsv", "libx264" }, "h264_qsv")]
    public void WithNoHardwareH264OnlyH264FallsBackToLibx264(VideoCodec codec, string[] working, string? expected)
    {
        var tried = new List<string>();
        var chosen = EncoderProbe.Choose(codec, name =>
        {
            tried.Add(name);
            return working.Contains(name);
        });

        Assert.Equal(expected, chosen);

        // Hardware candidates in order, stopping at the first that works; libx264 only for H.264 with none.
        var hardware = EncoderProbe.Candidates(codec);
        var stop = chosen is null || chosen == EncoderProbe.SoftwareH264 ? hardware.Count : hardware.ToList().IndexOf(chosen) + 1;
        Assert.Equal(hardware.Take(stop), tried.Where(name => name != EncoderProbe.SoftwareH264));
        Assert.Equal(codec == VideoCodec.H264 && chosen is null or EncoderProbe.SoftwareH264, tried.Contains(EncoderProbe.SoftwareH264));
    }

    [Fact]
    public void AnEncoderThatIsNotInTheBuildDoesNotWork()
    {
        Gpu.Require();
        RequireFFmpeg();
        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        using var device = D3D11Device.Create(0, log);

        Assert.False(EncoderProbe.Works(device.NativeDevice, "h264_nonexistent", log));
        Assert.Null(EncoderProbe.CodecOf("aac"));
        Assert.Equal(VideoCodec.Av1, EncoderProbe.CodecOf("av1_nvenc"));
    }

    [Fact]
    public void Libx264EncodesTheD3D11FramesThroughSystemMemory()
    {
        Gpu.Require();
        RequireFFmpeg();
        var path = Path.Combine(_directory, "x264.mp4");
        Encode(path, new VideoEncoderOptions(Encoder: EncoderProbe.SoftwareH264, Fps: 30, BitrateKbps: 4_000), frames: 45, fps: 30, white: true);

        Assert.Equal("h264,320,240,yuv420p,45", Probe("-v error -select_streams v -count_packets -show_entries stream=codec_name,pix_fmt,width,height,nb_read_packets -of csv=p=0", path));

        // The white BGRA frames come out white in limited-range luma: the conversion ran.
        var luma = Probe("-v error -f lavfi -i movie=x264.mp4,signalstats -show_entries frame_tags=lavfi.signalstats.YAVG -of csv=p=0", null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
            .Average();
        Assert.InRange(luma, 225, 240);
    }

    [Fact]
    public void HevcIsTaggedHvc1()
    {
        Gpu.Require();
        RequireFFmpeg();
        FFmpegLibrary.Initialize(NullLogger.Instance);
        string? encoder;
        using (var device = D3D11Device.Create(0, NullLogger.Instance))
        {
            encoder = EncoderProbe.ProbeCodec(device.NativeDevice, VideoCodec.Hevc, NullLogger.Instance)?.Encoder;
        }

        Assert.SkipWhen(encoder is null, "no HEVC encoder on this PC");
        var path = Path.Combine(_directory, "hevc.mp4");
        Encode(path, new VideoEncoderOptions(Encoder: encoder!, Codec: VideoCodec.Hevc), frames: 10, fps: 60, white: false);

        Assert.Equal("hevc,hvc1", Probe("-v error -select_streams v -show_entries stream=codec_name,codec_tag_string -of csv=p=0", path));
    }

    private static void RequireFFmpeg() => Assert.SkipUnless(
        FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")),
        "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");

    private static void Encode(string path, VideoEncoderOptions options, int frames, int fps, bool white)
    {
        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        using var device = D3D11Device.Create(0, log);
        using var muxer = new FragmentedMp4Muxer(path, log);
        using var pool = new D3D11VAFrames(device.NativeDevice, 320, 240, VideoEncoder.PoolSize);
        using var video = new VideoEncoder(pool, muxer, qpf, anchor, log, settings: options);
        muxer.Start();
        for (var i = 0; i < frames; i++)
        {
            using var surface = video.TryAcquire(anchor + (i * qpf / fps));
            Assert.NotNull(surface);
            var level = white ? 1f : i / (float)frames;
            device.Clear(surface.Texture, surface.Subresource, level, level, level);
            video.Submit(surface);
        }

        video.Flush();
        muxer.Finish();
    }

    private string Probe(string arguments, string? file)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"), file is null ? arguments : $"{arguments} \"{file}\"")
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
        return stdout.Result.Trim().Replace("\r", "", StringComparison.Ordinal);
    }
}
