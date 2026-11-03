using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// The Target Game: the one game the engine captures for. <see cref="ProfileId"/> is the game's
/// executable as <c>Canonicalize</c> made it (AD-18): its lowercase final path.
/// </summary>
/// <param name="Name">The game's display name; the executable's file name until Game Profiles name it.</param>
/// <param name="ProcessId">The game's process.</param>
public sealed record TargetGame(string ProfileId, string Name, int ProcessId);

/// <summary>Why the Target Game changed.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TargetChangeReason>))]
public enum TargetChangeReason
{
    /// <summary>Another game held focus long enough to take over.</summary>
    Focus,

    /// <summary>The Target's process exited.</summary>
    Exit,

    /// <summary>The Target was set to be ignored.</summary>
    Ignored,

    /// <summary>A game was detected while there was no Target, or (the tracer rule) any newly detected game.</summary>
    Detected,

    /// <summary>A game already running when the engine started.</summary>
    [JsonStringEnumMemberName("cold-start")]
    ColdStart,
}

/// <summary><c>target.changed</c>: the Target Game went from <see cref="From"/> to <see cref="To"/> (either may be none).</summary>
public sealed record TargetChanged(TargetGame? From, TargetGame? To, TargetChangeReason Reason);

/// <summary>
/// Game messages (epic-game-detection-profiles): Game Detection, the Target Game and Game Profiles.
/// The area's payloads, its own JSON context and its entries in these lists are added here only (AD-22).
/// </summary>
public static class GameMessages
{
    /// <summary>The Target Game changed; the SessionOrchestrator is its only producer (AD-19).</summary>
    public static EventType<TargetChanged> TargetChanged { get; } = new(
        "target.changed",
        GameJsonContext.Default.TargetChanged,
        "targetGame",
        (snapshot, changed) => snapshot with { TargetGame = changed.To });

    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [TargetChanged];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TargetChanged))]
[JsonSerializable(typeof(TargetGame))]
public sealed partial class GameJsonContext : JsonSerializerContext;
