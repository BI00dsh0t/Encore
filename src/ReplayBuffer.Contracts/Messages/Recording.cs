using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// Recording messages (epic-recordings): Recordings and Monitor Recording.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// </summary>
public static class RecordingMessages
{
    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [];
}
