using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// Audio messages (epic-multi-track-audio): Track Layouts, devices and meters. Coalescing events such as <c>meters.levels</c> go here.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// </summary>
public static class AudioMessages
{
    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [];
}
