using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Feedback;

namespace Encore.Engine.Ipc;

/// <summary>
/// The engine's active warnings (AD-3): raised as <c>warning.raised</c>, removed by
/// <c>warning.dismiss</c> or, for the feedback notices it raises, withdrawn by the engine itself.
/// </summary>
internal sealed class Warnings(EventHub hub, TimeProvider time) : IFeedbackNotices
{
    private readonly Lock _lock = new();

    public Warning Raise(string code, WarningSeverity severity, string source, string message, bool overlay, string? sound = null, bool dismissable = true)
    {
        var warning = new Warning(Guid.NewGuid().ToString("N"), code, severity, source, message, time.GetUtcNow(), overlay, sound, dismissable);
        lock (_lock)
        {
            hub.Publish(FeedbackMessages.Raised, warning);
        }

        return warning;
    }

    /// <summary>A feedback notice: on the Overlay, with its sound, dismissable.</summary>
    string IFeedbackNotices.Raise(FeedbackNotice notice) =>
        Raise(notice.Code, notice.Severity, notice.Source, notice.Message, overlay: true, notice.Sound).Id;

    /// <summary>
    /// The engine's own dismissal: publishes <c>warning.dismissed</c> if <paramref name="id"/> is
    /// active, dismissable or not; nothing happens if the user already dismissed it.
    /// </summary>
    public void Withdraw(string id)
    {
        lock (_lock)
        {
            if (hub.State.Warnings.Any(w => w.Id == id))
            {
                hub.Publish(FeedbackMessages.Dismissed, new WarningDismissed(id));
            }
        }
    }

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.WarningNotFound"/> or <see cref="ErrorCodes.WarningNotDismissable"/>.</exception>
    public Empty Dismiss(WarningDismiss dismiss)
    {
        lock (_lock)
        {
            var warning = hub.State.Warnings.FirstOrDefault(w => w.Id == dismiss.Id)
                ?? throw new ErrorCodeException(ErrorCodes.WarningNotFound, $"No active warning has id '{dismiss.Id}'.");
            if (!warning.Dismissable)
            {
                throw new ErrorCodeException(ErrorCodes.WarningNotDismissable, $"Warning '{warning.Code}' can't be dismissed.");
            }

            hub.Publish(FeedbackMessages.Dismissed, new WarningDismissed(warning.Id));
            return Empty.Value;
        }
    }
}
