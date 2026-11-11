using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

/// <summary>Capture faults from delegates: a fault a test doesn't set never happens.</summary>
internal sealed class TestCaptureFaults : ICaptureFaults
{
    public Func<string, Exception?>? ClipWriteFault { get; init; }

    public Func<Exception?>? RestartFault { get; init; }

    public Func<Exception?>? RecoveryFault { get; init; }

    public Func<Exception?>? WgcFault { get; init; }

    /// <summary>While it returns true each frame is dropped, as on the secure desktop.</summary>
    public Func<bool>? StarveFrames { get; init; }

    /// <summary>The composited screens, by GDI device name, captured through Desktop Duplication instead of WGC.</summary>
    public IReadOnlyCollection<string>? DuplicationScreens { get; init; }

    public TimeSpan MoveArmDelay { get; init; }

    public Exception? ClipWrite(string path) => ClipWriteFault?.Invoke(path);

    public Exception? Restart() => RestartFault?.Invoke();

    public Exception? Recovery() => RecoveryFault?.Invoke();

    public Exception? Wgc() => WgcFault?.Invoke();

    public bool DropsFrame() => StarveFrames?.Invoke() == true;

    public bool ForcesDuplication(string device) =>
        DuplicationScreens?.Contains(device, StringComparer.OrdinalIgnoreCase) == true;
}
