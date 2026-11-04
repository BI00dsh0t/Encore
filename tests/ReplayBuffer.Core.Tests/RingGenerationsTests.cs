using System.Text.Json;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Replay;

namespace ReplayBuffer.Core.Tests;

public sealed class RingGenerationsTests
{
    private const int VideoTimeBase = 90_000;
    private const int AudioTimeBase = 48_000;
    private const int Fps = 30;
    private const int AudioFrame = 1024;
    private const long Second = 1_000_000;

    [Fact]
    public void AThirtySecondWindowHoldsNoMoreThanThirtySecondsAndATwoHourWindowIsAccepted()
    {
        using (var generations = new RingGenerations(NewRing(30)))
        {
            Streams(generations);
            Feed(generations, 0, 40 * Second);
            var press = 40 * Second;
            var (cut, _) = generations.Cut(press - (30 * Second), press)!.Value;
            using (cut)
            {
                Assert.True(cut.EndUs - cut.StartUs <= 30 * Second);
            }
        }

        Assert.True(Keys.BufferWindow.IsValid(JsonSerializer.SerializeToElement(7200)));
        using var twoHours = new RingGenerations(NewRing(30));
        Streams(twoHours);
        Feed(twoHours, 0, 5 * Second);
        twoHours.Resize(NewRing(7200), 2 * Second);
        Assert.Equal(7200 * Second, twoHours.Ring.WindowUs);
        Feed(twoHours, 5 * Second, 8 * Second);
        var (big, _) = twoHours.Cut(2 * Second, 8 * Second)!.Value;
        using (big)
        {
            Assert.Equal(2 * Second, big.StartUs);
            Assert.NotEmpty(big.Packets);
        }
    }

    [Fact]
    public void ShrinkingWhileAClipWritesKeepsItsBytesAndFreesTheOldRingOnceItIsDone()
    {
        var old = NewRing(60);
        using var generations = new RingGenerations(old);
        Streams(generations);
        Feed(generations, 0, 20 * Second);
        var (a, _) = generations.Cut(5 * Second, 15 * Second)!.Value;

        // The Reset at A's end resizes to 30 s; the encoders keep appending.
        Assert.Equal(2, generations.Resize(NewRing(30), a.EndUs));
        Feed(generations, 20 * Second, 25 * Second);

        Assert.All(a.Packets, p => Assert.True(p.ToArray().AsSpan().SequenceEqual(Pattern(p.Stream, p.Pts, p.Length)), $"packet {p.Stream}@{p.Pts} changed"));
        Assert.True(old.LiveSlabs > 0);
        var end = a.EndUs;
        a.Dispose();
        Assert.Equal(0, old.LiveSlabs);

        // Gapless: the next Clip starts exactly where A ended.
        var (next, generation) = generations.Cut(end, 22 * Second)!.Value;
        using (next)
        {
            Assert.Equal(2, generation);
            Assert.Equal(end, next.StartUs);
        }
    }

    private static PacketRing NewRing(int seconds) =>
        new(seconds * Second, capacity: 256, slabBytes: 64 * 1024);

    private static void Streams(RingGenerations generations)
    {
        generations.AddStream(1, VideoTimeBase, isVideo: true);
        generations.AddStream(1, AudioTimeBase, isVideo: false);
    }

    /// <summary>
    /// Video (stream 0) at 30 fps with a keyframe each second and on the first frame, and AAC-sized
    /// audio (stream 1), interleaved, over <c>[fromUs, toUs)</c>.
    /// </summary>
    private static void Feed(RingGenerations generations, long fromUs, long toUs)
    {
        var first = ((fromUs * Fps) + Second - 1) / Second;
        long audioPts = fromUs * AudioTimeBase / Second / AudioFrame * AudioFrame;
        for (var frame = first; frame * Second / Fps < toUs; frame++)
        {
            var frameUs = frame * Second / Fps;
            for (; audioPts * Second / AudioTimeBase <= frameUs; audioPts += AudioFrame)
            {
                generations.Append(1, Pattern(1, audioPts, 300), audioPts, audioPts, AudioFrame, isKey: true);
            }

            var videoPts = frame * VideoTimeBase / Fps;
            generations.Append(0, Pattern(0, videoPts, 2048), videoPts, videoPts, VideoTimeBase / Fps, isKey: frame % Fps == 0 || frame == first);
        }
    }

    private static byte[] Pattern(int stream, long pts, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((pts * 31) + (stream * 7) + i);
        }

        return bytes;
    }
}
