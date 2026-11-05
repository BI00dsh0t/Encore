using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Core.Sessions;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// The engine's side of the wait for a Target Game's window (<see cref="CaptureWait"/>): a window is
/// ready once it is visible, not minimized or cloaked, and at least <see cref="MinWidth"/> ×
/// <see cref="MinHeight"/>. Each run of waits is logged once and shown on the status line as a
/// <c>capture-failed</c> Warning saying capture is waiting, withdrawn when the run ends. Called on the
/// orchestrator thread only.
/// </summary>
internal sealed class CaptureWaitWarnings(Warnings warnings, ILogger log)
{
    /// <summary>The smallest client area captured: a startup placeholder (Rocket League's is 156×27) is smaller.</summary>
    public const int MinWidth = 320;

    public const int MinHeight = 180;

    private string? _shown;

    /// <summary>The orchestrator's wait, wired to this.</summary>
    public CaptureWait Wait => new(
        target => WindowFinder.NotCapturable(target.Window, MinWidth, MinHeight),
        Waiting,
        Ended,
        e => CaptureSession.CodeOf(e) == ErrorCodes.CaptureFailed);

    private void Waiting(TargetCandidate target, string why)
    {
        log.LogWarning(
            "Waiting to capture {Name} (process {Pid}, window 0x{Window:X}): {Why}; retrying until it can be captured",
            target.Name, target.ProcessId, target.Window, why);
        Withdraw();
        _shown = warnings.Raise(
            ErrorCodes.CaptureFailed,
            WarningSeverity.Warning,
            "replay",
            $"Waiting for {target.Name}'s window before capturing; retrying every few seconds.",
            overlay: false).Id;
    }

    private void Ended(TargetCandidate target, bool captured)
    {
        if (captured)
        {
            log.LogInformation("Capturing {Name} (process {Pid}) after waiting for its window", target.Name, target.ProcessId);
        }
        else
        {
            log.LogInformation("Stopped waiting to capture {Name} (process {Pid}): it is no longer the Target Game", target.Name, target.ProcessId);
        }

        Withdraw();
    }

    private void Withdraw()
    {
        if (_shown is { } id)
        {
            _shown = null;
            warnings.Withdraw(id);
        }
    }
}
