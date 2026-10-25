using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayBuffer.Contracts.Settings;

/// <summary>
/// Source-generated serialization for every settings type (AD-14: no reflection). camelCase
/// properties, camelCase enum names, nullable annotations and required parameters enforced.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(SettingsUpdate))]
[JsonSerializable(typeof(EffectiveSettings))]
[JsonSerializable(typeof(VideoSettings))]
[JsonSerializable(typeof(TrackLayout))]
[JsonSerializable(typeof(ReplayStorage))]
[JsonSerializable(typeof(HotkeySettings))]
[JsonSerializable(typeof(RecordHotkeyMode))]
[JsonSerializable(typeof(FeedbackSounds))]
[JsonSerializable(typeof(CleanupSettings))]
[JsonSerializable(typeof(AppliesAt))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
