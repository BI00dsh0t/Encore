namespace Encore.Core.Sessions;

/// <summary>A run of waits for a Target's window: who is waited for, the failures so far and the next try. Orchestrator thread.</summary>
internal sealed class CaptureWaitRun(CaptureWait? wait, Action postRetry)
{
    private Timer? _retry;
    private int _failures;

    /// <summary>The Target whose window is waited for; null while no run is under way.</summary>
    public TargetCandidate? Target { get; private set; }

    /// <summary>Starts a run for the Target, reported once; a run for another process ends first.</summary>
    public void Begin(TargetCandidate target, string why)
    {
        if (Target is { } running && running.ProcessId != target.ProcessId)
        {
            End(captured: false);
        }

        if (Target is null)
        {
            _failures = 0;
            wait!.Waiting(target, why);
        }

        Target = target;
    }

    /// <summary>Posts the next try: after the backoff of the failures so far when this one failed, else at the poll.</summary>
    public void ScheduleRetry(bool failed)
    {
        if (failed)
        {
            _failures++;
        }

        _retry?.Dispose();
        _retry = new Timer(_ => postRetry(), null, failed ? wait!.Backoff(_failures) : wait!.Poll, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Drops a try still pending; the run goes on.</summary>
    public void CancelRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }

    /// <summary>Ends the run, if one is under way, and reports how it ended.</summary>
    public void End(bool captured)
    {
        if (Target is not { } waiting)
        {
            return;
        }

        Target = null;
        _failures = 0;
        CancelRetry();
        wait?.Ended(waiting, captured);
    }
}
