namespace ReplayBuffer.Media;

/// <summary>Per-packet flags, mirroring WASAPI's <c>AUDCLNT_BUFFERFLAGS_*</c>.</summary>
[Flags]
public enum AudioPacketFlags
{
    None = 0,

    /// <summary>The packet follows a gap in the device's stream.</summary>
    DataDiscontinuity = 1,

    /// <summary>The packet is silence; its samples are not read.</summary>
    Silent = 2,

    /// <summary>The device could not stamp the packet.</summary>
    TimestampError = 4,
}

/// <summary>Receives the contiguous output of an <see cref="AudioTimeline"/>.</summary>
public interface IAudioSampleSink
{
    /// <summary>Appends interleaved 48 kHz stereo float frames.</summary>
    void Write(ReadOnlySpan<float> interleaved);

    /// <summary>Appends <paramref name="frames"/> frames of silence.</summary>
    void WriteSilence(long frames);
}

/// <summary>
/// Turns one device's QPC-stamped packets into a contiguous 48 kHz stereo stream whose
/// frame 0 is the session anchor (AD-6). Gaps are filled with silence and overlaps trimmed
/// once they pass <see cref="ToleranceFrames"/>; data before the anchor is dropped.
/// Not thread-safe: one device thread drives it.
/// </summary>
public sealed class AudioTimeline
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>5 ms: drift inside this window is written as-is.</summary>
    public const int ToleranceFrames = SampleRate * 5 / 1000;

    private readonly long _qpcFrequency;
    private readonly long _anchorQpc;
    private readonly IAudioSampleSink _sink;

    private long _writtenFrames;
    private bool _hasPacket;
    private long _lastPacketQpc;
    private int _lastPacketFrames;
    private bool _lastWriteWasFill;

    public AudioTimeline(long qpcFrequency, long anchorQpc, IAudioSampleSink sink)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _qpcFrequency = qpcFrequency;
        _anchorQpc = anchorQpc;
        _sink = sink;
    }

    /// <summary>Frames written since the anchor.</summary>
    public long WrittenFrames => _writtenFrames;

    /// <summary>
    /// Adds one packet. <paramref name="interleaved"/> holds <paramref name="frames"/> × 2
    /// samples, or is ignored when the packet is <see cref="AudioPacketFlags.Silent"/>.
    /// </summary>
    public void Push(long qpc, int frames, ReadOnlySpan<float> interleaved, AudioPacketFlags flags)
    {
        if (frames <= 0)
        {
            return;
        }

        var silent = (flags & AudioPacketFlags.Silent) != 0;
        if (!silent && interleaved.Length < frames * Channels)
        {
            throw new ArgumentException("The packet holds fewer samples than its frame count.", nameof(interleaved));
        }

        qpc = ResolveStamp(qpc, flags);
        _hasPacket = true;
        _lastPacketQpc = qpc;
        _lastPacketFrames = frames;

        var position = FramesAt(qpc);
        var delta = position - _writtenFrames;

        // At the start, after a discontinuity, or after synthetic fill, the stamp is placed exactly.
        var exact = _writtenFrames == 0 || _lastWriteWasFill || (flags & AudioPacketFlags.DataDiscontinuity) != 0;
        var tolerance = exact ? 0 : ToleranceFrames;

        long skip = 0;
        if (delta > tolerance)
        {
            _sink.WriteSilence(delta);
            _writtenFrames += delta;
        }
        else if (delta < -tolerance)
        {
            skip = -delta;
        }

        if (skip >= frames)
        {
            return;
        }

        var keep = frames - (int)skip;
        if (silent)
        {
            _sink.WriteSilence(keep);
        }
        else
        {
            _sink.Write(interleaved.Slice((int)skip * Channels, keep * Channels));
        }

        _writtenFrames += keep;
        _lastWriteWasFill = false;
    }

    /// <summary>Writes silence up to <paramref name="qpc"/>, if the stream is behind it.</summary>
    public void FillTo(long qpc)
    {
        var target = FramesAt(qpc);
        if (target <= _writtenFrames)
        {
            return;
        }

        _sink.WriteSilence(target - _writtenFrames);
        _writtenFrames = target;
        _lastWriteWasFill = true;
    }

    /// <summary>The frame index, relative to the anchor, of a QPC instant.</summary>
    public long FramesAt(long qpc) => QpcClock.ToUnits(qpc - _anchorQpc, _qpcFrequency, SampleRate);

    private long ResolveStamp(long qpc, AudioPacketFlags flags)
    {
        var bad = qpc == 0
            || (flags & AudioPacketFlags.TimestampError) != 0
            || (_hasPacket && qpc <= _lastPacketQpc);
        if (!bad)
        {
            return qpc;
        }

        if (_hasPacket)
        {
            return _lastPacketQpc + QpcClock.ToUnits(_lastPacketFrames, SampleRate, _qpcFrequency);
        }

        // No earlier stamp to extend: the packet continues the stream.
        return _anchorQpc + QpcClock.ToUnits(_writtenFrames, SampleRate, _qpcFrequency);
    }
}
