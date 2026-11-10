using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;

namespace Encore.Contracts.Messages;

/// <c>recording.started</c>: a Recording is being written to <see cref="Path"/>; <c>recording.stopped</c> follows.
public sealed record RecordingStarted(string Path);

/// <c>recording.stopped</c>: the Recording has ended; Reason is a stop reason, or an error code when its writer failed.
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

    /// <summary>Its disk filled; the file ends at its last complete fragment. Equal to <see cref="ErrorCodes.DiskFull"/>.</summary>
    public const string DiskFull = "disk-full";

    /// <summary>The PC went to sleep; the file ends at its last fragment and the Recording isn't restarted.</summary>
    public const string Suspend = "suspend";

    /// <summary>The Monitor Recording's monitor was removed; the file ends at its last frame.</summary>
    public const string MonitorRemoved = "monitor-removed";
}

/// <summary>One attached monitor as <c>monitors.list</c> reports it; the optional fields are null from an older engine.</summary>
public sealed record DisplayMonitor(
    string DeviceName,
    string FriendlyName,
    int Width,
    int Height,
    bool Primary,
    string? MonitorId = null,
    int? X = null,
    int? Y = null,
    int? RefreshHz = null,
    bool Hdr = false);

/// <summary><c>monitors.list</c>'s reply: every attached monitor, the primary first.</summary>
public sealed record MonitorList(IReadOnlyList<DisplayMonitor> Monitors);

/// <summary>Recording messages: Recordings and Monitor Recording. After an engine restart only Rewind resumes.</summary>
public static class RecordingMessages
{
    /// <summary>A Recording started; the snapshot keeps the last one.</summary>
    public static EventType<RecordingStarted> Started { get; } = new(
        "recording.started",
        RecordingJsonContext.Default.RecordingStarted,
        "lastRecordingStarted",
        (snapshot, started) => snapshot with { LastRecordingStarted = started });

    /// <summary>A Recording stopped; the snapshot keeps the last one.</summary>
    public static EventType<RecordingStopped> Stopped { get; } = new(
        "recording.stopped",
        RecordingJsonContext.Default.RecordingStopped,
        "lastRecordingStopped",
        (snapshot, stopped) => snapshot with { LastRecordingStopped = stopped });

    /// <summary>Lists the attached monitors, for the Monitor Recording monitor choice.</summary>
    public static CommandType<Empty, MonitorList> MonitorsList { get; } =
        new("monitors.list", RecordingJsonContext.Default.Empty, RecordingJsonContext.Default.MonitorList);

    /// <summary>The attached monitors changed: one was connected or removed, the primary moved, or a mode changed.</summary>
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
