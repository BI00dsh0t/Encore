namespace Encore.Media;

/// <summary>A session's mic mute over time: the state at Start, then each change with the QPC it applies from. Thread-safe.</summary>
public sealed class MicMuteSchedule
{
    private readonly Lock _writer = new();

    /// <summary>The changes in QPC order; the first is the Start state, from the beginning of time.</summary>
    private (long Qpc, bool Muted)[] _changes;

    /// <summary>The muted argument is the mute at Start, in place from the first frame.</summary>
    public MicMuteSchedule(bool muted) => _changes = [(long.MinValue, muted)];

    /// <summary>The latest state.</summary>
    public bool Muted => Volatile.Read(ref _changes)[^1].Muted;

    /// <summary>The log, read once per run by a gate.</summary>
    internal (long Qpc, bool Muted)[] Changes => Volatile.Read(ref _changes);

    /// <summary>Sets the mute from a QPC time on; a time earlier than the last change's is taken as that change's.</summary>
    public void Set(bool muted, long qpc)
    {
        lock (_writer)
        {
            var current = _changes;
            var last = current[^1];
            if (last.Muted == muted)
            {
                return;
            }

            var next = new (long Qpc, bool Muted)[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = (Math.Max(qpc, last.Qpc), muted);
            Volatile.Write(ref _changes, next);
        }
    }
}

/// <summary>Silences one mic's input to one Track while the schedule says muted, before the mix. One device thread drives it.</summary>
public sealed class MicMuteGate : IAudioSampleSink
{
    private const int Channels = AudioTimeline.Channels;

    private readonly IAudioSampleSink _inner;
    private readonly MicMuteSchedule _schedule;
    private readonly long _qpcFrequency;
    private readonly long _anchorQpc;
    private long _position;

    /// <summary>The inner sink is the Track's mixer input for this device; anchorQpc is the session anchor, frame 0.</summary>
    public MicMuteGate(IAudioSampleSink inner, MicMuteSchedule schedule, long qpcFrequency, long anchorQpc)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _inner = inner;
        _schedule = schedule;
        _qpcFrequency = qpcFrequency;
        _anchorQpc = anchorQpc;
    }

    /// <summary>Frames passed on so far, silence included.</summary>
    public long Position => _position;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / Channels;
        var offset = 0;
        while (offset < frames)
        {
            var (muted, until) = StateAt(_position);
            var run = (int)Math.Min(frames - offset, until - _position);
            _position += run;
            if (muted)
            {
                _inner.WriteSilence(run);
            }
            else
            {
                _inner.Write(interleaved.Slice(offset * Channels, run * Channels));
            }

            offset += run;
        }
    }

    public void WriteSilence(long frames)
    {
        if (frames <= 0)
        {
            return;
        }

        _position += frames;
        _inner.WriteSilence(frames);
    }

    /// <summary>The mute at a frame, and the frame the next change applies from (<see cref="long.MaxValue"/> for none).</summary>
    private (bool Muted, long Until) StateAt(long frame)
    {
        var changes = _schedule.Changes;
        var until = long.MaxValue;
        for (var i = changes.Length - 1; i > 0; i--)
        {
            var from = FrameOf(changes[i].Qpc);
            if (from <= frame)
            {
                return (changes[i].Muted, until);
            }

            until = from;
        }

        return (changes[0].Muted, until);
    }

    /// <summary>The first frame at or after a QPC time, in a stream whose frame 0 is the anchor; 0 at or before the anchor.</summary>
    public static long FrameFrom(long qpc, long anchorQpc, long qpcFrequency)
    {
        if (qpc <= anchorQpc)
        {
            return 0;
        }

        var scaled = (Int128)(qpc - anchorQpc) * AudioTimeline.SampleRate;
        var frame = scaled / qpcFrequency;
        return (long)(scaled % qpcFrequency == 0 ? frame : frame + 1);
    }

    private long FrameOf(long qpc) => FrameFrom(qpc, _anchorQpc, _qpcFrequency);
}
