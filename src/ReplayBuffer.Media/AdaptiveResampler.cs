using ReplayBuffer.Core.Audio;

namespace ReplayBuffer.Media;

/// <summary>
/// A polyphase windowed-sinc resampler for 48 kHz stereo float with a fractional, adjustable
/// <see cref="Step"/>: the input frames consumed per output frame (AD-13). Output frame j is the
/// input signal evaluated at input position <c>p_j</c>, where <c>p_0 = 0</c> and
/// <c>p_{j+1} = p_j + Step</c>. The kernel is centred on that position, so input frame I comes out
/// at the output index whose position is I (<see cref="OutputIndexOf"/>); the only delay is in time,
/// since an output frame needs the <see cref="HalfTaps"/> input frames after its position.
/// At a step of exactly 1 the output is the input, sample for sample.
/// Every buffer is allocated up front; pushing and producing never allocate. Not thread-safe.
/// </summary>
public sealed class AdaptiveResampler
{
    /// <summary>Input frames each side of the position that the kernel reads.</summary>
    public const int HalfTaps = 16;

    /// <summary>Kernel tables per input frame; between two, coefficients are interpolated linearly.</summary>
    public const int Phases = 256;

    private const int Taps = 2 * HalfTaps;
    private const int Channels = AudioFormat.Channels;
    private const double KaiserBeta = 8.6;

    private static readonly float[] Table = BuildTable();

    private readonly float[] _buffer;
    private readonly int _capacity;

    // The buffer holds input frames [_bufferStart, _inputFrames); index 0 of it is frame _bufferStart.
    private long _bufferStart;
    private long _inputFrames;
    private long _positionWhole;
    private double _positionFraction;
    private long _lastSound;
    private long _outputFrames;

