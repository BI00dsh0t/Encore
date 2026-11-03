using ReplayBuffer.Core.Replay;

namespace ReplayBuffer.Core.Tests;

public sealed class PacketRingTests
{
    private const int VideoTimeBase = 90_000;
    private const int AudioTimeBase = 48_000;
    private const int Fps = 30;
    private const int AudioFrame = 1024;
    private const long Second = 1_000_000;

    [Fact]
    public void ARingPastItsWindowIsTrimmedAndACutRunsFromTheFirstKeyframeInTheWindowToThePress()
    {
        using var ring = new PacketRing(10 * Second, capacity: 64, slabBytes: 64 * 1024);
        var (video, audio) = Streams(ring);

        Feed(ring, video, audio, seconds: 30, videoBytes: 2048);

        // The window plus 2 s of slack is held, and the slabs below it are freed.
        Assert.InRange(ring.OldestUs, (30 - 12 - 1) * Second, (30 - 12 + 1) * Second);
        Assert.True(ring.LiveSlabs < 20, $"{ring.LiveSlabs} slabs are still allocated");

        var press = (28 * Second) + 400_000;
        using var cut = ring.Cut(press - (10 * Second), press)!;

        // The window starts at 18.4 s: the first keyframe in it is 19 s, and the last frame is at or before the press.
        Assert.Equal(19 * Second, cut.StartUs);
        var videoPackets = cut.Packets.Where(p => p.Stream == video).ToList();
        Assert.True(videoPackets[0].IsKey);
        Assert.Equal(19 * VideoTimeBase, cut.Origin(video));
        Assert.Equal(19 * VideoTimeBase, videoPackets[0].Pts);
        Assert.InRange(videoPackets[^1].TimeUs, press - (Second / Fps), press);

        // Audio covers the same span, from the packet holding the keyframe's time to the one holding the press.
        var audioPackets = cut.Packets.Where(p => p.Stream == audio).ToList();
        Assert.InRange(audioPackets[0].TimeUs, cut.StartUs - 21_334, cut.StartUs);
        Assert.InRange(audioPackets[^1].TimeUs, press - 21_334, press);
        Assert.Equal(19 * AudioTimeBase, cut.Origin(audio));
        Assert.Equal(cut.Packets.OrderBy(p => p.TimeUs).Select(p => p.TimeUs), cut.Packets.Select(p => p.TimeUs));
    }

    [Fact]
    public void ACutsBytesStayIntactAfterTheRingTrimsPastThemAndIsDisposed()
    {
        var ring = new PacketRing(2 * Second, capacity: 16, slabBytes: 64 * 1024);
        var (video, audio) = Streams(ring);
        Feed(ring, video, audio, seconds: 5, videoBytes: 3000);
        var cut = ring.Cut(2 * Second, 4 * Second)!;

        // Long past the cut, then gone: the pinned slabs stay until the cut is disposed.
        Feed(ring, video, audio, seconds: 20, videoBytes: 3000, fromSecond: 5);
        Assert.True(ring.OldestUs > 4 * Second);
        ring.Dispose();
        Assert.True(ring.LiveSlabs > 0);

        Assert.NotEmpty(cut.Packets);
        Assert.All(cut.Packets, packet => Assert.True(packet.Bytes.SequenceEqual(Pattern(packet.Stream, packet.Pts, packet.Length)), $"packet {packet.Stream}@{packet.Pts} changed"));

        cut.Dispose();
        Assert.Equal(0, ring.LiveSlabs);
    }

    private static (int Video, int Audio) Streams(PacketRing ring) =>
        (ring.AddStream(1, VideoTimeBase, isVideo: true), ring.AddStream(1, AudioTimeBase, isVideo: false));

    /// <summary>Video at 30 fps with a keyframe each second, and AAC-sized audio packets, interleaved in time.</summary>
    private static void Feed(PacketRing ring, int video, int audio, int seconds, int videoBytes, int fromSecond = 0)
    {
        long frame = (long)fromSecond * Fps;
        long audioPts = (long)fromSecond * AudioTimeBase / AudioFrame * AudioFrame;
        var endUs = (long)(fromSecond + seconds) * Second;
        while (frame * Second / Fps < endUs)
        {
            var videoPts = frame * VideoTimeBase / Fps;
            while (audioPts * Second / AudioTimeBase <= frame * Second / Fps)
            {
                ring.Append(audio, Pattern(audio, audioPts, 300), audioPts, audioPts, AudioFrame, isKey: true);
                audioPts += AudioFrame;
            }

            ring.Append(video, Pattern(video, videoPts, videoBytes), videoPts, videoPts, VideoTimeBase / Fps, isKey: frame % Fps == 0);
            frame++;
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
