using Encore.Core.Audio;

namespace Encore.Media;

/// <summary>
/// One device's stream (AD-13, AD-20): turns its QPC-stamped packets into contiguous, QPC-locked
/// 48 kHz stereo blocks for a sink. Output frame k is at <c>origin + k / 48 000 s</c>, where the
/// origin is the first packet's stamp (or the first <see cref="FillTo"/>).
/// <list type="bullet">
/// <item>Stamps that are zero, flagged bad or not after the previous one are extrapolated from the last packet.</item>
/// <item>The first packet, a packet after a discontinuity, and the first packet after silence fill are placed exactly.</item>
/// <item>Otherwise one <see cref="AdaptiveResampler"/> is steered by a PI loop on the error between where the
/// packet's first frame comes out and its stamp, the ratio clamped to ±<see cref="MaxRatioDeviation"/>.
/// An error past <see cref="ToleranceFrames"/> (an unflagged gap, a stall, an overlap) is a step
/// correction: silence inserted, or the packet trimmed.</item>
/// </list>
/// Allocation-free after construction. Not thread-safe: one device thread drives it; counters may be
/// read from elsewhere, as diagnostics.
/// </summary>
public sealed class DeviceStream
{
    /// <summary>5 ms: an error past this is corrected by a step, not by steering.</summary>
    public const int ToleranceFrames = AudioFormat.SampleRate * 5 / 1000;

    /// <summary>The steering ratio stays within 1 ± 10⁻³.</summary>
    public const double MaxRatioDeviation = 1e-3;

    /// <summary>Proportional gain, per sample of error: with <see cref="Ki"/>, a critically damped loop at ω = 1 rad/s.</summary>
    public const double Kp = 2.0 / AudioFormat.SampleRate;

    /// <summary>Integral gain, per sample-second of error.</summary>
    public const double Ki = 1.0 / AudioFormat.SampleRate;

    /// <summary>The steering error is recorded from this long after the origin.</summary>
    public const double SettleSeconds = 5;

    private const int Channels = AudioFormat.Channels;
    private const int ChunkFrames = 1024;
    private const int OutputCapacityFrames = 2048;
    private const double MaxIntegralSeconds = 0.1;

    private readonly long _qpcFrequency;
    private readonly IAudioPcmSubscriber _sink;
    private readonly AdaptiveResampler _resampler = new(ChunkFrames + 64);
    private readonly float[] _output = new float[OutputCapacityFrames * Channels];
    private readonly long _settleTicks;

    private bool _started;
    private long _originQpc;
    private bool _exactNext;
    private bool _continuingCorrection;
    private bool _hasPacket;
    private long _lastPacketQpc;
    private int _lastPacketFrames;
    private double _integral;
    private double _ratioSum;
    private long _ratioCount;

