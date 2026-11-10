using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

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
/// A running game Game Detection found (any source), known until its process exits. Informational:
/// it isn't the Target Game, which only <c>target.changed</c> sets (AD-19).
/// </summary>
/// <param name="ProfileId">The executable as <c>Canonicalize</c> made it (AD-18).</param>
/// <param name="Name">The game's display name; the executable's file name until Game Profiles name it.</param>
/// <param name="ProcessId">The game's process.</param>
public sealed record DetectedGame(string ProfileId, string Name, int ProcessId);

/// <summary><c>game.add</c>: hand-adds an executable to <c>manualGames</c> (R1). The engine canonicalizes <see cref="Path"/> (AD-18).</summary>
/// <param name="Path">The executable's path as typed or browsed.</param>
public sealed record GameAdd(string Path);

/// <summary>The reply to <c>game.add</c>: the canonical path now in <c>manualGames</c>.</summary>
public sealed record GameAdded(string Path);

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

    /// <summary>A game was detected; it replaces any entry with the same process. Published by Game Detection.</summary>
    public static EventType<DetectedGame> Detected { get; } = new(
        "game.detected",
        GameJsonContext.Default.DetectedGame,
        "detectedGames",
        (snapshot, game) => snapshot with { DetectedGames = [.. Without(snapshot.DetectedGames, game.ProcessId), game] });

    /// <summary>A detected game's process exited; its entry is removed. Published by Game Detection.</summary>
    public static EventType<DetectedGame> Exited { get; } = new(
        "game.exited",
        GameJsonContext.Default.DetectedGame,
        "detectedGames",
        (snapshot, game) => snapshot with { DetectedGames = [.. Without(snapshot.DetectedGames, game.ProcessId)] });

    /// <summary>
    /// Adds an executable to <c>manualGames</c> once, canonical; a path that isn't an existing executable
    /// is refused with <c>invalid-arguments</c>.
    /// </summary>
    public static CommandType<GameAdd, GameAdded> Add { get; } =
        new("game.add", GameJsonContext.Default.GameAdd, GameJsonContext.Default.GameAdded);

    public static IReadOnlyList<CommandType> Commands { get; } = [Add];

    public static IReadOnlyList<EventType> Events { get; } = [TargetChanged, Detected, Exited];

    private static IEnumerable<DetectedGame> Without(IReadOnlyList<DetectedGame>? games, int processId) =>
        (games ?? []).Where(g => g.ProcessId != processId);
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TargetChanged))]
[JsonSerializable(typeof(TargetGame))]
[JsonSerializable(typeof(DetectedGame))]
[JsonSerializable(typeof(GameAdd))]
[JsonSerializable(typeof(GameAdded))]
public sealed partial class GameJsonContext : JsonSerializerContext;
