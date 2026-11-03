namespace ReplayBuffer.Media;

/// <summary>
/// The last stage of a Track (AD-13): a stereo-linked peak limiter that acts only above full scale.
/// Audio that stays within [-1, 1] passes through bit-identically. A frame whose peak would exceed
/// full scale drops the gain at once to bring it to exactly full scale (instant attack, no
/// look-ahead, so nothing is delayed); the gain then recovers toward 1 with a
/// <see cref="ReleaseSeconds"/> time constant and snaps to exactly 1. One gain applies to both
/// channels. Non-finite samples are written as 0. No gain control, normalisation or compression
/// below full scale. Not thread-safe: its owner serialises the writes. Nothing is allocated once
/// constructed.
/// </summary>
public sealed class PeakLimiter : IAudioSampleSink
{
    /// <summary>The release time constant, in seconds.</summary>
    public const double ReleaseSeconds = 0.05;

    /// <summary>The per-frame release factor applied to the gain's distance from 1.</summary>
    public static readonly double ReleaseCoefficient = Math.Exp(-1 / (ReleaseSeconds * AudioTimeline.SampleRate));

    /// <summary>A gain this close to 1 is snapped to exactly 1.</summary>
    private const double SnapEpsilon = 1e-6;

    private const int ChunkFrames = 4096;
    private const int Channels = AudioTimeline.Channels;

    private readonly IAudioSampleSink _output;
    private readonly float[] _scratch = new float[ChunkFrames * Channels];
    private double _gain = 1;

    /// <param name="output">Receives the limited stream, typically the Track's encoder.</param>
    public PeakLimiter(IAudioSampleSink output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>The current gain; exactly 1 when the limiter is not acting.</summary>
    public double Gain => _gain;

    /// <summary>Frames written with a gain below 1.</summary>
    public long LimitedFrames { get; private set; }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        interleaved = interleaved[..(interleaved.Length / Channels * Channels)];
        if (interleaved.IsEmpty)
        {
            return;
        }

        if (_gain == 1 && InRange(interleaved))
        {
            _output.Write(interleaved);
            return;
        }

        while (!interleaved.IsEmpty)
        {
            var count = Math.Min(interleaved.Length, _scratch.Length);
            var target = _scratch.AsSpan(0, count);
            Process(interleaved[..count], target);
            _output.Write(target);
            interleaved = interleaved[count..];
        }
    }

    public void WriteSilence(long frames)
    {
        if (frames <= 0)
        {
            return;
        }

        if (_gain < 1)
        {
            var remaining = Math.Pow(ReleaseCoefficient, frames);
            _gain = Snap(1 - (1 - _gain) * remaining);
        }

        _output.WriteSilence(frames);
    }

    /// <summary>Whether every sample is within [-1, 1]; NaN is not.</summary>
    private static bool InRange(ReadOnlySpan<float> samples)
    {
        foreach (var x in samples)
        {
            if (!(Math.Abs(x) <= 1f))
            {
                return false;
            }
        }

        return true;
    }

    private void Process(ReadOnlySpan<float> source, Span<float> target)
    {
        var gain = _gain;
        var limited = 0L;
        for (var s = 0; s < source.Length; s += Channels)
        {
            var l = source[s];
            var r = source[s + 1];
            if (!float.IsFinite(l))
            {
                l = 0;
            }

            if (!float.IsFinite(r))
            {
                r = 0;
            }

            double peak = Math.Max(Math.Abs(l), Math.Abs(r));
            if (peak * gain > 1)
            {
                gain = 1 / peak;
            }

            if (gain == 1)
            {
                target[s] = l;
                target[s + 1] = r;
            }
            else
            {
                target[s] = (float)Math.Clamp(l * gain, -1, 1);
                target[s + 1] = (float)Math.Clamp(r * gain, -1, 1);
            }

            if (gain < 1)
            {
                limited++;
                gain = Snap(1 - (1 - gain) * ReleaseCoefficient);
            }
        }

        _gain = gain;
        LimitedFrames += limited;
    }

    private static double Snap(double gain) => gain >= 1 - SnapEpsilon ? 1 : gain;
}
