using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.Engine.Settings;

/// <summary>The file's shape; a missing <c>micMuted</c> makes it unreadable, and an empty list is left out when writing.</summary>
/// <param name="NonGamesChecked">Whether the one-time check of the Game Profiles for non-games has run.</param>
internal sealed record RuntimeState(bool? MicMuted, IReadOnlyList<string>? OpenRecordings = null, bool? NonGamesChecked = null);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuntimeState))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext;
