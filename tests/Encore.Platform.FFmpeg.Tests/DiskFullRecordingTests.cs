using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Core.Output;
using static Encore.Platform.FFmpeg.Tests.LongRecordingTests;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>A Recording whose disk fills keeps its file, cut back to its last complete fragment. Needs the fetched FFmpeg.</summary>
public sealed class DiskFullRecordingTests : IDisposable
{
    private const long Capacity = 200 * 1024;
    private const int Seconds = 30;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-diskfull-").FullName;

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
    public async Task AFullDiskLeavesTheFileAtItsLastWholeFragmentWithEveryTrack()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")),
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");
        FFmpegLibrary.Initialize(NullLogger.Instance);
        var path = Path.Combine(_directory, "full.mp4");
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter(path, Streams(), () => 0, 0, NullLogger.Instance, e => done.TrySetResult(e))
        {
            FlushToDisk = false,
            CapacityBytes = Capacity,
        };
        writer.Start();

        Feed(writer);
        writer.OutputEnded();

        var error = await done.Task.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.DiskFull, WriteFailures.ReasonOf(error));
        Assert.True(File.Exists(path));
        // The seam fills the file to the capacity; a cut back to the last whole fragment leaves it shorter.
        Assert.InRange(new FileInfo(path).Length, 1, Capacity - 1);

        var durations = Probe(path);
        Assert.Equal(1 + Tracks, durations.Count);
        Assert.True(durations[0] > 1.0, $"The video is {durations[0]} s");
        foreach (var audio in durations.Skip(1))
        {
            Assert.InRange(audio, durations[0] - 0.5, durations[0] + 0.5);
        }
    }

    /// <summary>Packets in time order: 2 KB video frames and small AAC ones, so the capacity fills after a few seconds.</summary>
    private static unsafe void Feed(RecordingWriter writer)
    {
        var video = ffmpeg.av_packet_alloc();
        var audio = ffmpeg.av_packet_alloc();
        try
        {
            FFmpegLibrary.Check(ffmpeg.av_new_packet(video, 2048), "av_new_packet");
            FFmpegLibrary.Check(ffmpeg.av_new_packet(audio, 64), "av_new_packet");
            new Span<byte>(audio->data, 64).Clear(); // never an ADTS header by chance
            long nextAudio = 0;
            for (long frame = 0; frame < (long)Seconds * Fps; frame++)
            {
                Offer(writer, video, 0, frame * VideoTicks, VideoTicks, frame % KeyEvery == 0);
                var before = (frame + 1) * SampleRate / Fps;
                while (nextAudio * AacSamples < before)
                {
                    for (var track = 1; track <= Tracks; track++)
                    {
                        Offer(writer, audio, track, nextAudio * AacSamples, AacSamples, true);
                    }

                    nextAudio++;
                }

                while (writer.Queued > 256)
                {
                    Thread.Yield();
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&video);
            ffmpeg.av_packet_free(&audio);
        }
    }

    /// <summary>Each stream's duration in seconds, from its packets; ffprobe must read the file with no demuxer error.</summary>
    private static List<double> Probe(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "json", path })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);

        // The synthetic payloads are zeros, so ffprobe's decoders complain while it probes; the demuxer must not.
        var demuxErrors = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("[mpeg4 @", StringComparison.Ordinal)
                && !line.StartsWith("[aac @", StringComparison.Ordinal)
                && !line.StartsWith("Last message repeated", StringComparison.Ordinal))
            .ToList();
        Assert.True(demuxErrors.Count == 0, stderr);

        using var json = JsonDocument.Parse(stdout.Result);
        var ends = new SortedDictionary<int, double>();
        foreach (var packet in json.RootElement.GetProperty("packets").EnumerateArray())
        {
            var stream = packet.GetProperty("stream_index").GetInt32();
            var end = double.Parse(packet.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture)
                + double.Parse(packet.GetProperty("duration_time").GetString()!, CultureInfo.InvariantCulture);
            ends[stream] = Math.Max(ends.GetValueOrDefault(stream), end);
        }

        return [.. ends.Values];
    }
}
