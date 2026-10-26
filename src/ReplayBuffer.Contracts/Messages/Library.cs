using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// Library messages (epic-library-cleanup): the Library index and Cleanup.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// </summary>
public static class LibraryMessages
{
    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [];
}
