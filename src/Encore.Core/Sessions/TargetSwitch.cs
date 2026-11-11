using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

/// <summary>The rule for a proposed Target Game: when it only moves a window, when it is held, and when it switches.</summary>
internal static class TargetSwitch
{
    /// <summary>What the proposal does, given the committed Target and whether a game Recording runs or is still finishing.</summary>
    public static TargetDecision Decide(TargetCandidate? current, TargetCandidate? to, TargetChangeReason reason, bool recording)
    {
        if (to is not null && current is not null && to.ProcessId == current.ProcessId)
        {
            return TargetDecision.SameProcess;
        }

        // An exit or Ignored ends the Recording's session, so it is never held.
        var ends = reason is TargetChangeReason.Exit or TargetChangeReason.Ignored;
        if (recording && current is not null && to is not null && !ends)
        {
            return TargetDecision.Held;
        }

        return to is null && current is null ? TargetDecision.Nothing : TargetDecision.Switch;
    }

    /// <summary>The stop reason of a Recording running on the game session a Target change ends.</summary>
    public static string RecordingReasonFor(TargetChangeReason reason) => reason switch
    {
        TargetChangeReason.Exit => RecordingStopReasons.GameExit,
        TargetChangeReason.Ignored => RecordingStopReasons.Ignored,
        _ => RecordingStopReasons.SessionEnded,
    };
}
