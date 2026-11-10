using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;
using Encore.Engine.Ipc;
using Encore.Platform.Windows;

namespace Encore.Engine.Sessions;

/// <summary>
/// The engine's side of following a Target Game's process (<see cref="CaptureWait"/>): its window
/// to capture is the one it was given while that can be captured (visible, titled, not minimized or
/// cloaked, at least <see cref="MinWidth"/> × <see cref="MinHeight"/>), else its largest that can.
/// Each run of waits is logged once and shown on the status line as a <c>capture-failed</c> Warning
/// saying capture is waiting, withdrawn when the run ends. Called on the orchestrator thread only.
/// </summary>
internal sealed class CaptureWaitWarnings(Warnings warnings, ILogger log)
{
    /// <summary>The smallest client area captured: a startup placeholder (Rocket League's is 156×27) is smaller.</summary>
    public const int MinWidth = 320;

    public const int MinHeight = 180;

    private string? _shown;

    /// <summary>The orchestrator's wait, wired to this.</summary>
    public CaptureWait Wait => new(
        target => WindowFinder.CaptureWindowOf(target.ProcessId, target.Window, MinWidth, MinHeight) is var (window, why) && window != 0
            ? CaptureWindow.Of(window)
            : CaptureWindow.None(why ?? "no window of the process can be captured"),
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
            $"Waiting for {target.Name}'s window… Capture resumes once it can be captured.",
            overlay: false).Id;
    }

    private void Ended(TargetCandidate target, bool captured)
    {
        if (captured)
        {
            log.LogInformation("Capturing {Name} (process {Pid}) again after waiting for its window", target.Name, target.ProcessId);
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