    /// <param name="inputCapacity">The most input frames that can wait unconsumed, beyond the kernel's own window.</param>
    public AdaptiveResampler(int inputCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputCapacity);
        _capacity = inputCapacity + (2 * Taps);
        _buffer = new float[_capacity * Channels];
        Reset();
    }

    /// <summary>Input frames consumed per output frame; 1 + 10⁻⁶ consumes one frame more per million.</summary>
    public double Step { get; set; } = 1;

    /// <summary>Input frames pushed since the last reset, silence included.</summary>
    public long InputFrames => _inputFrames;

    /// <summary>Output frames produced since the last reset.</summary>
    public long OutputFrames => _outputFrames;

    /// <summary>The input position of the next output frame.</summary>
    public double Position => _positionWhole + _positionFraction;

    /// <summary>How many input frames can be pushed now without overwriting frames the kernel still needs.</summary>
    public int FreeInput => (int)(_capacity - (_inputFrames - OldestNeeded));

    private long OldestNeeded => _positionWhole - HalfTaps + 1;

    /// <summary>The output index (fractional) at which input position <paramref name="inputPosition"/> comes out at the current step.</summary>
    public double OutputIndexOf(double inputPosition) => _outputFrames + ((inputPosition - Position) / Step);

    /// <summary>Back to the start: the history before input frame 0 is silence, and the step is 1.</summary>
    public void Reset()
    {
        Array.Clear(_buffer);
        _bufferStart = -(HalfTaps - 1);
        _inputFrames = 0;
        _positionWhole = 0;
        _positionFraction = 0;
        _lastSound = long.MinValue;
        _outputFrames = 0;
        Step = 1;
    }

    /// <summary>Appends interleaved stereo frames; at most <see cref="FreeInput"/>.</summary>
    public void Push(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / Channels;
        if (frames == 0)
        {
            return;
        }

        var offset = Reserve(frames);
        interleaved[..(frames * Channels)].CopyTo(_buffer.AsSpan(offset * Channels));
        _inputFrames += frames;
        _lastSound = _inputFrames - 1;
    }

    /// <summary>Appends <paramref name="frames"/> frames of silence; at most <see cref="FreeInput"/>.</summary>
    public void PushSilence(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var offset = Reserve(frames);
        _buffer.AsSpan(offset * Channels, frames * Channels).Clear();
        _inputFrames += frames;
    }

    /// <summary>
    /// Produces output frames into <paramref name="destination"/> while the input allows, all of one
    /// kind: either frames that depend on some pushed sound, written as samples, or frames whose whole
    /// kernel window is silence, which <paramref name="silent"/> reports and which are not written.
    /// Returns the frames produced; 0 when the next one needs more input.
    /// </summary>
    public int Produce(Span<float> destination, out bool silent)
    {
        var max = destination.Length / Channels;
        silent = false;
        if (max == 0 || !CanProduce())
        {
            return 0;
        }

        silent = NextIsSilent();
        var count = 0;
        while (count < max && CanProduce() && NextIsSilent() == silent)
        {
            if (!silent)
            {
                Interpolate(destination.Slice(count * Channels, Channels));
            }

            Advance();
            count++;
        }

        _outputFrames += count;
        return count;
    }

    private bool CanProduce() => _positionWhole + HalfTaps < _inputFrames;

    private bool NextIsSilent() => _positionWhole - HalfTaps + 1 > _lastSound;

    private void Advance()
    {
        _positionFraction += Step;
        var whole = Math.Floor(_positionFraction);
        _positionWhole += (long)whole;
        _positionFraction -= whole;
    }

    private void Interpolate(Span<float> frame)
    {
        var phase = _positionFraction * Phases;
        var row = (int)phase;
        if (row >= Phases)
        {
            row = Phases - 1;
        }

        var blend = (float)(phase - row);
        var low = Table.AsSpan(row * Taps, Taps);
        var high = Table.AsSpan((row + 1) * Taps, Taps);
        var first = (int)(_positionWhole - HalfTaps + 1 - _bufferStart) * Channels;
        var window = _buffer.AsSpan(first, Taps * Channels);

        float left = 0, right = 0;
        for (var k = 0; k < Taps; k++)
        {
            var c = low[k] + (blend * (high[k] - low[k]));
            left += c * window[2 * k];
            right += c * window[(2 * k) + 1];
        }

        frame[0] = left;
        frame[1] = right;
    }

    /// <summary>Makes room for <paramref name="frames"/> at the end, moving the kept frames to the front when needed; returns the buffer offset of the first.</summary>
    private int Reserve(int frames)
    {
        if (frames > FreeInput)
        {
            throw new InvalidOperationException($"{frames} input frames don't fit; {FreeInput} are free.");
        }

        if (_inputFrames + frames - _bufferStart > _capacity)
        {
            var keepFrom = (int)(OldestNeeded - _bufferStart);
            var keep = (int)(_inputFrames - OldestNeeded);
            _buffer.AsSpan(keepFrom * Channels, keep * Channels).CopyTo(_buffer);
            _bufferStart = OldestNeeded;
        }

        return (int)(_inputFrames - _bufferStart);
    }

    /// <summary>
    /// Row r holds the taps for the fractional position r / <see cref="Phases"/>; tap k reads input
    /// frame <c>whole − HalfTaps + 1 + k</c>. Each row is a Kaiser-windowed sinc normalized to unit
    /// DC gain; row 0 is a unit impulse, so a step of 1 copies the input.
    /// </summary>
    private static float[] BuildTable()
    {
        var table = new float[(Phases + 1) * Taps];
        var row = new double[Taps];
        var i0Beta = BesselI0(KaiserBeta);
        for (var r = 0; r <= Phases; r++)
        {
            var fraction = (double)r / Phases;
            var sum = 0.0;
            for (var k = 0; k < Taps; k++)
            {
                var t = k - (HalfTaps - 1) - fraction;
                double h;
                if (Math.Abs(t - Math.Round(t)) < 1e-12)
                {
                    h = Math.Round(t) == 0 ? 1 : 0;
                }
                else
                {
                    var x = t / HalfTaps;
                    var window = Math.Abs(x) >= 1 ? 0 : BesselI0(KaiserBeta * Math.Sqrt(1 - (x * x))) / i0Beta;
                    h = Math.Sin(Math.PI * t) / (Math.PI * t) * window;
                }

                row[k] = h;
                sum += h;
            }

            for (var k = 0; k < Taps; k++)
            {
                table[(r * Taps) + k] = (float)(row[k] / sum);
            }
        }

        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        var half = x / 2;
        for (var k = 1; k < 50; k++)
        {
            term *= half / k;
            var squared = term * term;
            sum += squared;
            if (squared < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
