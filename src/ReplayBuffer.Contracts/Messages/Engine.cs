using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>
/// Everything the App renders (AD-3). <c>state.snapshot</c> sends it whole as the first frame on a
/// connection; each event then changes one field. An epic adds its field here, with a default, and
/// the event that changes it in its own message file.
/// </summary>
public sealed record StateSnapshot
{
    /// <summary>Identifies one engine run; a new value means a restarted engine.</summary>
    public required string EngineInstanceId { get; init; }

    public required int Pid { get; init; }

    /// <summary>The <c>seq</c> of the last event this snapshot includes.</summary>
    public required long Seq { get; init; }

    public required SettingsDocument Settings { get; init; }

    public required CaptureState Capture { get; init; }

    /// <summary>The active warnings, oldest first.</summary>
    public required IReadOnlyList<Warning> Warnings { get; init; }

    /// <summary>The fields that identify the snapshot rather than state; no event changes them.</summary>
    public static IReadOnlyList<string> IdentityFields { get; } = ["engineInstanceId", "pid", "seq"];

    /// <summary>A fresh engine's state, before any event.</summary>
    public static StateSnapshot Initial(string engineInstanceId, int pid, SettingsDocument settings) => new()
    {
        EngineInstanceId = engineInstanceId,
        Pid = pid,
        Seq = 0,
        Settings = settings,
        Capture = CaptureState.Initial,
        Warnings = [],
    };
}

/// <summary>A payload with no fields, <c>{}</c>.</summary>
public sealed record Empty
{
    public static Empty Value { get; } = new();
}

/// <summary>The engine's own messages.</summary>
public static class EngineMessages
{
    /// <summary>The first frame on every connection; its payload is a <see cref="StateSnapshot"/>.</summary>
    public const string StateSnapshotType = "state.snapshot";

    /// <summary>Ends the engine once the reply is written (AD-16).</summary>
    public static CommandType<Empty, Empty> Shutdown { get; } =
        new("engine.shutdown", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    public static IReadOnlyList<CommandType> Commands { get; } = [Shutdown];

    public static IReadOnlyList<EventType> Events { get; } = [];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StateSnapshot))]
[JsonSerializable(typeof(Empty))]
public sealed partial class EngineJsonContext : JsonSerializerContext;
