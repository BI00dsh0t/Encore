namespace ReplayBuffer.Bench;

/// <summary>
/// Finds the onsets of <c>TonePlayer</c>'s 3 kHz bursts in decoded PCM: the 3 kHz quadrature detector of
/// story 1.12's verify test (a 2 ms window; one cycle is 16 samples at 48 kHz), indexed by sample
/// instead of QPC. An onset is the first sample where the envelope crosses <c>On</c>; the detector
/// re-arms after 200 ms below <c>Off</c>. The thresholds scale with the bursts' peak level, so the
/// test's −30 dBFS gives its 0.004 and 0.0015. Feed it mono samples in order, in any chunk sizes.
/// </summary>
internal sealed class OnsetDetector
{
    public const int SampleRate = 48_000;
    public const double DefaultBurstDbfs = -30;

    private const int Window = 96;
    private const int RearmSamples = SampleRate / 5;

    private static readonly double[] Cos = [.. Enumerable.Range(0, 16).Select(k => Math.Cos(2 * Math.PI * k / 16))];
    private static readonly double[] Sin = [.. Enumerable.Range(0, 16).Select(k => Math.Sin(2 * Math.PI * k / 16))];

    private readonly double[] _i = new double[Window];
    private readonly double[] _q = new double[Window];
    private readonly double _on;
    private readonly double _off;
    private double _sumI;
    private double _sumQ;
    private long _sample;
    private bool _armed = true;
    private int _below;

    /// <param name="burstDbfs">The bursts' peak level (<c>TonePlayer</c>'s <c>dbfs</c>).</param>
    public OnsetDetector(double burstDbfs = DefaultBurstDbfs)
    {
        var amplitude = Math.Pow(10, burstDbfs / 20);
        _on = amplitude * 0.004 / 0.0316;
        _off = amplitude * 0.0015 / 0.0316;
    }

    /// <summary>The sample index of each onset, from the first sample fed.</summary>
    public List<long> Onsets { get; } = [];

    /// <summary>Samples fed so far.</summary>
    public long Samples => _sample;

    public void Process(ReadOnlySpan<float> mono)
    {
        foreach (var sample in mono)
        {
            double x = sample;
            var slot = (int)(_sample % Window);
            var phase = (int)(_sample % 16);
            _sumI += (x * Cos[phase]) - _i[slot];
            _sumQ += (x * Sin[phase]) - _q[slot];
            _i[slot] = x * Cos[phase];
            _q[slot] = x * Sin[phase];

            var envelope = 2 * Math.Sqrt((_sumI * _sumI) + (_sumQ * _sumQ)) / Window;
            if (_armed && envelope > _on)
            {
                Onsets.Add(_sample);
                _armed = false;
                _below = 0;
            }
            else if (!_armed)
            {
                _below = envelope < _off ? _below + 1 : 0;
                if (_below >= RearmSamples)
                {
                    _armed = true;
                }
            }

            _sample++;
        }
    }

    /// <summary>Stereo interleaved samples, averaged to mono as the test's detector does.</summary>
    public void ProcessStereo(ReadOnlySpan<float> interleaved, Span<float> scratch)
    {
        var frames = interleaved.Length / 2;
        for (var start = 0; start < frames; start += scratch.Length)
        {
            var count = Math.Min(scratch.Length, frames - start);
            for (var n = 0; n < count; n++)
            {
                var at = 2 * (start + n);
                scratch[n] = 0.5f * (interleaved[at] + interleaved[at + 1]);
            }

            Process(scratch[..count]);
        }
    }
}
