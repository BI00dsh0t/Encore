using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// Game messages (epic-game-detection-profiles): Game Detection, the Target Game and Game Profiles.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// </summary>
public static class GameMessages
{
    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [];
}
