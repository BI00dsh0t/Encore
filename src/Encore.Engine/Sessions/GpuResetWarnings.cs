using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Sessions;
using Encore.Engine.Ipc;

namespace Encore.Engine.Sessions;

/// <summary>
/// The status line's side of a GPU driver reset (R3): each loss raises <c>gpu-reset</c> (Warning,
/// not on the Overlay). A recovery withdraws the oldest one still shown, so the line no longer
/// says Rewind is rebuilding. A give-up withdraws it too, and raises
/// <c>gpu-reset-failed</c> (Error). Called on the orchestrator thread only.
/// </summary>
internal sealed class GpuResetWarnings(Warnings warnings, ILogger log)
{
    // One per session still recovering, oldest first.
    private readonly Queue<string> _shown = new();

    /// <summary>The orchestrator's callbacks, wired to this.</summary>
    public DeviceRecovery Recovery => new(Reset, Failed, Recovered);

    private void Reset(string message)
    {
        log.LogWarning("{Code}: {Message}", ErrorCodes.GpuReset, message);
        _shown.Enqueue(warnings.Raise(ErrorCodes.GpuReset, WarningSeverity.Warning, "replay", message, overlay: false).Id);
    }

    private void Failed(string message)
    {
        log.LogError("{Code}: {Message}", ErrorCodes.GpuResetFailed, message);
        WithdrawOldest();
        warnings.Raise(ErrorCodes.GpuResetFailed, WarningSeverity.Error, "replay", message, overlay: false);
    }

    private void Recovered(TimeSpan took)
    {
        log.LogInformation("Recovered from the GPU reset in {Ms:0} ms; buffering again", took.TotalMilliseconds);
        WithdrawOldest();
    }

    private void WithdrawOldest()
    {
        if (_shown.TryDequeue(out var id))
        {
            warnings.Withdraw(id);
        }
    }
}
