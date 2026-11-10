using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using static Encore.Platform.FFmpeg.Tests.LongRecordingTests;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>
/// A Recording's first frame is on the frame grid. Synthetic packets as in
/// <see cref="LongRecordingTests"/>; the Recording starts on the keyframe at 6 s, where each AAC
/// Track has a packet that starts 256 samples (5.3 ms) before it and is centred after it.
/// </summary>
public sealed class RecordingStartTests : IDisposable
{
    private const int KeyFrame = 180;
    private const int Seconds = 8;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-start-").FullName;

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
    public async Task AnAudioPacketStartingBeforeTheKeyLeavesTheFirstFrameOnTheGrid()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")),
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");
        FFmpegLibrary.Initialize(NullLogger.Instance);
        var path = Path.Combine(_directory, "start.mp4");
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter(path, Streams(), () => KeyFrame * VideoTicks, 0, NullLogger.Instance, e => done.TrySetResult(e)) { FlushToDisk = false };
        writer.Start();

        Feed(writer);
        writer.OutputEnded();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));

        var pts = VideoPts(path);
        Assert.Equal((Seconds * Fps) - KeyFrame, pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            Assert.True(pts[i] == pts[0] + (i * VideoTicks), $"video packet {i} at {pts[i]}, not {pts[0] + (i * VideoTicks)}");
        }

        // Each Track's audio stays contiguous from its first packet.
        var audio = Packets(path, "a:0");
        for (var i = 1; i < audio.Count; i++)
        {
            Assert.True(audio[i].Pts == audio[i - 1].Pts + audio[i - 1].Duration, $"audio packet {i} at {audio[i].Pts}");
        }
    }

    private static unsafe void Feed(RecordingWriter writer)
    {
        long nextAudio = 0;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            FFmpegLibrary.Check(ffmpeg.av_new_packet(packet, 16), "av_new_packet");
            new Span<byte>(packet->data, 16).Clear(); // never an ADTS header by chance
            for (long frame = 0; frame < Seconds * Fps; frame++)
            {
                Offer(writer, packet, 0, frame * VideoTicks, VideoTicks, frame % KeyEvery == 0);
                var before = (frame + 1) * SampleRate / Fps;
                while (nextAudio * AacSamples < before)
                {
                    for (var track = 1; track <= Tracks; track++)
                    {
                        Offer(writer, packet, track, nextAudio * AacSamples, AacSamples, true);
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

    private static List<long> VideoPts(string path) => [.. Packets(path, "v:0").Select(p => p.Pts)];

    internal static List<(long Pts, long Duration)> Packets(string path, string stream)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-select_streams", stream, "-show_entries", "packet=pts,duration", "-show_packets", "-of", "json", path })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        using var json = JsonDocument.Parse(stdout.Result);
        return [.. json.RootElement.GetProperty("packets").EnumerateArray()
            .Select(p => (p.GetProperty("pts").GetInt64(), p.GetProperty("duration").GetInt64()))
            .OrderBy(p => p.Item1)];
    }
}
