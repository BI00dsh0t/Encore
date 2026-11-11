namespace Encore.Core.Sessions;

/// <summary>A game's or the Monitor's Recordings: the one running, and the one a restart ended. Orchestrator thread only.</summary>
internal sealed class RecordingSlot
{
    /// <summary>The Recording started and not yet posted back; null when none runs.</summary>
    public RecordingRun? Running { get; private set; }

    /// <summary>The Recording a forced encoder restart ended, until its writer posts back; null for none.</summary>
    public RecordingRun? Ending { get; private set; }

    /// <summary>Whether no Recording of this kind is running or still finishing.</summary>
    public bool IsOff => Running is null && Ending is null;

    public void Start(RecordingRun recording) => Running = recording;

    /// <summary>An encoder restart ended the running Recording, which becomes the ending one; false while one is still ending.</summary>
    public bool TryMoveRunningToEnding()
    {
        if (Ending is not null || Running is null)
        {
            return false;
        }

        Ending = Running;
        Running = null;
        return true;
    }

    /// <summary>The Recording's writer posted back, or shutdown gave up on it: it leaves the slot. Says which place it held.</summary>
    public RecordingPlace Remove(RecordingRun recording)
    {
        if (ReferenceEquals(Ending, recording))
        {
            Ending = null;
            return RecordingPlace.Ending;
        }

        if (ReferenceEquals(Running, recording))
        {
            Running = null;
            return RecordingPlace.Running;
        }

        return RecordingPlace.None;
    }

    /// <summary>The running Recording when it replaced <paramref name="ended"/> at an encoder restart; else null.</summary>
    public RecordingRun? ReplacementOf(RecordingRun ended) =>
        Running is { } next && ReferenceEquals(next.Follows, ended) ? next : null;
}
