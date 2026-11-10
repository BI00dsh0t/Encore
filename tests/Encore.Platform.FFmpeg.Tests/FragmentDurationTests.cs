using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Core.Library;
using static Encore.Platform.FFmpeg.Tests.LongRecordingTests;

namespace Encore.Platform.FFmpeg.Tests;

/// <summary>
/// Story 14.3: a finalized Recording carries its length in an <c>mehd</c>, the last child of its
/// <c>mvex</c>, patched in place over the <c>udta</c>'s start, with nothing after the <c>moov</c>
/// moved. Synthetic packets as in <see cref="LongRecordingTests"/>; needs the fetched FFmpeg.
/// </summary>
public sealed class FragmentDurationTests : IDisposable
{
    private const int Seconds = 3;

    private readonly string _directory = Directory.CreateTempSubdirectory("rb-mehd-").FullName;

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
    public async Task AFinalizedRecordingHasAnMehdOfItsLengthAndEveryOffsetAfterTheMoovStillHolds()
    {
        Assert.SkipUnless(
            FFmpegLibrary.MissingLibraries(AppContext.BaseDirectory).Count == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")),
            "native/ffmpeg is not fetched; run pwsh tools/fetch-ffmpeg.ps1");
        FFmpegLibrary.Initialize(NullLogger.Instance);
        var path = Path.Combine(_directory, "mehd.mp4");
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter(path, Streams(), () => 0, 0, NullLogger.Instance, e => done.TrySetResult(e)) { FlushToDisk = false };
        writer.Start();
        Feed(writer);
        writer.OutputEnded();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));

        var frame = 1.0 / Fps;
        var bytes = File.ReadAllBytes(path);
        Assert.True(Mp4File.IsComplete(path));
        var top = Children(bytes, 0, bytes.Length);
        var moov = top.Single(b => b.Type == "moov");
        var children = Children(bytes, moov.Start + 8, moov.End);
        Assert.Equal(moov.End, children[^1].End);

        // mvex ends with a v1 mehd; a free box follows it, to where the udta ended.
        var mvexAt = children.FindIndex(b => b.Type == "mvex");
        var mvex = children[mvexAt];
        var mehd = Children(bytes, mvex.Start + 8, mvex.End)[^1];
        Assert.Equal(("mehd", 20), (mehd.Type, mehd.End - mehd.Start));
        Assert.Equal(1, bytes[mehd.Start + 8]);
        Assert.Equal("free", children[mvexAt + 1].Type);
        Assert.Equal(moov.End, children[mvexAt + 1].End);
        Assert.DoesNotContain(children, b => b.Type == "udta");

        // Its length, in the mvhd timescale, is the video's.
        var mvhd = children.Single(b => b.Type == "mvhd");
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mvhd.Start + (bytes[mvhd.Start + 8] == 1 ? 28 : 20)));
        var length = (double)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(mehd.Start + 12)) / timescale;
        Assert.InRange(length, Seconds - frame, Seconds + frame);
        var (format, titles) = Probe(path);
        Assert.InRange(format, Seconds - frame, Seconds + frame);
        Assert.Equal([null, .. Enumerable.Range(1, Tracks).Select(i => $"Track {i}")], titles);

        // The mfra is last and whole, and its first tfra entry still points at a moof.
        var mfra = top[^1];
        Assert.Equal("mfra", mfra.Type);
        Assert.Equal((uint)(mfra.End - mfra.Start), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 4)));
        var tfra = Children(bytes, mfra.Start + 8, mfra.End).First(b => b.Type == "tfra");
        var v1 = bytes[tfra.Start + 8] == 1;
        var entry = tfra.Start + 24;
        var moof = v1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(entry + 8)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 4));
        Assert.Contains(top, b => b.Start == moof && b.Type == "moof");

        // Cut between the two writes: mvex not yet grown, a stray mehd directly in the moov.
        var between = Path.Combine(_directory, "between.mp4");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(mvex.Start), (uint)(mvex.End - mvex.Start - 20));
        File.WriteAllBytes(between, bytes);
        Assert.True(Mp4File.IsComplete(between));
        Assert.Equal(moov.End, Children(bytes, moov.Start + 8, moov.End)[^1].End);
        Assert.Equal(1 + Tracks, Probe(between).Titles.Count);
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

    /// <summary>The boxes that tile [<paramref name="start"/>, <paramref name="end"/>), each with 32-bit sizes; fails if they don't.</summary>
    private static List<(string Type, int Start, int End)> Children(byte[] bytes, int start, int end)
    {
        var boxes = new List<(string, int, int)>();
        for (var position = start; position < end;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            Assert.InRange(size, 8, end - position);
            boxes.Add((Encoding.ASCII.GetString(bytes, position + 4, 4), position, position + size));
            position += size;
        }

        return boxes;
    }

    /// <summary>ffprobe's container duration in seconds and each stream's title.</summary>
    private static (double Format, List<string?> Titles) Probe(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration:stream_tags=title,name", "-of", "json", path })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        using var json = JsonDocument.Parse(stdout.Result);
        var root = json.RootElement;
        var format = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var titles = root.GetProperty("streams").EnumerateArray()
            .Select(s => s.TryGetProperty("tags", out var tags) && (tags.TryGetProperty("name", out var title) || tags.TryGetProperty("title", out title)) ? title.GetString() : null)
            .ToList();
        return (format, titles);
    }
}
