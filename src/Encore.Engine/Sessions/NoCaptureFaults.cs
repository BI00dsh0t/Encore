namespace Encore.Engine.Sessions;

/// <summary>The faults of a shipping session: none ever happens.</summary>
internal sealed class NoCaptureFaults : ICaptureFaults
{
    public static readonly NoCaptureFaults Instance = new();

    private NoCaptureFaults()
    {
    }

    public TimeSpan MoveArmDelay => TimeSpan.Zero;

    public Exception? ClipWrite(string path) => null;

    public Exception? Restart() => null;

    public Exception? Recovery() => null;

    public Exception? Wgc() => null;

    public bool DropsFrame() => false;

    public bool ForcesDuplication(string device) => false;
}
