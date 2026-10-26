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

    /// <summary>When the engine last published <c>engine.heartbeat</c>, or <c>null</c> before the first.</summary>
    public DateTimeOffset? HeartbeatAt { get; init; }

    /// <summary>The process id of the App that last sent <c>app.attach</c>, or <c>null</c> before one has.</summary>
    public int? AppPid { get; init; }

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

/// <summary><c>engine.heartbeat</c> (AD-16): the engine is alive; sent every <see cref="Supervision.HeartbeatInterval"/>.</summary>
public sealed record EngineHeartbeat(DateTimeOffset At);

/// <summary>Why the App restarted the engine it attaches to.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<EngineRestartReason>))]
public enum EngineRestartReason
{
    /// <summary>The engine exited with a non-zero code, or was killed from outside.</summary>
    Crashed,

    /// <summary>The App killed the engine after it stopped answering.</summary>
    Hung,
}

/// <summary>
/// <c>app.attach</c>'s arguments (AD-16): the App's process, which the engine relaunches if it exits
/// with no <c>engine.shutdown</c>, and why the App restarted this engine, if it did.
/// </summary>
public sealed record AppAttach(int Pid, EngineRestartReason? EngineRestart = null);

/// <summary><c>app.attached</c>: the App the engine now supervises.</summary>
public sealed record AppAttached(int Pid);

/// <summary>The engine's own messages.</summary>
public static class EngineMessages
{
    /// <summary>The first frame on every connection; its payload is a <see cref="StateSnapshot"/>.</summary>
    public const string StateSnapshotType = "state.snapshot";

    /// <summary>Ends the engine once the reply is written (AD-16).</summary>
    public static CommandType<Empty, Empty> Shutdown { get; } =
        new("engine.shutdown", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    /// <summary>The App registers for supervision (AD-16); the engine raises <c>engine-restarted</c> when it carries a reason.</summary>
    public static CommandType<AppAttach, Empty> Attach { get; } =
        new("app.attach", EngineJsonContext.Default.AppAttach, EngineJsonContext.Default.Empty);

    /// <summary>The engine is alive (AD-16). Coalescing: a connection's queue keeps only the latest.</summary>
    public static EventType<EngineHeartbeat> Heartbeat { get; } = new(
        "engine.heartbeat",
        EngineJsonContext.Default.EngineHeartbeat,
        "heartbeatAt",
        (snapshot, heartbeat) => snapshot with { HeartbeatAt = heartbeat.At },
        coalesces: true);

    public static EventType<AppAttached> Attached { get; } = new(
        "app.attached",
        EngineJsonContext.Default.AppAttached,
        "appPid",
        (snapshot, attached) => snapshot with { AppPid = attached.Pid });

    public static IReadOnlyList<CommandType> Commands { get; } = [Shutdown, Attach];

    public static IReadOnlyList<EventType> Events { get; } = [Heartbeat, Attached];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StateSnapshot))]
[JsonSerializable(typeof(Empty))]
[JsonSerializable(typeof(EngineHeartbeat))]
[JsonSerializable(typeof(AppAttach))]
[JsonSerializable(typeof(AppAttached))]
public sealed partial class EngineJsonContext : JsonSerializerContext;