    public DeviceStream(long qpcFrequency, IAudioPcmSubscriber sink)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _qpcFrequency = qpcFrequency;
        _sink = sink;
        _settleTicks = (long)(SettleSeconds * qpcFrequency);
        SteeringError = new LatencyHistogram(qpcFrequency, bucketMicroseconds: 5, rangeMicroseconds: 50_000);
    }

    public bool Started => _started;

    /// <summary>The QPC of output frame 0.</summary>
    public long OriginQpc => _originQpc;

    /// <summary>Input frames fed to the resampler: packets, less trims, plus silence inserted.</summary>
    public long InputFrames => _resampler.InputFrames;

    /// <summary>Frames sent to the sink.</summary>
    public long OutputFrames => _resampler.OutputFrames;

    /// <summary>Packets after which silence of more than the tolerance was inserted (flagged gaps, unflagged gaps, resume after fill).</summary>
    public long GapFills { get; private set; }

    public long GapFillFrames { get; private set; }

    /// <summary>Packets trimmed by more than the tolerance because they overlapped the stream.</summary>
    public long Trims { get; private set; }

    public long TrimmedFrames { get; private set; }

    /// <summary>Silent input frames inserted by <see cref="FillTo"/> (an idle or failed device).</summary>
    public long IdleFillFrames { get; private set; }

    /// <summary>Packets whose stamp was missing, flagged bad or not after the last one, and so extrapolated.</summary>
    public long ResolvedStamps { get; private set; }

    /// <summary>Packets that were neither exact placements nor within the tolerance: an unflagged gap or overlap, corrected by a step.</summary>
    public long StepCorrections { get; private set; }

    /// <summary>Packets dropped because nothing could place them: a bad stamp before the stream started.</summary>
    public long UnplacedPackets { get; private set; }

    /// <summary>The current ratio, (step − 1) × 10⁶: negative consumes input slower than 48 kHz, for a device whose clock runs slow.</summary>
    public double RatioPpm => (_resampler.Step - 1) * 1e6;

    /// <summary>The mean ratio over steered packets after the settle time; 0 before.</summary>
    public double MeanRatioPpm => _ratioCount == 0 ? 0 : _ratioSum / _ratioCount * 1e6;

    /// <summary>The error at the latest packet once it was placed, in samples: positive when its content comes out after its stamp.</summary>
    public double LastErrorSamples { get; private set; }

    /// <summary>The largest |error| of a steered packet after the settle time, in samples.</summary>
    public double MaxSettledErrorSamples { get; private set; }

    /// <summary>|error| of every steered packet after the settle time, in QPC ticks.</summary>
    public LatencyHistogram SteeringError { get; }

    /// <summary>The output index (fractional) at which stream input frame <paramref name="inputFrame"/> comes out.</summary>
    public double OutputIndexOf(double inputFrame) => _resampler.OutputIndexOf(inputFrame);

    /// <summary>Converts a QPC instant to a fractional output index.</summary>
    public double OutputIndexAt(long qpc) => (double)(qpc - _originQpc) * AudioFormat.SampleRate / _qpcFrequency;

    /// <summary>
    /// Adds one packet as the device delivered it. <paramref name="interleaved"/> holds
    /// <paramref name="frames"/> × 2 samples, or is ignored when the packet is <see cref="AudioPacketFlags.Silent"/>.
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

        if (!TryResolveStamp(qpc, flags, out var stamp))
        {
            UnplacedPackets++;
            return;
        }

        var exact = !_started || _exactNext || (flags & AudioPacketFlags.DataDiscontinuity) != 0;
        if (!_started)
        {
            Start(stamp);
        }

        var previousStamp = _lastPacketQpc;
        var hadPacket = _hasPacket;
        _hasPacket = true;
        _lastPacketQpc = stamp;
        _lastPacketFrames = frames;

        var error = Error(stamp);

        // A correction that dropped a whole packet goes on into this one: it is placed the same way, and not counted again.
        var continuing = _continuingCorrection;
        _continuingCorrection = false;
        var stepped = !exact && !continuing && Math.Abs(error) > ToleranceFrames;
        long skip = 0;
        if (exact || continuing || stepped)
        {
            if (stepped)
            {
                StepCorrections++;
            }

            // Input frames to insert (positive) or trim (negative) so the packet lands on its stamp.
            var correction = (long)Math.Round(-error * _resampler.Step);
            var counted = !continuing && Math.Abs(error) > ToleranceFrames;
            if (correction > 0)
            {
                InsertSilence(correction);
                if (counted)
                {
                    GapFills++;
                    GapFillFrames += correction;
                }
            }
            else if (correction < 0)
            {
                skip = -correction;
                if (counted)
                {
                    Trims++;
                }

                if (counted || continuing)
                {
                    TrimmedFrames += Math.Min(skip, frames);
                }
            }

            if (skip >= frames)
            {
                // Nothing of it is kept; the next packet is placed the same way.
                _exactNext = exact;
                _continuingCorrection = counted || continuing;
                LastErrorSamples = error - frames;
                return;
            }

            // The first kept frame's stamp is skip frames after the packet's.
            LastErrorSamples = Error(stamp) - skip;
        }
        else
        {
            Steer(error, hadPacket ? stamp - previousStamp : 0, stamp);
            LastErrorSamples = error;
        }

        _exactNext = false;
        var keep = frames - (int)skip;
        if (silent)
        {
            InsertSilence(keep);
        }
        else
        {
            var samples = interleaved.Slice((int)skip * Channels, keep * Channels);
            while (!samples.IsEmpty)
            {
                var chunk = Math.Min(samples.Length / Channels, _resampler.FreeInput);
                _resampler.Push(samples[..(chunk * Channels)]);
                samples = samples[(chunk * Channels)..];
                Drain();
            }
        }
    }

    /// <summary>Fills the stream with silence up to <paramref name="qpc"/>, if it is behind it; the next packet is placed exactly.</summary>
    public void FillTo(long qpc)
    {
        if (!_started)
        {
            Start(qpc);
            _exactNext = true;
            return;
        }

        var target = OutputIndexAt(qpc);
        var end = _resampler.OutputIndexOf(_resampler.InputFrames);
        if (target <= end)
        {
            return;
        }

        var frames = (long)Math.Ceiling((target - end) * _resampler.Step);
        InsertSilence(frames);
        IdleFillFrames += frames;
        _exactNext = true;
    }

    private void Start(long originQpc)
    {
        _started = true;
        _originQpc = originQpc;
        _resampler.Reset();
    }

    /// <summary>Where the next input frame comes out minus where its stamp says it belongs, in samples.</summary>
    private double Error(long stamp) => _resampler.OutputIndexOf(_resampler.InputFrames) - OutputIndexAt(stamp);

    private void Steer(double error, long sincePrevious, long stamp)
    {
        var dt = Math.Clamp((double)sincePrevious / _qpcFrequency, 0, MaxIntegralSeconds);
        var limit = MaxRatioDeviation / Ki;
        _integral = Math.Clamp(_integral + (error * dt), -limit, limit);
        var deviation = Math.Clamp((Kp * error) + (Ki * _integral), -MaxRatioDeviation, MaxRatioDeviation);
        _resampler.Step = 1 + deviation;

        if (stamp - _originQpc >= _settleTicks)
        {
            var magnitude = Math.Abs(error);
            SteeringError.Record((long)(magnitude * _qpcFrequency / AudioFormat.SampleRate));
            if (magnitude > MaxSettledErrorSamples)
            {
                MaxSettledErrorSamples = magnitude;
            }

            _ratioSum += deviation;
            _ratioCount++;
        }
    }

    private bool TryResolveStamp(long qpc, AudioPacketFlags flags, out long stamp)
    {
        var bad = qpc == 0
            || (flags & AudioPacketFlags.TimestampError) != 0
            || (_hasPacket && qpc <= _lastPacketQpc);
        if (!bad)
        {
            stamp = qpc;
            return true;
        }

        ResolvedStamps++;
        if (_hasPacket)
        {
            stamp = _lastPacketQpc + QpcClock.ToUnits(_lastPacketFrames, AudioFormat.SampleRate, _qpcFrequency);
            return true;
        }

        if (_started)
        {
            // Only silence fill so far: the packet continues the stream.
            var end = _resampler.OutputIndexOf(_resampler.InputFrames);
            stamp = _originQpc + (long)Math.Round(end * _qpcFrequency / AudioFormat.SampleRate);
            return true;
        }

        stamp = 0;
        return false;
    }

    private void InsertSilence(long frames)
    {
        while (frames > 0)
        {
            var chunk = (int)Math.Min(frames, _resampler.FreeInput);
            _resampler.PushSilence(chunk);
            frames -= chunk;
            Drain();
        }
    }

    private void Drain()
    {
        while (true)
        {
            var first = _resampler.OutputFrames;
            var count = _resampler.Produce(_output, out var silent);
            if (count == 0)
            {
                return;
            }

            var start = _originQpc + QpcClock.ToUnits(first, AudioFormat.SampleRate, _qpcFrequency);
            var block = new AudioBlock(start, count, silent ? ReadOnlySpan<float>.Empty : _output.AsSpan(0, count * Channels));
            _sink.OnPcm(in block);
        }
    }
}
