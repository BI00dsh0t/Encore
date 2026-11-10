namespace Encore.Media;

/// <summary>Sums a Track's devices into one stream by frame position, then limits it. Thread-safe; see docs/code-notes.md.</summary>
public sealed class TrackMixer
{
    /// <summary>2 s: how far the leading input may run ahead before the slowest is filled with silence.</summary>
    public const int MaxLagFrames = 2 * AudioTimeline.SampleRate;

    /// <summary>The most frames one input appends before the mix is advanced.</summary>
    private const int ChunkFrames = 4096;

    /// <summary>Each input's pending frames span at most the lag bound plus one chunk.</summary>
    private const int RingFrames = MaxLagFrames + ChunkFrames;

    private const int Channels = AudioTimeline.Channels;

    private readonly Lock _lock = new();
    private readonly PeakLimiter _limiter;
    private readonly IAudioSampleSink[] _inputs;
    private readonly float[][] _rings;
    private readonly long[] _written;
    private readonly float[] _mix;
    private long _emitted;
    private long _dropped;

    /// <summary>The inputs count is the devices the Track mixes, zero for a silent Track; output is usually its encoder.</summary>
    public TrackMixer(int inputs, IAudioSampleSink output)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputs);
        ArgumentNullException.ThrowIfNull(output);
        _limiter = new PeakLimiter(output);
        _inputs = new IAudioSampleSink[inputs];
        _written = new long[inputs];
        if (inputs <= 1)
        {
            // Nothing to align: a single input's frames go straight to the limiter, without a ring.
            if (inputs == 1)
            {
                _inputs[0] = new PassthroughInput(this);
            }

            _rings = [];
            _mix = [];
            return;
        }

        _rings = new float[inputs][];
        for (var i = 0; i < inputs; i++)
        {
            _rings[i] = new float[RingFrames * Channels];
            _inputs[i] = new MixerInput(this, i);
        }

        _mix = new float[ChunkFrames * Channels];
    }

    /// <summary>The number of inputs.</summary>
    public int Inputs => _inputs.Length;

    /// <summary>Frames written to the output so far.</summary>
    public long EmittedFrames
    {
        get
        {
            lock (_lock)
            {
                return _emitted;
            }
        }
    }

    /// <summary>Frames of late input dropped because the mix had already passed them.</summary>
    public long DroppedFrames
    {
        get
        {
            lock (_lock)
            {
                return _dropped;
            }
        }
    }

    /// <summary>Frames the limiter wrote with a gain below 1.</summary>
    public long LimitedFrames
    {
        get
        {
            lock (_lock)
            {
                return _limiter.LimitedFrames;
            }
        }
    }

    /// <summary>The frames the longest input has written, late ones included; 0 with no inputs.</summary>
    public long LongestInputFrames
    {
        get
        {
            lock (_lock)
            {
                var longest = 0L;
                foreach (var written in _written)
                {
                    longest = Math.Max(longest, written);
                }

                return longest;
            }
        }
    }

    /// <summary>The sink for input <paramref name="index"/>; frame 0 is the session anchor.</summary>
    public IAudioSampleSink Input(int index) => _inputs[index];

    /// <summary>Writes the mix up to a frame, counting any input that hasn't reached it as silence there.</summary>
    public void PadTo(long frame)
    {
        lock (_lock)
        {
            if (frame <= _emitted)
            {
                return;
            }

            if (_rings.Length == 0)
            {
                // No ring: no input, or one whose frames are all written by now; the rest is silence.
                var silence = frame - _emitted;
                _emitted = frame;
                _limiter.WriteSilence(silence);
                return;
            }

            Emit(frame);
        }
    }

    /// <summary>Writes the mix up to the longest input, padding shorter ones with silence; call once the inputs have stopped.</summary>
    public void Flush() => PadTo(LongestInputFrames);

    /// <summary>A single input: drops what the mix has passed, then writes the rest straight to the limiter.</summary>
    private void Pass(ReadOnlySpan<float> interleaved, long silentFrames)
    {
        var silent = interleaved.IsEmpty;
        var frames = silent ? silentFrames : interleaved.Length / Channels;
        lock (_lock)
        {
            var late = Math.Min(frames, Math.Max(0, _emitted - _written[0]));
            _dropped += late;
            _written[0] += late;
            var rest = frames - late;
            if (rest <= 0)
            {
                return;
            }

            // Counted before the write: a failing encoder never sees the same range twice.
            _written[0] += rest;
            _emitted = _written[0];
            if (silent)
            {
                _limiter.WriteSilence(rest);
            }
            else
            {
                _limiter.Write(interleaved[(int)(late * Channels)..]);
            }
        }
    }

    private void Append(int input, ReadOnlySpan<float> interleaved, long silentFrames)
    {
        var silent = interleaved.IsEmpty;
        var frames = silent ? silentFrames : interleaved.Length / Channels;
        var ring = _rings[input];
        var offset = 0L;

        // The lock is taken per chunk, so a long silence fill never holds the other inputs for more than one chunk.
        while (offset < frames)
        {
            lock (_lock)
            {
                // Frames the mix has already passed (written as silence for this input) are dropped.
                var late = Math.Min(frames - offset, Math.Max(0, _emitted - _written[input]));
                if (late > 0)
                {
                    _dropped += late;
                    _written[input] += late;
                    offset += late;
                    if (offset >= frames)
                    {
                        return;
                    }
                }

                var chunk = (int)Math.Min(ChunkFrames, frames - offset);
                var start = _written[input];
                for (var done = 0; done < chunk;)
                {
                    var slot = (int)((start + done) % RingFrames);
                    var run = Math.Min(chunk - done, RingFrames - slot);
                    var target = ring.AsSpan(slot * Channels, run * Channels);
                    if (silent)
                    {
                        target.Clear();
                    }
                    else
                    {
                        interleaved.Slice((int)(offset + done) * Channels, run * Channels).CopyTo(target);
                    }

                    done += run;
                }

                _written[input] = start + chunk;
                offset += chunk;
                Advance();
            }
        }
    }

    /// <summary>Emits what every input has reached, or up to the lag bound behind the leader. Under the lock.</summary>
    private void Advance()
    {
        long min = long.MaxValue, max = 0;
        foreach (var written in _written)
        {
            min = Math.Min(min, written);
            max = Math.Max(max, written);
        }

        Emit(Math.Max(min, max - MaxLagFrames));
    }

    /// <summary>Writes the sum of frames [<see cref="_emitted"/>, <paramref name="ready"/>). Under the lock.</summary>
    private void Emit(long ready)
    {
        while (_emitted < ready)
        {
            var slot = (int)(_emitted % RingFrames);
            var count = (int)Math.Min(Math.Min(ready - _emitted, ChunkFrames), RingFrames - slot);
            var mix = _mix.AsSpan(0, count * Channels);
            mix.Clear();
            for (var i = 0; i < _rings.Length; i++)
            {
                // An input behind this range is silence there; its frames will be dropped when they come.
                var have = (int)Math.Clamp(_written[i] - _emitted, 0, count);
                if (have == 0)
                {
                    continue;
                }

                var source = _rings[i].AsSpan(slot * Channels, have * Channels);
                var target = mix[..(have * Channels)];
                for (var s = 0; s < target.Length; s++)
                {
                    target[s] += source[s];
                }
            }

            // Counted before the write: a failing encoder never sees the same range twice.
            _emitted += count;
            _limiter.Write(mix);
        }
    }

    private sealed class PassthroughInput(TrackMixer mixer) : IAudioSampleSink
    {
        public void Write(ReadOnlySpan<float> interleaved)
        {
            if (interleaved.Length >= Channels)
            {
                mixer.Pass(interleaved[..(interleaved.Length / Channels * Channels)], 0);
            }
        }

        public void WriteSilence(long frames)
        {
            if (frames > 0)
            {
                mixer.Pass([], frames);
            }
        }
    }

    private sealed class MixerInput(TrackMixer mixer, int index) : IAudioSampleSink
    {
        public void Write(ReadOnlySpan<float> interleaved)
        {
            if (interleaved.Length >= Channels)
            {
                mixer.Append(index, interleaved[..(interleaved.Length / Channels * Channels)], 0);
            }
        }

        public void WriteSilence(long frames)
        {
            if (frames > 0)
            {
                mixer.Append(index, [], frames);
            }
        }
    }
}

/// <summary>Writes one stream to several sinks: a device shared by several Tracks feeds each Track's mixer.</summary>
public sealed class AudioFanOut(IReadOnlyList<IAudioSampleSink> sinks) : IAudioSampleSink
{
    private readonly IAudioSampleSink[] _sinks = [.. sinks];

    public void Write(ReadOnlySpan<float> interleaved)
    {
        foreach (var sink in _sinks)
        {
            sink.Write(interleaved);
        }
    }

    public void WriteSilence(long frames)
    {
        foreach (var sink in _sinks)
        {
            sink.WriteSilence(frames);
        }
    }
}
