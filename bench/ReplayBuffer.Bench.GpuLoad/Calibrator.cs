namespace ReplayBuffer.Bench.GpuLoad;

/// <summary>
/// Steers the shader's iteration count N toward the target present rate during warm-up. Each window's
/// measured rate scales N by (measured / target), held to ×0.25–×4 a step so a frame never grows long
/// enough to trip the driver's timeout, and damped once within ×1.5 of the target so it settles; then
/// N is frozen for the run.
/// </summary>
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

        // Frame time is roughly a fixed cost plus N times a per-iteration cost: scaling N by the rate
        // ratio overshoots a little at low N, so near the target the step is damped.
        var ratio = measuredFps / _target;
        var step = Math.Abs(Math.Log(ratio)) < Math.Log(1.5) ? Math.Pow(ratio, 0.7) : Math.Clamp(ratio, 0.25, 4.0);
        var next = Math.Round(Iterations * step);
        Iterations = (uint)Math.Clamp(next, 1, MaxIterations);
        return Iterations;
    }
}
