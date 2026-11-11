using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

/// <summary>The Target Game: the one game the engine captures for; ProfileId is its executable's lowercase final path.</summary>
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

    /// <summary>A game was detected while there was no Target.</summary>
    Detected,

    /// <summary>A game already running when the engine started.</summary>
    [JsonStringEnumMemberName("cold-start")]
    ColdStart,
}

/// <summary><c>target.changed</c>: the Target Game went from <see cref="From"/> to <see cref="To"/> (either may be none).</summary>
public sealed record TargetChanged(TargetGame? From, TargetGame? To, TargetChangeReason Reason);

/// <summary>A running game that Game Detection found, known until its process exits; it is not the Target Game.</summary>
public sealed record DetectedGame(string ProfileId, string Name, int ProcessId);

/// <summary><c>game.add</c>: hand-adds an executable to <c>manualGames</c>; the engine makes the path canonical.</summary>
public sealed record GameAdd(string Path);

/// <summary>The reply to <c>game.add</c>: the canonical path now in <c>manualGames</c>.</summary>
public sealed record GameAdded(string Path);

/// <summary>Game messages: Game Detection, the Target Game and Game Profiles; this area's payloads are added here only.</summary>
public static class GameMessages
{
    /// <summary>The Target Game changed; the SessionOrchestrator is its only producer.</summary>
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

    /// <summary>Adds an executable to <c>manualGames</c> once; a path that isn't an existing executable is refused.</summary>
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
