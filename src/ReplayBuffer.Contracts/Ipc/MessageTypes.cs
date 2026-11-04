using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Contracts.Ipc;

/// <summary>A command (AD-3): sent by a client, answered by exactly one reply.</summary>
public abstract class CommandType(string name)
{
    /// <summary>The <c>noun.verb</c> type name.</summary>
    public string Name { get; } = name;

    public override string ToString() => Name;
}

/// <summary>A command whose arguments are a <typeparamref name="TArgs"/> and whose result is a <typeparamref name="TResult"/>.</summary>
public sealed class CommandType<TArgs, TResult>(string name, JsonTypeInfo<TArgs> argsType, JsonTypeInfo<TResult> resultType)
    : CommandType(name)
{
    public JsonTypeInfo<TArgs> ArgsType { get; } = argsType;

    public JsonTypeInfo<TResult> ResultType { get; } = resultType;
}

/// <summary>
/// An event (AD-3): sent by the engine with a <c>seq</c>. Every event changes one field of
/// <see cref="StateSnapshot"/>, through one <c>Apply</c> that the engine and its clients share.
/// </summary>
public abstract class EventType(string name, string snapshotField, bool coalesces)
{
    /// <summary>The <c>noun.verb</c> type name.</summary>
    public string Name { get; } = name;

    /// <summary>The JSON name of the <see cref="StateSnapshot"/> field this event changes.</summary>
    public string SnapshotField { get; } = snapshotField;

    /// <summary>Whether a connection's queue keeps only the latest of these (AD-3).</summary>
    public bool Coalesces { get; } = coalesces;

    /// <summary>Applies a payload read from JSON; <c>Seq</c> is the caller's to set.</summary>
    /// <exception cref="JsonException">The payload doesn't fit the event's type.</exception>
    public abstract StateSnapshot ApplyJson(StateSnapshot snapshot, JsonElement payload);

    public override string ToString() => Name;
}

/// <summary>An event whose payload is a <typeparamref name="T"/>.</summary>
public sealed class EventType<T>(
    string name,
    JsonTypeInfo<T> payloadType,
    string snapshotField,
    Func<StateSnapshot, T, StateSnapshot> apply,
    bool coalesces = false)
    : EventType(name, snapshotField, coalesces)
{
    public JsonTypeInfo<T> PayloadType { get; } = payloadType;

    /// <summary>The snapshot after this event; <c>Seq</c> is the caller's to set.</summary>
    public StateSnapshot Apply(StateSnapshot snapshot, T payload) => apply(snapshot, payload);

    /// <exception cref="JsonException">The payload doesn't fit <typeparamref name="T"/>.</exception>
    public T Read(JsonElement payload) =>
        payload.Deserialize(PayloadType) ?? throw new JsonException($"'{Name}' has a null payload.");

    public override StateSnapshot ApplyJson(StateSnapshot snapshot, JsonElement payload) => Apply(snapshot, Read(payload));
}

/// <summary>
/// Every command and event, gathered from the message file of each area. An area adds its
/// messages to its own file's lists, never here (AD-22).
/// </summary>
public static class MessageRegistry
{
    public static IReadOnlyList<CommandType> Commands { get; } =
    [
        .. EngineMessages.Commands,
        .. SettingsMessages.Commands,
        .. CaptureMessages.Commands,
        .. FeedbackMessages.Commands,
        .. AudioMessages.Commands,
        .. GameMessages.Commands,
        .. LibraryMessages.Commands,
        .. RecordingMessages.Commands,
        .. HotkeyMessages.Commands,
        .. DevMessages.Commands,
    ];

    public static IReadOnlyList<EventType> Events { get; } =
    [
        .. EngineMessages.Events,
        .. SettingsMessages.Events,
        .. CaptureMessages.Events,
        .. FeedbackMessages.Events,
        .. AudioMessages.Events,
        .. GameMessages.Events,
        .. LibraryMessages.Events,
        .. RecordingMessages.Events,
        .. HotkeyMessages.Events,
        .. DevMessages.Events,
    ];

    private static readonly Dictionary<string, EventType> EventsByName = Events.ToDictionary(e => e.Name, StringComparer.Ordinal);

    /// <summary>The registered event named <paramref name="name"/>, or <c>null</c>.</summary>
    public static EventType? FindEvent(string name) => EventsByName.GetValueOrDefault(name);
}
