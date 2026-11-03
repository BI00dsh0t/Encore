using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Capture;

namespace ReplayBuffer.Platform.FFmpeg.Tests;

/// <summary>
/// The exact save cut (story 4.2, AD-10) through the real stack: 21 s of virtual capture at 30 fps
/// into a <see cref="ReplayRingSink"/> (D3D11 → NVENC, one AAC Track), a keyframe requested one frame
/// early for presses at 10.02 s and 20.02 s, and two Clips cut as a save and its Reset cut
/// them, written with <see cref="ClipWriter"/> and read back with ffprobe. Needs an NVIDIA GPU and
/// the fetched FFmpeg.
/// </summary>
public sealed class ReplayCutTests : IDisposable
{
    private const int Fps = 30;
    private const int Seconds = 21;
    private const int PacketFrames = 480; // 10 ms
    private const long WindowUs = 60_000_000;
    private const double OneFrame = 1.0 / Fps;
    private const double AacFrame = 1024.0 / AudioTimeline.SampleRate;

    private static readonly double[] Presses = [10.02, 20.02];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rb-cut-" + Guid.NewGuid().ToString("N"));

    public ReplayCutTests() => Directory.CreateDirectory(_directory);

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
    public void ClipsCutAtTheKeyframesForcedAfterTwoPressesMeetWithNoFrameMissingOrRepeated()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0,
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");

        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        string[] clips = [Path.Combine(_directory, "clip1.mp4"), Path.Combine(_directory, "clip2.mp4")];

        using (var device = D3D11Device.Create(0, log))
        using (var frames = new D3D11VAFrames(device.NativeDevice, 640, 360, VideoEncoder.PoolSize))
        using (var sink = new ReplayRingSink(new PacketRing(WindowUs, capacity: 4096)))
        using (var video = new VideoEncoder(frames, sink, qpf, anchor, log, settings: new VideoEncoderOptions(Fps: Fps, BitrateKbps: 2_000)))
        using (var audio = new AacEncoder(sink, "Track", isDefault: true, log))
        {
            var timeline = new AudioTimeline(qpf, anchor, audio);
            var packet = new float[PacketFrames * AudioTimeline.Channels];
            var keys = new long[Presses.Length];
            var servedAt = new int[Presses.Length];
            var pressed = 0;
            long? ticket = null;
            long audioFrames = 0;
            for (var i = 0; i < Fps * Seconds; i++)
            {
                // One frame early, as when the last frame captured at or before the press is still
                // in the pacer's hand-off: the keyframe must still land on the first frame after it.
                if (ticket is null && pressed < Presses.Length && (double)(i + 1) / Fps > Presses[pressed])
                {
                    ticket = video.RequestKeyframe(anchor + (long)(Presses[pressed] * qpf));
                }

                using (var surface = video.TryAcquire(anchor + (i * qpf / Fps)))
                {
                    Assert.NotNull(surface);
                    var level = (i % 64) / 64f;
                    device.Clear(surface.Texture, surface.Subresource, level, level, level);
                    video.Submit(surface);
                }

                // Read as a writer would, long before the next request.
                if (ticket is { } waiting && video.TryGetForcedKey(waiting, out keys[pressed]))
                {
                    servedAt[pressed++] = i;
                    ticket = null;
                }

                var until = (long)((i + 1) * (double)AudioTimeline.SampleRate / Fps);
                for (; audioFrames < until; audioFrames += PacketFrames)
                {
                    Array.Fill(packet, 0.1f * MathF.Sin(audioFrames / 50f));
                    timeline.Push(anchor + (audioFrames * qpf / AudioTimeline.SampleRate), PacketFrames, packet, AudioPacketFlags.None);
                }
            }

            video.Flush();
            audio.Flush();
            Assert.Equal([301, 601], servedAt);

            // A save cuts from its window's start, or the earlier save's end, to its forced keyframe.
            // Both are cut first, then written by two writers at once (R6): neither corrupts the other.
            var sources = new List<IDisposable>();
            try
            {
                var startUs = long.MinValue;
                var cuts = new List<(ClipSource Source, string Path)>();
                for (var p = 0; p < Presses.Length; p++)
                {
                    var toUs = sink.VideoMicros(keys[p]);
                    var pressUs = (long)(Presses[p] * 1_000_000);
                    var source = sink.Cut(Math.Max(pressUs - WindowUs, startUs), toUs);
                    Assert.NotNull(source);
                    sources.Add(source);
                    cuts.Add((source, clips[p]));
                    startUs = toUs;
                }

                var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
                var writers = cuts.Select(cut => new Thread(() =>
                {
                    try
                    {
                        ClipWriter.Write(cut.Source, cut.Path, log);
                    }
                    catch (Exception e)
                    {
                        failures.Enqueue(e);
                    }
                })).ToList();
                writers.ForEach(t => t.Start());
                writers.ForEach(t => t.Join());
                Assert.Empty(failures);
            }
            finally
            {
                sources.ForEach(s => s.Dispose());
            }
        }

        // Clip 1 holds the frames captured at or before press 1 (0 to 10.0 s), Clip 2 those after it up to press 2.
        var first = Streams(clips[0]);
        var second = Streams(clips[1]);
        Assert.Equal(301, first.Video.Packets);
        Assert.Equal(300, second.Video.Packets);
        Assert.InRange(second.Video.Duration, 10 - OneFrame, 10 + OneFrame);
        foreach (var clip in new[] { first, second })
        {
            Assert.InRange(clip.Audio.Duration, clip.Video.Duration - AacFrame, clip.Video.Duration + AacFrame);
        }
    }

    private ((int Packets, double Duration) Video, (int Packets, double Duration) Audio) Streams(string clip)
    {
        using var probe = JsonDocument.Parse(Ffprobe($"-v error -count_packets -show_streams -of json \"{clip}\""));
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => (
                Type: s.GetProperty("codec_type").GetString(),
                Packets: int.Parse(s.GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture),
                Duration: double.Parse(s.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture)))
            .ToList();
        var video = Assert.Single(streams, s => s.Type == "video");
        var audio = Assert.Single(streams, s => s.Type == "audio");
        return ((video.Packets, video.Duration), (audio.Packets, audio.Duration));
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
