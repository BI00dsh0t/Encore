using ReplayBuffer.Core.Audio;
using ReplayBuffer.Engine.Audio;
using ReplayBuffer.Media;

namespace ReplayBuffer.Engine.Tests;

public sealed class AudioTimelineTapTests
{
    private const long Frequency = 10_000_000;
    private const int BlockFrames = 480; // 10 ms
    private const long BlockTicks = BlockFrames * Frequency / AudioFormat.SampleRate;

    [Fact]
    public void BlocksHeldBeforePublishAreReplayedSoTheStreamStartsAtTheAnchorWithNoFill()
    {
        const long anchor = 1_000 * Frequency;
        var tap = new AudioTimelineTap(holdFrames: AudioFormat.SampleRate);
        var sink = new RecordingSink();
        var timeline = new AudioTimeline(Frequency, anchor, sink);

        // Two blocks before the anchor and two after it arrive before the timeline is published.
        for (var k = 0; k < 4; k++)
        {
            Push(tap, anchor + ((k - 2) * BlockTicks), marker: k);
        }

        tap.Publish(timeline);
        Push(tap, anchor + (2 * BlockTicks), marker: 4);

        // From the anchor on: blocks 2, 3 and 4, contiguous, with no gap fill.
        Assert.Equal(3 * BlockFrames, timeline.WrittenFrames);
        Assert.Equal(0, timeline.GapFillFrames);
        Assert.Equal(0, sink.SilentFrames);
        float[] expected = [.. Enumerable.Range(2, 3).SelectMany(m => Enumerable.Repeat((float)m, BlockFrames * AudioFormat.Channels))];
        Assert.Equal(expected, sink.Samples);
    }

    private static void Push(AudioTimelineTap tap, long qpc, int marker)
    {
        var samples = new float[BlockFrames * AudioFormat.Channels];
        Array.Fill(samples, marker);
        tap.OnPcm(new AudioBlock(qpc, BlockFrames, samples));
    }

    private sealed class RecordingSink : IAudioSampleSink
    {
        public List<float> Samples { get; } = [];

        public long SilentFrames { get; private set; }

        public void Write(ReadOnlySpan<float> interleaved) => Samples.AddRange(interleaved);

        public void WriteSilence(long frames) => SilentFrames += frames;
    }
}
