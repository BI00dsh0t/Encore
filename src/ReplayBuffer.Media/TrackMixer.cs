namespace ReplayBuffer.Media;

/// <summary>
/// Sums a Track's devices into one stream. Each input is one device's <see cref="AudioTimeline"/>
/// output, whose frame k is k frames after the session anchor (derived from QPC), so summing by
/// position sums by QPC. Frame k of the output is the sum of every input's frame k; it is written
/// once every input has reached it, or once the leading input is more than
/// <see cref="MaxLagFrames"/> ahead, in which case the laggard counts as silence there and its late
/// frames are dropped when they arrive. With one input it passes straight through, bit-identically.
/// No gain, limiter or clipping handling (story 2.2). Thread-safe: each input is written from its
/// device's thread, under one short lock; nothing is allocated once constructed.
/// </summary>
public sealed class TrackMixer
{
    /// <summary>2 s: how far the leading input may run ahead of the slowest before the slowest is filled with silence.</summary>
    public const int MaxLagFrames = 2 * AudioTimeline.SampleRate;

    /// <summary>The most frames one input appends before the mix is advanced.</summary>
    private const int ChunkFrames = 4096;

    /// <summary>Each input's pending frames span at most the lag bound plus one chunk.</summary>
    private const int RingFrames = MaxLagFrames + ChunkFrames;

    private const int Channels = AudioTimeline.Channels;

    private readonly Lock _lock = new();
    private readonly IAudioSampleSink _output;
    private readonly IAudioSampleSink[] _inputs;
    private readonly float[][] _rings;
    private readonly long[] _written;
    private readonly float[] _mix;
    private long _emitted;

    /// <param name="inputs">The number of devices the Track mixes; at least one.</param>
    /// <param name="output">Receives the mix, typically the Track's encoder.</param>
    public TrackMixer(int inputs, IAudioSampleSink output)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputs);
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _inputs = new IAudioSampleSink[inputs];
        _written = new long[inputs];
        if (inputs == 1)
        {
            // Passthrough: the input is the output itself; nothing to align.
            _inputs[0] = output;
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

    /// <summary>Frames written to the output so far (not counted for a passthrough mixer).</summary>
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
    public long DroppedFrames { get; private set; }

    /// <summary>The sink for input <paramref name="index"/>; frame 0 is the session anchor.</summary>
    public IAudioSampleSink Input(int index) => _inputs[index];

    /// <summary>
    /// Writes the mix up to the longest input, the shorter ones padded with silence. Call once the
    /// inputs have stopped; later input past that point is mixed as usual.
    /// </summary>
    public void Flush()
    {
        if (_inputs.Length == 1)
        {
            return;
        }

        lock (_lock)
        {
            Emit(_written.Max());
        }
    }

    private void Append(int input, ReadOnlySpan<float> interleaved, long silentFrames)
    {
        lock (_lock)
        {
            var silent = interleaved.IsEmpty;
            var frames = silent ? silentFrames : interleaved.Length / Channels;
            var offset = 0L;

            // Frames the mix has already passed (written as silence for this input) are dropped.
            var late = Math.Min(frames, Math.Max(0, _emitted - _written[input]));
            if (late > 0)
            {
                DroppedFrames += late;
                _written[input] += late;
                offset = late;
            }

            var ring = _rings[input];
            while (offset < frames)
            {
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

            _output.Write(mix);
            _emitted += count;
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
