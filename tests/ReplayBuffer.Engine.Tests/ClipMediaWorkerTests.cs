using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Engine.Tests;

/// <summary>All real-media work is explicit and deferred. No capture hardware or real Library files.</summary>
public sealed class ClipMediaWorkerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rb-clip-media-").FullName;
    public void Dispose() => Directory.Delete(_folder, true);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ChangedAndMissingSourceIdentityIsRejectedWithoutLaunchingTools()
    {
        var path = Path.Combine(_folder, "source.mp4");
        File.WriteAllBytes(path, [1]);
        var source = Identity(path);
        ClipMediaWorker.ValidateSource(source);
        File.AppendAllText(path, "changed");
        Assert.Throws<IOException>(() => ClipMediaWorker.ValidateSource(source));
        File.Delete(path);
        Assert.Throws<IOException>(() => ClipMediaWorker.ValidateSource(source));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ExactRangeNamedStreamsSeparateMp3AndMixPreserveOffsets()
    {
        var source = await FixtureAsync();
        var probe = await ClipMediaWorker.ExecuteAsync(new("probe", source), null, Token);
        Assert.Equal(["Game", "Mic delayed"], probe.Metadata!.Tracks.Select(t => t.Name));
        var request = new ClipMediaRequest("export", source, 0.113, 1.417, Tracks: [1, 2], Destination: Path.Combine(_folder, "separate.mp4"));
        var result = await ClipMediaWorker.ExecuteAsync(request, null, Token);
        Assert.InRange(result.Range!.Start, 0.113, 0.147);
        Assert.InRange(result.Range.End, 1.417, 1.451);
        var media = await InspectAsync(result.Output!);
        Assert.Equal(2, AudioCount(media));
        Assert.InRange(Duration(media), result.Range.End - result.Range.Start - 0.05, result.Range.End - result.Range.Start + 0.08);
        Assert.Equal(["Game", "Mic delayed"], media.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "audio")
            .Select(s => s.GetProperty("tags").GetProperty("handler_name").GetString()));
        var mix = await ClipMediaWorker.ExecuteAsync(request with { Mix = true, Destination = Path.Combine(_folder, "mix.mp4") }, null, Token);
        Assert.Equal(1, AudioCount(await InspectAsync(mix.Output!)));
        var directory = Path.Combine(_folder, "mp3 tracks");
        await ClipMediaWorker.ExecuteAsync(request with { Format = "mp3", Destination = directory }, null, Token);
        Assert.Equal(2, Directory.GetFiles(directory, "*.mp3").Length);
        Assert.Contains(Directory.GetFiles(directory), p => Path.GetFileName(p).Contains("Game", StringComparison.Ordinal));
        Assert.Contains(Directory.GetFiles(directory), p => Path.GetFileName(p).Contains("Mic delayed", StringComparison.Ordinal));
        var delayedMp3 = Directory.GetFiles(directory).Single(p => Path.GetFileName(p).Contains("Mic delayed", StringComparison.Ordinal));
        var samples = await DecodeStereoAsync(delayedMp3, Path.Combine(_folder, "delayed.pcm"));
        // 0.113 and 1.417 are exact 48 kHz sample boundaries, independent of the 30 fps picture.
        Assert.InRange(samples.Length / 2, 62591, 62593); // (1.417 - 0.113) * 48000
        var onset = FirstAudibleTime(samples);
        Assert.InRange(onset, 0.127, 0.147); // delayed source begins near 0.250, relative to 0.113
        var audioRange = await ClipMediaWorker.ExecuteAsync(request with { Operation = "range", Format = "mp3" }, null, Token);
        Assert.Equal("audioSamples48000", audioRange.Range!.Alignment);
        Assert.Equal(0.113, audioRange.Range.Start, 9);
        Assert.Equal(1.417, audioRange.Range.End, 9);
        var audioPreview = await ClipMediaWorker.ExecuteAsync(request with
        {
            Operation = "preview", Format = "mp3", Tracks = [2], Mix = true,
            Destination = Path.Combine(_folder, "audio-selection-preview.mp4"),
        }, null, Token);
        Assert.Equal(audioRange.Range, audioPreview.Range);
        var previewSamples = await DecodeStereoAsync(audioPreview.Output!, Path.Combine(_folder, "preview.pcm"));
        Assert.InRange(FirstAudibleTime(previewSamples), onset - 0.008, onset + 0.008);
        var mixedMp3 = await ClipMediaWorker.ExecuteAsync(request with { Format = "mp3", Mix = true, Destination = Path.Combine(_folder, "combined.mp3") }, null, Token);
        var mp3Media = await InspectAsync(mixedMp3.Output!);
        Assert.Equal(1, AudioCount(mp3Media));
        Assert.Equal("48000", mp3Media.GetProperty("streams")[0].GetProperty("sample_rate").GetString());
        Assert.Equal(2, mp3Media.GetProperty("streams")[0].GetProperty("channels").GetInt32());
        var wave = await ClipMediaWorker.ExecuteAsync(new("waveform", source, 0, 0.7, Bins: 700, Tracks: [2]), null, Token);
        Assert.All(wave.Waveforms![0].Peaks.Take(180), p => Assert.InRange(p, 0, 0.005f));
        Assert.Contains(wave.Waveforms[0].Peaks.Skip(300), p => p > 0.01f);
        // Verify decoded output keeps the delayed track's leading silence on the shared video timeline.
        var outputSource = Identity(result.Output!);
        var outputProbe = await ClipMediaWorker.ExecuteAsync(new("probe", outputSource), null, Token);
        var delayed = outputProbe.Metadata!.Tracks[1].StreamIndex;
        var cutWave = await ClipMediaWorker.ExecuteAsync(new("waveform", outputSource, 0, 0.5, Bins: 500, Tracks: [delayed]), null, Token);
        Assert.All(cutWave.Waveforms![0].Peaks.Take(50), p => Assert.InRange(p, 0, 0.01f));
        Assert.Contains(cutWave.Waveforms[0].Peaks.Skip(220), p => p > 0.01f);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task CollisionAndCancellationLeaveSourcesAndExistingDestinationsIntact()
    {
        var source = await FixtureAsync();
        var existing = Path.Combine(_folder, "existing.mp4");
        File.WriteAllText(existing, "keep me");
        var request = new ClipMediaRequest("export", source, 0.1, 1.4, Tracks: [1], Destination: existing);
        await Assert.ThrowsAsync<IOException>(() => ClipMediaWorker.ExecuteAsync(request, null, Token));
        Assert.Equal("keep me", File.ReadAllText(existing));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sawScratch = false;
        var progress = new CallbackProgress(() =>
        {
            sawScratch = Directory.GetDirectories(_folder, ".rb-clip-*").Length > 0;
            cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipMediaWorker.ExecuteAsync(request with { Destination = Path.Combine(_folder, "cancel.mp4") }, progress, cancel.Token));
        Assert.True(sawScratch);
        ClipMediaWorker.ValidateSource(source);
        Assert.False(File.Exists(Path.Combine(_folder, "cancel.mp4")));
        Assert.Empty(Directory.GetDirectories(_folder, ".rb-clip-*"));
    }

    private sealed class CallbackProgress(Action callback) : IProgress<double>
    {
        public void Report(double value) => callback();
    }

    private async Task<ClipSource> FixtureAsync()
    {
        var path = Path.Combine(_folder, "fixture.mp4");
        await ToolAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-itsoffset", "0.25", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1.75",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-threads", "2", "-c:a", "aac", "-metadata:s:a:0", "title=Game", "-metadata:s:a:0", "handler_name=Game",
            "-metadata:s:a:1", "title=Mic delayed", "-metadata:s:a:1", "handler_name=Mic delayed", "-y", path]);
        return Identity(path);
    }
    private static ClipSource Identity(string path) { var file = new FileInfo(path); return new(path, file.Length, file.LastWriteTimeUtc.Ticks); }
    private static async Task<float[]> DecodeStereoAsync(string input, string pcm)
    {
        await ToolAsync("ffmpeg", ["-v", "error", "-nostdin", "-i", input, "-map", "0:a:0", "-ar", "48000", "-ac", "2", "-f", "f32le", pcm]);
        var bytes = await File.ReadAllBytesAsync(pcm, Token);
        return Enumerable.Range(0, bytes.Length / sizeof(float)).Select(i => BitConverter.ToSingle(bytes, i * sizeof(float))).ToArray();
    }
    private static double FirstAudibleTime(float[] samples)
    {
        var index = Array.FindIndex(samples, sample => Math.Abs(sample) > 0.01f);
        Assert.True(index >= 0, "Decoded audio must contain the selected delayed tone.");
        return index / 2 / 48000d;
    }
    private static async Task<JsonElement> InspectAsync(string path)
    {
        var text = await ToolAsync("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", path]);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    private static int AudioCount(JsonElement media) => media.GetProperty("streams").EnumerateArray().Count(s => s.GetProperty("codec_type").GetString() == "audio");
    private static double Duration(JsonElement media) => double.Parse(media.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
    private static async Task<string> ToolAsync(string name, string[] arguments)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, name + ".exe");
        Assert.SkipUnless(File.Exists(executable), name + " is not built beside the tests.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var errors = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        Assert.True(process.ExitCode == 0, await errors);
        return await output;
    }
}
