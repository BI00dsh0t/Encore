using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.Contracts.Settings;

/// <summary>Source-generated serialization for every settings type, with no reflection; camelCase names, nulls enforced.</summary>
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
[JsonSerializable(typeof(OverlayDotMode))]
[JsonSerializable(typeof(AppTheme))]
[JsonSerializable(typeof(FeedbackSounds))]
[JsonSerializable(typeof(CleanupSettings))]
[JsonSerializable(typeof(MonitorRecordingScreens))]
[JsonSerializable(typeof(AppliesAt))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
