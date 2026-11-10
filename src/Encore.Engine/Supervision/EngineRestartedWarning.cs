using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Engine.Ipc;

namespace Encore.Engine.Supervision;

/// <summary>The <c>engine-restarted</c> warning, raised once however many paths report the restart.</summary>
internal static class EngineRestartedWarning
{
    /// <summary>Makes the check and the raise one step.</summary>
    private static readonly Lock RaiseLock = new();

    /// <summary>Raises <c>engine-restarted</c> for a reason, unless one is already active.</summary>
    public static void Raise(EventHub hub, Warnings warnings, EngineRestartReason reason)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(warnings);
        lock (RaiseLock)
        {
            if (hub.State.Warnings.Any(warning => warning.Code == ErrorCodes.EngineRestarted))
            {
                return;
            }

            warnings.Raise(
                ErrorCodes.EngineRestarted,
                WarningSeverity.Warning,
                "supervision",
                reason == EngineRestartReason.Hung
                    ? "Encore's background service stopped answering and was restarted."
                    : "Encore's background service stopped unexpectedly and was restarted.",
                overlay: false);
        }
    }
}
