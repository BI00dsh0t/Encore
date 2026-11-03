namespace ReplayBuffer.Media.Tests;

/// <summary>Records what a timeline writes, one entry per frame: the left sample, or NaN for silence.</summary>
internal sealed class RecordingSink : IAudioSampleSink
{
    public List<float> Frames { get; } = [];

    public void Write(ReadOnlySpan<float> interleaved)
    {
        for (var i = 0; i < interleaved.Length; i += 2)
        {
            Frames.Add(interleaved[i]);
        }
    }

    public void WriteSilence(long frames)
    {
        for (long i = 0; i < frames; i++)
        {
            Frames.Add(float.NaN);
        }
    }

    public int SilentFrames => Frames.Count(float.IsNaN);
}

public sealed class AudioTimelineTests
{
    private const long Qpf = 10_000_000; // a typical QPC frequency
    private const long Anchor = 5_000_000_000;

    private readonly RecordingSink _sink = new();
    private readonly AudioTimeline _timeline;

    public AudioTimelineTests() => _timeline = new AudioTimeline(Qpf, Anchor, _sink);

    /// <summary>The QPC of a frame position relative to the anchor.</summary>
    private static long At(long frames) => Anchor + (frames * Qpf / AudioTimeline.SampleRate);

    /// <summary>A packet whose frames carry <paramref name="value"/>, then +1, +2…</summary>
    private static float[] Packet(int frames, float value)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[2 * i] = value + i;
            samples[(2 * i) + 1] = value + i;
        }

        return samples;
    }

    private void Push(long frame, int frames, float value, AudioPacketFlags flags = AudioPacketFlags.None) =>
        _timeline.Push(At(frame), frames, Packet(frames, value), flags);

    [Fact]
    public void ContiguousPacketsAreWrittenAsTheyCome()
    {
        Push(0, 480, 0);
        Push(480, 480, 480);
        Push(960, 480, 960);

        Assert.Equal(1440, _sink.Frames.Count);
        Assert.Equal(0, _sink.SilentFrames);
        Assert.Equal(Enumerable.Range(0, 1440).Select(i => (float)i), _sink.Frames);
    }

    [Fact]
    public void AFirstPacketAfterTheAnchorIsPrecededBySilence()
    {
        Push(100, 480, 0);

        Assert.Equal(100, _sink.SilentFrames);
        Assert.Equal(580, _sink.Frames.Count);
        Assert.Equal(0f, _sink.Frames[100]);
    }

    [Fact]
    public void PreAnchorDataIsTrimmed()
    {
        Push(-200, 480, 0);

        Assert.Equal(280, _sink.Frames.Count);
        Assert.Equal(200f, _sink.Frames[0]); // the frame stamped at the anchor
        Assert.Equal(0, _sink.SilentFrames);
    }

    [Fact]
    public void AGapBeyondFiveMillisecondsIsFilledWithSilence()
    {
        Push(0, 480, 0);
        Push(480 + 480, 480, 1000); // 10 ms late

        Assert.Equal(1440, _sink.Frames.Count);
        Assert.Equal(480, _sink.SilentFrames);
        Assert.Equal(1000f, _sink.Frames[960]);
    }

    [Fact]
    public void AnOverlapBeyondFiveMillisecondsIsTrimmed()
    {
        Push(0, 480, 0);
        Push(480 - 360, 480, 1000); // 7.5 ms early

        Assert.Equal(600, _sink.Frames.Count);
        Assert.Equal(1360f, _sink.Frames[480]); // the overlapping 360 frames were dropped
        Assert.Equal(0, _sink.SilentFrames);
    }

    [Fact]
    public void DriftWithinFiveMillisecondsIsWrittenAsIs()
    {
        Push(0, 480, 0);
        Push(480 + 200, 480, 1000); // 4.2 ms late
        Push(960 + 200 - 400, 480, 2000); // then 8.3 ms early against the stamp, 4.2 ms against the stream

        Assert.Equal(1440, _sink.Frames.Count);
        Assert.Equal(0, _sink.SilentFrames);
        Assert.Equal(1000f, _sink.Frames[480]);
        Assert.Equal(2000f, _sink.Frames[960]);
    }

    [Fact]
    public void ADiscontinuityIsAGapEvenInsideTheTolerance()
    {
        Push(0, 480, 0);
        Push(480 + 100, 480, 1000, AudioPacketFlags.DataDiscontinuity);

        Assert.Equal(100, _sink.SilentFrames);
        Assert.Equal(1000f, _sink.Frames[580]);
    }

    [Theory]
    [InlineData(AudioPacketFlags.TimestampError)]
    [InlineData(AudioPacketFlags.None)]
    public void ABadStampUsesThePreviousStampPlusItsFrames(AudioPacketFlags flags)
    {
        Push(0, 480, 0);

        // A TIMESTAMP_ERROR packet carries a stamp far off; a zero stamp is bad on its own.
        var stamp = flags == AudioPacketFlags.TimestampError ? At(48_000) : 0;
        _timeline.Push(stamp, 480, Packet(480, 480), flags);

        Assert.Equal(960, _sink.Frames.Count);
        Assert.Equal(0, _sink.SilentFrames);
        Assert.Equal(480f, _sink.Frames[480]);
    }

    [Fact]
    public void ABadFirstStampContinuesFromTheAnchor()
    {
        _timeline.Push(0, 480, Packet(480, 0), AudioPacketFlags.None);

        Assert.Equal(480, _sink.Frames.Count);
        Assert.Equal(0f, _sink.Frames[0]);
    }

    [Fact]
    public void SilentPacketsAreWrittenAsSilenceWithoutReadingSamples()
    {
        _timeline.Push(At(0), 480, ReadOnlySpan<float>.Empty, AudioPacketFlags.Silent);

        Assert.Equal(480, _sink.SilentFrames);
    }

    [Fact]
    public void IdleFillWritesSilenceUpToTheGivenInstant()
    {
        _timeline.FillTo(At(4800));
        _timeline.FillTo(At(9600));
        _timeline.FillTo(At(9000)); // already covered: nothing

        Assert.Equal(9600, _sink.Frames.Count);
        Assert.Equal(9600, _sink.SilentFrames);
        Assert.Equal(9600, _timeline.WrittenFrames);
    }

    [Fact]
    public void IdleFillKeepsTheStreamContinuousBetweenPackets()
    {
        Push(0, 480, 0);
        _timeline.FillTo(At(4800));
        Push(4800, 480, 1000);

        Assert.Equal(5280, _sink.Frames.Count);
        Assert.Equal(4320, _sink.SilentFrames);
        Assert.Equal(1000f, _sink.Frames[4800]);
    }

    [Fact]
    public void ALatePacketInsideFilledSilenceIsTrimmedWithNoGap()
    {
        _timeline.FillTo(At(4800));
        Push(4800 - 120, 480, 1000); // 2.5 ms into the fill: inside the tolerance, still trimmed exactly

        Assert.Equal(4800 + 360, _sink.Frames.Count);
        Assert.Equal(4800, _sink.SilentFrames);
        Assert.Equal(1120f, _sink.Frames[4800]);
    }

    [Fact]
    public void APacketJustAfterFilledSilenceIsPlacedExactly()
    {
        _timeline.FillTo(At(4800));
        Push(4800 + 50, 480, 1000);

        Assert.Equal(4850, _sink.SilentFrames);
        Assert.Equal(1000f, _sink.Frames[4850]);
    }

    [Fact]
    public void FramesAtRoundsToTheNearestFrame()
    {
        Assert.Equal(0, _timeline.FramesAt(Anchor));
        Assert.Equal(48_000, _timeline.FramesAt(Anchor + Qpf));
        Assert.Equal(-48_000, _timeline.FramesAt(Anchor - Qpf));
        Assert.Equal(1, _timeline.FramesAt(Anchor + 150)); // 0.72 of a frame
    }
}

public sealed class QpcClockTests
{
}
