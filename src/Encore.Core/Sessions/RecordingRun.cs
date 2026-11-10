using Encore.Contracts.Messages;

namespace Encore.Core.Sessions;

/// <summary>A Recording started on session <paramref name="sessionId"/>; orchestrator thread only.</summary>
internal sealed class RecordingRun(long sessionId, string path, IRecordingWrite write)
{
    public long SessionId { get; } = sessionId;

    public string Path { get; } = path;

    public IRecordingWrite Write { get; } = write;

    /// <summary>Why it is stopping (<see cref="RecordingStopReasons"/>); null while it runs.</summary>
    public string? Reason { get; set; }

    /// <summary>Completed by the writer's callback, from its thread, with its error, before it posts back.</summary>
    public TaskCompletionSource<Exception?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The Recording a forced encoder restart ended and this one replaced, until this one is announced.</summary>
    public RecordingRun? Follows { get; set; }

    /// <summary>Whether it is the Monitor session's Monitor Recording, not the game session's.</summary>
    public bool Monitor { get; init; }
}
