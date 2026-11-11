using Encore.Engine.ClipMedia;

namespace Encore.Engine.Tests;

/// <summary>The clip worker's peak cache writing, fed raw samples instead of FFmpeg's output.</summary>
public sealed class StereoPeaksTests
{
    [Fact]
    public async Task EachMillisecondKeepsItsLouderChannelClampedAndTheFileIsPaddedWithSilence()
    {
        // Two milliseconds of 48 kHz stereo: the first peaks at 0.5 on the right, the second at 2 (clamped to 1).
        var samples = new List<float>();
        for (var frame = 0; frame < 96; frame++)
        {
            samples.Add(frame == 10 ? -0.25f : 0.1f);
            samples.Add(frame switch { 20 => 0.5f, 60 => 2f, _ => 0f });
        }

        // Three bytes of a part frame trail the whole ones.
        var bytes = samples.SelectMany(BitConverter.GetBytes).Concat(new byte[3]).ToArray();
        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file);
        var peaks = new MillisecondPeakWriter(writer, count: 4);

        var leftover = await StereoPeaks.ReadAsync(new MemoryStream(bytes), peaks.Add, CancellationToken.None);
        peaks.Finish(CancellationToken.None);
        writer.Flush();

        Assert.Equal(3, leftover);
        var written = file.ToArray();
        Assert.Equal(new[] { 0.5f, 1f, 0f, 0f }, Enumerable.Range(0, 4).Select(i => BitConverter.ToSingle(written, i * 4)));
    }
}
