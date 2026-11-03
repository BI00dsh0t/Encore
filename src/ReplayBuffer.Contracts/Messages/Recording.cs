using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// <c>recording.started</c>: a Recording is being written to <see cref="Path"/> (R7). The
/// SessionOrchestrator is its only producer (AD-19); <c>recording.stopped</c> follows.
/// </summary>
public sealed record RecordingStarted(string Path);

/// <summary>
/// <c>recording.stopped</c>: the Recording at <see cref="Path"/> has ended (R7), and its file is
/// complete or, when nothing was written, gone. <see cref="Reason"/> is one of
/// <see cref="RecordingStopReasons"/>, or an error code when its writer failed. The
/// SessionOrchestrator is its only producer (AD-19).
/// </summary>
public sealed record RecordingStopped(string Path, string Reason);

/// <summary>Why a Recording stopped, as <see cref="RecordingStopped.Reason"/> carries it.</summary>
public static class RecordingStopReasons
{
    /// <summary>A Record Hotkey press stopped it.</summary>
    public const string Stopped = "stopped";

    /// <summary>The session it was recorded from ended.</summary>
    public const string SessionEnded = "session-ended";
}

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
    /// <summary>A Recording started (R7); the snapshot keeps the last one.</summary>
    public static EventType<RecordingStarted> Started { get; } = new(
        "recording.started",
        RecordingJsonContext.Default.RecordingStarted,
        "lastRecordingStarted",
        (snapshot, started) => snapshot with { LastRecordingStarted = started });

    /// <summary>A Recording stopped (R7); the snapshot keeps the last one.</summary>
    public static EventType<RecordingStopped> Stopped { get; } = new(
        "recording.stopped",
        RecordingJsonContext.Default.RecordingStopped,
        "lastRecordingStopped",
        (snapshot, stopped) => snapshot with { LastRecordingStopped = stopped });

    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [Started, Stopped];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RecordingStarted))]
[JsonSerializable(typeof(RecordingStopped))]
public sealed partial class RecordingJsonContext : JsonSerializerContext;
