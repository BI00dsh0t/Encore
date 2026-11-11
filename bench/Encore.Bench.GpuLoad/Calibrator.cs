namespace Encore.Bench.GpuLoad;

/// <summary>Steers the shader's iteration count toward the target present rate during warm-up, then freezes it for the run.</summary>
internal sealed class Calibrator
{
    public const uint InitialIterations = 256;
    public const uint MaxIterations = 1u << 22;

    /// <summary>A window ends once it is this long and has at least <see cref="MinWindowFrames"/> presents.</summary>
    public const double WindowSeconds = 0.5;
    public const int MinWindowFrames = 3;

    /// <summary>Presents ignored after each change of N, while the frames queued before it drain.</summary>
    public const int SettleFrames = 4;

    private readonly double _target;

    public Calibrator(double targetFps, uint initial = InitialIterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetFps);
        _target = targetFps;
        Iterations = Math.Clamp(initial, 1u, MaxIterations);
    }

    public uint Iterations { get; private set; }

    /// <summary>Takes one window's measured present rate and returns the next N.</summary>
    public uint Next(double measuredFps)
    {
        if (!double.IsFinite(measuredFps) || measuredFps <= 0)
        {
            return Iterations;
        }

        // Frame time is a fixed cost plus N times a per-iteration cost, so near the target the step is damped.
        var ratio = measuredFps / _target;
        var step = Math.Abs(Math.Log(ratio)) < Math.Log(1.5) ? Math.Pow(ratio, 0.7) : Math.Clamp(ratio, 0.25, 4.0);
        var next = Math.Round(Iterations * step);
        Iterations = (uint)Math.Clamp(next, 1, MaxIterations);
        return Iterations;
    }
}
