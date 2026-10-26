using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Messages;

/// <summary><c>settings.update</c>'s result: the revision it committed.</summary>
public sealed record SettingsUpdateResult(long Revision);

/// <summary><c>settings.changed</c> (AD-4): the whole committed document.</summary>
public sealed record SettingsChanged(long Revision, SettingsDocument Document);

/// <summary>Settings messages (AD-4). The DTOs are in <see cref="ReplayBuffer.Contracts.Settings"/>.</summary>
public static class SettingsMessages
{
    public static CommandType<SettingsUpdate, SettingsUpdateResult> Update { get; } =
        new("settings.update", SettingsJsonContext.Default.SettingsUpdate, SettingsMessagesJsonContext.Default.SettingsUpdateResult);

    public static EventType<SettingsChanged> Changed { get; } = new(
        "settings.changed",
        SettingsMessagesJsonContext.Default.SettingsChanged,
        "settings",
        (snapshot, changed) => snapshot with { Settings = SettingsMaps.WithOrdinalKeys(changed.Document) });

    public static IReadOnlyList<CommandType> Commands { get; } = [Update];

    public static IReadOnlyList<EventType> Events { get; } = [Changed];
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SettingsUpdateResult))]
[JsonSerializable(typeof(SettingsChanged))]
public sealed partial class SettingsMessagesJsonContext : JsonSerializerContext;
