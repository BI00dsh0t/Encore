using Encore.Core.Audio;

namespace Encore.Media;

/// <summary>Turns one device's QPC-stamped packets into a contiguous stream whose frame 0 is the session anchor.</summary>
public sealed class AudioTimeline
{
    public const int SampleRate = AudioFormat.SampleRate;
    public const int Channels = AudioFormat.Channels;

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

    /// <summary>Packets placed after a gap of more than the tolerance, which was filled with silence.</summary>
    public long GapFills { get; private set; }

    /// <summary>Silent frames written by <see cref="GapFills"/>.</summary>
    public long GapFillFrames { get; private set; }

    /// <summary>Packets trimmed because they overlapped the stream, or began before the anchor, by more than the tolerance.</summary>
    public long Trims { get; private set; }

    /// <summary>Frames dropped by <see cref="Trims"/>.</summary>
    public long TrimmedFrames { get; private set; }

    /// <summary>Silent frames written by <see cref="FillTo"/> (idle or failed device).</summary>
    public long IdleFillFrames { get; private set; }

    /// <summary>Packets whose stamp was missing, flagged bad or not after the last one, and so extrapolated.</summary>
    public long ResolvedStamps { get; private set; }

    /// <summary>Adds one packet; the samples are ignored when the packet is flagged silent.</summary>
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
        var skip = Place(delta, frames, exact);
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

    /// <summary>Fills a gap before the packet with silence, or returns the frames to trim where it overlaps the stream.</summary>
    private long Place(long delta, int frames, bool exact)
    {
        var tolerance = exact ? 0 : ToleranceFrames;

        // Exact placement (start, idle resume) is normal; only a step past the tolerance is a correction.
        var correction = Math.Abs(delta) > ToleranceFrames;
        if (delta > tolerance)
        {
            _sink.WriteSilence(delta);
            _writtenFrames += delta;
            if (correction)
            {
                GapFills++;
                GapFillFrames += delta;
            }

            return 0;
        }

        if (delta >= -tolerance)
        {
            return 0;
        }

        var skip = -delta;
        if (correction)
        {
            Trims++;
            TrimmedFrames += Math.Min(skip, frames);
        }

        return skip;
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
        IdleFillFrames += target - _writtenFrames;
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

        ResolvedStamps++;

        if (_hasPacket)
        {
            return _lastPacketQpc + QpcClock.ToUnits(_lastPacketFrames, SampleRate, _qpcFrequency);
        }

        // No earlier stamp to extend: the packet continues the stream.
        return _anchorQpc + QpcClock.ToUnits(_writtenFrames, SampleRate, _qpcFrequency);
    }
}
