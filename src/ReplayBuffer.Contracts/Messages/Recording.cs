using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// Recording messages (epic-recordings): Recordings and Monitor Recording.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// </summary>
/// <remarks>
/// The resume rule after a restart (AD-16, story 8): when the engine restarts, only the Replay
/// Buffer resumes. A Recording or Monitor Recording that was running never resumes by itself;
/// <see cref="ErrorCodes.RecordingInterrupted"/> is raised for it instead. This area implements
/// the rule when it adds Recordings.
/// </remarks>
public static class RecordingMessages
{
    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [];
}
