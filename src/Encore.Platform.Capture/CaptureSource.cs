using Microsoft.Extensions.Logging;

namespace Encore.Platform.Capture;

/// <summary>What every capture source shares: its two events, a close raised once, and frames handed on safely.</summary>
public abstract class CaptureSource : ICaptureSource
{
    private int _closedRaised;

    private protected CaptureSource(CapturedFrameHandler onFrame, ILogger logger)
    {
        Handler = onFrame;
        Logger = logger;
    }

    /// <inheritdoc/>
    public event Action? Closed;

    /// <inheritdoc/>
    public event Action<Exception>? Failed;

    private protected CapturedFrameHandler Handler { get; }

    private protected ILogger Logger { get; }

    public abstract void Start();

    public abstract void SetFps(int fps);

    public abstract void Dispose();

    /// <summary>The ticks of half an output slot: frames arrive at most twice a slot.</summary>
    private protected static long HalfSlot(long ticksPerSecond, int fps) => ticksPerSecond / (2L * fps);

    /// <summary>True the first time only, so <see cref="Closed"/> is raised once however many paths see the end.</summary>
    private protected bool TryClaimClose() => Interlocked.Exchange(ref _closedRaised, 1) == 0;

    private protected void RaiseClosed() => Closed?.Invoke();

    private protected void RaiseFailed(Exception error) => Failed?.Invoke(error);

    private protected void LogRate(int fps) => Logger.LogInformation("Capturing at most {Fps} fps", 2 * fps);

    /// <summary>Hands a frame to the handler; a handler that throws is logged and reported, and capture goes on.</summary>
    private protected void Deliver(long qpc, nint texture, VideoSize contentSize)
    {
        try
        {
            Handler(qpc, texture, contentSize);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Frame handler failed");
            RaiseFailed(e);
        }
    }
}
