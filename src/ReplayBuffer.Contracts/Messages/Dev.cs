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
