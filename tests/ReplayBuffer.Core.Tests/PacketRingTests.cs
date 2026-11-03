using ReplayBuffer.Core.Replay;

namespace ReplayBuffer.Core.Tests;

public sealed class PacketRingTests
{
    private const int VideoTimeBase = 90_000;
    private const int AudioTimeBase = 48_000;
    private const int Fps = 30;
    private const int AudioFrame = 1024;
    private const long Second = 1_000_000;
    private const long AacFrameUs = AudioFrame * Second / AudioTimeBase;
    private const long HalfAacFrameUs = (AacFrameUs / 2) + 1;

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

        // The window starts at 18.4 s: the first keyframe in it is 19 s, and the last frame starts before the press.
        Assert.Equal(19 * Second, cut.StartUs);
        Assert.Equal(press, cut.EndUs);
        var videoPackets = cut.Packets.Where(p => p.Stream == video).ToList();
        Assert.True(videoPackets[0].IsKey);
        Assert.Equal(19 * VideoTimeBase, cut.Origin(video));
        Assert.Equal(19 * VideoTimeBase, videoPackets[0].Pts);
        Assert.InRange(videoPackets[^1].TimeUs, press - (Second / Fps) - 1, press - 1);

        // Audio covers the same span, each end at the AAC boundary nearest the video's.
        var audioPackets = cut.Packets.Where(p => p.Stream == audio).ToList();
        Assert.InRange(audioPackets[0].TimeUs, cut.StartUs - HalfAacFrameUs, cut.StartUs + HalfAacFrameUs);
        Assert.InRange(audioPackets[^1].TimeUs + AacFrameUs, press - HalfAacFrameUs, press + HalfAacFrameUs);
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
        Assert.All(cut.Packets, packet => Assert.True(packet.ToArray().AsSpan().SequenceEqual(Pattern(packet.Stream, packet.Pts, packet.Length)), $"packet {packet.Stream}@{packet.Pts} changed"));

        cut.Dispose();
        Assert.Equal(0, ring.LiveSlabs);
    }

    [Fact]
    public void TwoCutsThatShareTheFirstPressesKeyframeHoldEveryPacketOnce()
    {
        using var ring = new PacketRing(60 * Second, capacity: 256, slabBytes: 64 * 1024);
        var (video, audio) = Streams(ring);

        // Presses at 10.02 s and 20.02 s force a keyframe on the next frame: 301 (10.033 s) and 601.
        const long press1 = 10_020_000, press2 = 20_020_000;
        FeedLagging(ring, video, audio, frames: 21 * Fps, forcedKeys: [301, 601]);
        var key1 = ring.ToMicros(video, 301L * VideoTimeBase / Fps);
        var key2 = ring.ToMicros(video, 601L * VideoTimeBase / Fps);

        using var first = ring.Cut(press1 - (60 * Second), key1)!;
        using var second = ring.Cut(Math.Max(press2 - (60 * Second), key1), key2)!;

        Assert.Equal(0, first.StartUs);
        Assert.Equal(key1, second.StartUs);
        Assert.Equal(10 * Second, second.EndUs - second.StartUs);

        // Together they hold every video frame before the second key and every audio packet centred before it, each once.
        var both = first.Packets.Concat(second.Packets).ToList();
        Assert.Equal(Enumerable.Range(0, 601).Select(f => (long)f * VideoTimeBase / Fps), both.Where(p => p.Stream == video).Select(p => p.Pts));
        var audioPts = new List<long>();
        for (long pts = 0; (pts * Second / AudioTimeBase) + (AacFrameUs / 2) < key2; pts += AudioFrame)
        {
            audioPts.Add(pts);
        }

        Assert.Equal(audioPts, both.Where(p => p.Stream == audio).Select(p => p.Pts));

        // Each cut's audio spans its video within one AAC frame.
        foreach (var cut in new[] { first, second })
        {
            var sound = cut.Packets.Where(p => p.Stream == audio).ToList();
            Assert.InRange(sound[0].TimeUs - cut.StartUs, -AacFrameUs, AacFrameUs);
            Assert.InRange(sound[^1].TimeUs + AacFrameUs - cut.EndUs, -AacFrameUs, AacFrameUs);
        }
    }

    internal static (int Video, int Audio) Streams(PacketRing ring) =>
        (ring.AddStream(1, VideoTimeBase, isVideo: true), ring.AddStream(1, AudioTimeBase, isVideo: false));

    /// <summary>Video at 30 fps with a keyframe each second, and AAC-sized audio packets, interleaved in time.</summary>
    internal static void Feed(PacketRing ring, int video, int audio, int seconds, int videoBytes, int fromSecond = 0)
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

    /// <summary>
    /// Video at 30 fps, a keyframe each second and at <paramref name="forcedKeys"/>, with AAC-sized
    /// audio appended 100 ms behind it, as the encoders lag; then the audio's last 100 ms.
    /// </summary>
    private static void FeedLagging(PacketRing ring, int video, int audio, int frames, long[] forcedKeys)
    {
        const long lagUs = 100_000;
        long audioPts = 0;
        for (long frame = 0; frame < frames; frame++)
        {
            var frameUs = frame * Second / Fps;
            for (; (audioPts * Second / AudioTimeBase) <= frameUs - lagUs; audioPts += AudioFrame)
            {
                ring.Append(audio, Pattern(audio, audioPts, 300), audioPts, audioPts, AudioFrame, isKey: true);
            }

            var videoPts = frame * VideoTimeBase / Fps;
            ring.Append(video, Pattern(video, videoPts, 2048), videoPts, videoPts, VideoTimeBase / Fps, isKey: frame % Fps == 0 || forcedKeys.Contains(frame));
        }

        for (; (audioPts * Second / AudioTimeBase) < (long)frames * Second / Fps; audioPts += AudioFrame)
        {
            ring.Append(audio, Pattern(audio, audioPts, 300), audioPts, audioPts, AudioFrame, isKey: true);
        }
    }

    internal static byte[] Pattern(int stream, long pts, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((pts * 31) + (stream * 7) + i);
        }

        return bytes;
    }
}
