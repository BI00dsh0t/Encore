namespace Encore.Media;

/// <summary>
/// A session's mic mute over time: its state at Start, then each change with the QPC it applies
/// from (AD-13). Thread-safe: <see cref="Set"/> comes from the orchestrator thread and swaps an
/// immutable log, which the audio threads' <see cref="MicMuteGate"/>s read with one
/// <see cref="Volatile.Read{T}(ref T)"/> per run, without a lock.
/// </summary>
public sealed class MicMuteSchedule
{
    private readonly Lock _writer = new();

    /// <summary>The changes in QPC order; the first is the Start state, from the beginning of time.</summary>
    private (long Qpc, bool Muted)[] _changes;

    /// <param name="muted">The mute at Start, in place from the first frame.</param>
    public MicMuteSchedule(bool muted) => _changes = [(long.MinValue, muted)];

    /// <summary>The latest state.</summary>
    public bool Muted => Volatile.Read(ref _changes)[^1].Muted;

    /// <summary>The log, read once per run by a gate.</summary>
    internal (long Qpc, bool Muted)[] Changes => Volatile.Read(ref _changes);

    /// <summary>
    /// Sets the mute from <paramref name="qpc"/> on. A set to the current state changes nothing; a
    /// QPC earlier than the last change's is taken as that change's, so the log stays in order.
    /// </summary>
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

/// <summary>
/// Silences one Mic Device's input to one <c>muteAffected</c> Track while its
/// <see cref="MicMuteSchedule"/> says muted, before the mix (AD-13). It sits between a device's
/// <see cref="AudioTimeline"/> (whose output frame k is k frames after the anchor, AD-6) and the
/// Track's mixer input, counts its frame position from 0, and maps each change to its first frame
/// at or after the change's QPC (a change at or before the anchor applies from frame 0). Muted runs
/// go on as <see cref="IAudioSampleSink.WriteSilence"/>, the rest unchanged; frames already passed
/// on are never revisited. Driven by one device thread; nothing is allocated or locked per write.
/// </summary>
public sealed class MicMuteGate : IAudioSampleSink
{
    private const int Channels = AudioTimeline.Channels;

    private readonly IAudioSampleSink _inner;
    private readonly MicMuteSchedule _schedule;
    private readonly long _qpcFrequency;
    private readonly long _anchorQpc;
    private long _position;

    /// <param name="inner">The Track's mixer input for this device.</param>
    /// <param name="schedule">The session's mic mute.</param>
    /// <param name="qpcFrequency">QPC ticks per second.</param>
    /// <param name="anchorQpc">The session anchor: frame 0.</param>
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

    /// <summary>The mute at <paramref name="frame"/>, and the frame the next change applies from (<see cref="long.MaxValue"/> for none).</summary>
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

    /// <summary>
    /// The first frame at or after <paramref name="qpc"/> of a stream whose frame 0 is
    /// <paramref name="anchorQpc"/>: ceil((qpc − anchor) · 48000 / frequency); 0 at or before the anchor.
    /// </summary>
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
