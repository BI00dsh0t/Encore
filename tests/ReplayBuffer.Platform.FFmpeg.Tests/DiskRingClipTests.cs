using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Core.Replay;
using ReplayBuffer.Media;
using ReplayBuffer.Platform.Capture;

namespace ReplayBuffer.Platform.FFmpeg.Tests;

/// <summary>
/// Disk mode's Clips (R3): about 4 s of NVENC and one AAC Track go, packet for packet, into a
/// Memory-mode and a Disk-mode <see cref="ReplayRingSink"/>; each is cut at the same points and
/// written with <see cref="ClipWriter"/>, and the two files are byte-identical. Needs an NVIDIA GPU
/// and the fetched FFmpeg.
/// </summary>
public sealed class DiskRingClipTests : IDisposable
{
    private const int Fps = 30;
    private const int Seconds = 4;
    private const int PacketFrames = 480; // 10 ms
    private const long WindowUs = 30_000_000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rb-disk-clip-" + Guid.NewGuid().ToString("N"));

    public DiskRingClipTests() => Directory.CreateDirectory(_directory);

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

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public void AClipFromADiskRingIsByteIdenticalToTheSameClipFromAMemoryRing()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0,
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");

        var log = NullLogger.Instance;
        FFmpegLibrary.Initialize(log);
        var qpf = Stopwatch.Frequency;
        var anchor = Stopwatch.GetTimestamp();
        string[] clips = [Path.Combine(_directory, "memory.mp4"), Path.Combine(_directory, "disk.mp4")];
        var ringFile = RingFile.Create(Path.Combine(_directory, "ring"), 64L * 1024 * 1024);

        using (var memory = new ReplayRingSink(new PacketRing(WindowUs, capacity: 1024)))
        using (var disk = new ReplayRingSink(new PacketRing(WindowUs, capacity: 1024, ringFile)))
        {
            var tee = new TeeSink(memory, disk);
            using (var device = D3D11Device.Create(0, log))
            using (var frames = new D3D11VAFrames(device.NativeDevice, 640, 360, VideoEncoder.PoolSize))
            using (var video = new VideoEncoder(frames, tee, qpf, anchor, log, settings: new VideoEncoderOptions(Fps: Fps, BitrateKbps: 2_000)))
            using (var audio = new AacEncoder(tee, "Track", isDefault: true, log))
            {
                var timeline = new AudioTimeline(qpf, anchor, audio);
                var packet = new float[PacketFrames * AudioTimeline.Channels];
                long audioFrames = 0;
                for (var i = 0; i < Fps * Seconds; i++)
                {
                    using (var surface = video.TryAcquire(anchor + (i * qpf / Fps)))
                    {
                        Assert.NotNull(surface);
                        var level = (i % 64) / 64f;
                        device.Clear(surface.Texture, surface.Subresource, level, level, level);
                        video.Submit(surface);
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
            }

            const long toUs = 3_000_000;
            using (var fromMemory = memory.Cut(0, toUs))
            using (var fromDisk = disk.Cut(0, toUs))
            {
                Assert.NotNull(fromMemory);
                Assert.NotNull(fromDisk);
                ClipWriter.Write(fromMemory, clips[0], log);
                ClipWriter.Write(fromDisk, clips[1], log);
            }
        }

        // Both sinks disposed and no cut held: the ring file is gone.
        Assert.False(File.Exists(ringFile.Path));
        var bytes = File.ReadAllBytes(clips[0]);
        Assert.True(bytes.Length > 10_000, $"the Clip is only {bytes.Length} bytes");
        Assert.Equal(bytes, File.ReadAllBytes(clips[1]));
    }

    /// <summary>Hands each packet to both sinks: a new reference for the first, the packet itself for the second.</summary>
    private sealed unsafe class TeeSink(PacketSink first, PacketSink second) : PacketSink
    {
        internal override bool NeedsGlobalHeader => true;

        internal override int AddStream(AVCodecContext* encoder, string? title, bool isDefault)
        {
            var index = first.AddStream(encoder, title, isDefault);
            Assert.Equal(index, second.AddStream(encoder, title, isDefault));
            return index;
        }

        internal override void Write(AVPacket* packet, int streamIndex, AVRational timeBase)
        {
            var clone = ffmpeg.av_packet_alloc();
            try
            {
                FFmpegLibrary.Check(ffmpeg.av_packet_ref(clone, packet), "av_packet_ref");
                first.Write(clone, streamIndex, timeBase);
            }
            finally
            {
                ffmpeg.av_packet_free(&clone);
            }

            second.Write(packet, streamIndex, timeBase);
        }
    }
}
