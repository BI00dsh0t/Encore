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

    /// <summary>The Target Game it was recorded from exited, or its window closed.</summary>
    public const string GameExit = "game-exit";

    /// <summary>The Target Game it was recorded from was set Ignored.</summary>
    public const string Ignored = "ignored";

    /// <summary>The engine shut down (Quit).</summary>
    public const string Shutdown = "shutdown";

    /// <summary>A forced encoder restart ended it; a new Recording starts at once in another file.</summary>
    public const string EncoderRestart = "encoder-restart";

    /// <summary>Its disk filled (R5); the file ends at its last complete fragment. Equal to <see cref="ErrorCodes.DiskFull"/>.</summary>
    public const string DiskFull = "disk-full";

    /// <summary>The PC went to sleep; the file ends at its last fragment and the Recording isn't restarted.</summary>
    public const string Suspend = "suspend";

    /// <summary>
    /// The Monitor Recording's monitor was removed (T-03); the file ends at its last frame. Equal to
    /// <see cref="ErrorCodes.MonitorRemoved"/>.
    /// </summary>
    public const string MonitorRemoved = "monitor-removed";
}

/// <summary>
/// One attached display monitor, as <c>monitors.list</c> reports it: its GDI device name (such as
/// <c>\\.\DISPLAY1</c>, the value <c>monitorRecordingMonitor</c> holds), its friendly name, and its
/// current resolution in pixels.
/// </summary>
public sealed record DisplayMonitor(string DeviceName, string FriendlyName, int Width, int Height, bool Primary);

/// <summary><c>monitors.list</c>'s reply: every attached monitor, the primary first.</summary>
public sealed record MonitorList(IReadOnlyList<DisplayMonitor> Monitors);

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

    /// <summary>Lists the attached monitors, for the Monitor Recording monitor choice (R6).</summary>
    public static CommandType<Empty, MonitorList> MonitorsList { get; } =
        new("monitors.list", RecordingJsonContext.Default.Empty, RecordingJsonContext.Default.MonitorList);

    /// <summary>
    /// The attached monitors changed (T-03): one was connected or removed, the primary moved, or a
    /// mode changed. Carries the new list, as <c>monitors.list</c> would reply; the snapshot keeps it.
    /// </summary>
    public static EventType<MonitorList> MonitorsChanged { get; } = new(
        "monitors.changed",
        RecordingJsonContext.Default.MonitorList,
        "monitors",
        (snapshot, monitors) => snapshot with { Monitors = monitors });

    public static IReadOnlyList<CommandType> Commands { get; } = [MonitorsList];

    public static IReadOnlyList<EventType> Events { get; } = [Started, Stopped, MonitorsChanged];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RecordingStarted))]
[JsonSerializable(typeof(RecordingStopped))]
[JsonSerializable(typeof(DisplayMonitor))]
[JsonSerializable(typeof(MonitorList))]
[JsonSerializable(typeof(Empty))]
public sealed partial class RecordingJsonContext : JsonSerializerContext;
