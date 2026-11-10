namespace Encore.Core.Sessions;

/// <summary>The rule for a game session whose settings changed: keep its chain, wait, switch at a Reset, or build ahead.</summary>
internal static class ChainChange
{
    public static ChainDecision Decide(bool sameChain, bool recordingOnSession, bool monitorRuns)
    {
        if (sameChain)
        {
            return ChainDecision.Keep;
        }

        if (recordingOnSession)
        {
            return ChainDecision.HeldByRecording;
        }

        return monitorRuns ? ChainDecision.SwitchAtReset : ChainDecision.PrepareAhead;
    }
}
