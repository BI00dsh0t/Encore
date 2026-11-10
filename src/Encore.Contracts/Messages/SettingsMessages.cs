using System.Text.Json;
using System.Text.Json.Serialization;
using Encore.Contracts.Ipc;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Messages;

/// <summary><c>settings.update</c>'s result: the revision it committed.</summary>
public sealed record SettingsUpdateResult(long Revision);

/// <summary><c>settings.changed</c>: the whole committed document.</summary>
public sealed record SettingsChanged(long Revision, SettingsDocument Document);

/// <summary>Settings messages. The DTOs are in <see cref="Encore.Contracts.Settings"/>.</summary>
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
