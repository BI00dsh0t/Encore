using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary>The Replay Buffer's state in <see cref="CaptureState"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ReplayState>))]
public enum ReplayState
{
    Off,
    Buffering,
    Draining,
}

/// <summary>A Recording's or Monitor Recording's state in <see cref="CaptureState"/>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RecordingState>))]
public enum RecordingState
{
    Off,
    Running,
}

/// <summary>
/// <c>capture.stateChanged</c>, one of the two shapes the Overlay and status line render (AD-3).
/// The SessionOrchestrator is its only producer (AD-19).
/// </summary>
public sealed record CaptureState(
    ReplayState Replay,
    RecordingState Recording,
    RecordingState MonitorRecording,
    bool MicMuted,
    DateTimeOffset? CooldownUntil)
{
    /// <summary>Nothing running, and the mic muted, as AD-5 restores it with no saved state.</summary>
    public static CaptureState Initial { get; } = new(ReplayState.Off, RecordingState.Off, RecordingState.Off, MicMuted: true, CooldownUntil: null);
}

/// <summary>Capture messages: the Replay Buffer, the capture session and the shared capture state.</summary>
public static class CaptureMessages
{
    public static EventType<CaptureState> StateChanged { get; } = new(
        "capture.stateChanged",
        CaptureJsonContext.Default.CaptureState,
        "capture",
        (snapshot, state) => snapshot with { Capture = state });

    public static IReadOnlyList<CommandType> Commands { get; } = [];

    public static IReadOnlyList<EventType> Events { get; } = [StateChanged];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CaptureState))]
public sealed partial class CaptureJsonContext : JsonSerializerContext;
