using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>An outcome <c>dev.simulate</c> injects.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SimulatedEvent>))]
public enum SimulatedEvent
{
    /// <summary>The stub Recording state turns on, through the SessionOrchestrator (AD-19).</summary>
    RecordingStarted,

    /// <summary>The stub Recording state turns off.</summary>
    RecordingStopped,

    /// <summary>The volume is full: a <c>disk-full</c> notice.</summary>
    DiskFull,

    /// <summary>A save made no Clip: <c>clip.failed</c> with <see cref="ErrorCodes.SaveFailed"/>.</summary>
    SaveFailed,

    /// <summary>A save made a Clip: the <c>clip-saved</c> notice only. No <c>clip.saved</c> is published, as no file exists.</summary>
    ClipSaved,

    /// <summary>The engine hangs: its heartbeat stops and its event hub freezes, so the App kills and relaunches it (AD-16).</summary>
    EngineHang,

    /// <summary>
    /// The GPU driver resets: every live capture device reports itself lost, so the sessions on it
    /// raise <see cref="ErrorCodes.GpuReset"/> and rebuild.
    /// </summary>
    GpuReset,
}

/// <summary><c>dev.simulate</c>'s arguments.</summary>
public sealed record DevSimulate(SimulatedEvent Event);

/// <summary>
/// Dev messages: commands for exercising the engine before the features behind them exist. The
/// installed instance refuses them with <see cref="ErrorCodes.NotDevInstance"/>.
/// </summary>
public static class DevMessages
{
    /// <summary>Injects <see cref="DevSimulate.Event"/> as if it had happened.</summary>
    public static CommandType<DevSimulate, Empty> Simulate { get; } =
        new("dev.simulate", DevJsonContext.Default.DevSimulate, DevJsonContext.Default.Empty);

    public static IReadOnlyList<CommandType> Commands { get; } = [Simulate];

    public static IReadOnlyList<EventType> Events { get; } = [];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DevSimulate))]
[JsonSerializable(typeof(Empty))]
public sealed partial class DevJsonContext : JsonSerializerContext;
