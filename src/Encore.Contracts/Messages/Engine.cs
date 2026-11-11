using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

/// <summary>Everything the App renders: <c>state.snapshot</c> sends it whole first, then each event changes one field.</summary>
public sealed record StateSnapshot
{
    private readonly HotkeyStatus _hotkeys = HotkeyStatus.Unbound;
    private readonly VideoOptions _videoOptions = VideoOptions.Unprobed;
    private readonly AudioDeviceList _devices = AudioDeviceList.Empty;
    private readonly MeterLevels _meters = MeterLevels.Empty;
    private readonly IReadOnlyList<LibraryItem> _library = [];

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

    /// <summary>Each Hotkey action's binding and whether it is registered; all unbound before the first status.</summary>
    public HotkeyStatus Hotkeys
    {
        get => _hotkeys;

        // The generated deserializer passes null for a missing field.
        init => _hotkeys = value ?? HotkeyStatus.Unbound;
    }

    /// <summary>The last Hotkey press, or <c>null</c> before one.</summary>
    public HotkeyPress? LastHotkeyPress { get; init; }

    /// <summary>The last Clip saved, or <c>null</c> before one.</summary>
    public ClipSaved? LastClip { get; init; }

    /// <summary>The last accepted save press, or <c>null</c> before one.</summary>
    public ClipSaving? LastClipSaving { get; init; }

    /// <summary>The last save that made no Clip, or <c>null</c> before one.</summary>
    public ClipFailed? LastClipFailure { get; init; }

    /// <summary>The last Recording started, or <c>null</c> before one.</summary>
    public RecordingStarted? LastRecordingStarted { get; init; }

    /// <summary>The last Recording stopped, or <c>null</c> before one.</summary>
    public RecordingStopped? LastRecordingStopped { get; init; }

    /// <summary>What the <c>video</c> key can be set to on this PC; unprobed until <c>capture.videoOptions</c>.</summary>
    public VideoOptions VideoOptions
    {
        get => _videoOptions;

        // The generated deserializer passes null for a missing field.
        init => _videoOptions = value ?? VideoOptions.Unprobed;
    }

    /// <summary>The Global settings' storage estimate, or <c>null</c> before the engine has made one.</summary>
    public StorageEstimate? Estimate { get; init; }

    /// <summary>The audio devices the App can offer; empty until the engine lists them.</summary>
    public AudioDeviceList Devices
    {
        get => _devices;

        // The generated deserializer passes null for a missing field.
        init => _devices = value ?? AudioDeviceList.Empty;
    }

    /// <summary>The watched Tracks' latest levels; empty while nothing is watched.</summary>
    public MeterLevels Meters
    {
        get => _meters;

        // The generated deserializer passes null for a missing field.
        init => _meters = value ?? MeterLevels.Empty;
    }
    /// <summary>The Target Game, or <c>null</c> when there is none.</summary>
    public TargetGame? TargetGame { get; init; }

    /// <summary>The running games Game Detection knows, in detection order; for display only.</summary>
    public IReadOnlyList<DetectedGame>? DetectedGames { get; init; }

    /// <summary>The Library's items, in no particular order; the App sorts them.</summary>
    public IReadOnlyList<LibraryItem> Library
    {
        get => _library;

        // The generated deserializer passes null for a missing field.
        init => _library = value ?? [];
    }

    /// <summary>Whether Cleanup is held off: the index was rebuilt after a corrupt file, and that warning isn't dismissed yet.</summary>
    public bool CleanupBlocked { get; init; }

    /// <summary>The absolute Output Folder the Library indexes, which item paths are relative to; <c>null</c> when unknown.</summary>
    public string? LibraryFolder { get; init; }

    /// <summary>The attached monitors as of the last <c>monitors.changed</c>, the primary first; <c>null</c> before any change.</summary>
    public MonitorList? Monitors { get; init; }

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
        Hotkeys = HotkeyStatus.Unbound,
        VideoOptions = VideoOptions.Unprobed,
        Devices = AudioDeviceList.Empty,
        Meters = MeterLevels.Empty,
        TargetGame = null,
        DetectedGames = [],
        Library = [],
    };
}

/// <summary>A payload with no fields, <c>{}</c>.</summary>
public sealed record Empty
{
    public static Empty Value { get; } = new();
}

/// <summary><c>engine.heartbeat</c>: the engine is alive; sent every <see cref="Supervision.HeartbeatInterval"/>.</summary>
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

/// <summary><c>app.attach</c>'s arguments: the App's process to supervise, and why the App restarted this engine, if it did.</summary>
public sealed record AppAttach(int Pid, EngineRestartReason? EngineRestart = null);

/// <summary><c>app.attached</c>: the App the engine now supervises.</summary>
public sealed record AppAttached(int Pid);

/// <summary>The engine's own messages.</summary>
public static class EngineMessages
{
    /// <summary>The first frame on every connection; its payload is a <see cref="StateSnapshot"/>.</summary>
    public const string StateSnapshotType = "state.snapshot";

    /// <summary>Ends the engine once the reply is written.</summary>
    public static CommandType<Empty, Empty> Shutdown { get; } =
        new("engine.shutdown", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    /// <summary>The App registers for supervision; the engine raises <c>engine-restarted</c> when it carries a reason.</summary>
    public static CommandType<AppAttach, Empty> Attach { get; } =
        new("app.attach", EngineJsonContext.Default.AppAttach, EngineJsonContext.Default.Empty);

    /// <summary>The engine is alive. Coalescing: a connection's queue keeps only the latest.</summary>
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
