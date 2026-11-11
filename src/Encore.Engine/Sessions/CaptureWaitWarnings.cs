using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;
using Encore.Engine.Ipc;
using Encore.Platform.Windows;

namespace Encore.Engine.Sessions;

/// <summary>Picks the Target's window to capture, and warns on the status line while capture waits. Orchestrator thread.</summary>
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
        e => EngineFailure.CodeOf(e) == ErrorCodes.CaptureFailed);

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
            log.LogInformation(
                "Stopped waiting to capture {Name} (process {Pid}): it is no longer the Target Game", target.Name, target.ProcessId);
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
