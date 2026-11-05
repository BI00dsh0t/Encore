using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayBuffer.Contracts.Settings;

/// <summary>
/// The settings document (AD-4), immutable. Values are raw JSON per key, so the document holds
/// every catalog key's value and keeps keys it doesn't know; <see cref="Keys"/> gives them types.
/// Maps are keyed ordinally.
/// </summary>
public sealed record SettingsDocument
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The empty document at revision 0: every key takes its default.</summary>
    public static SettingsDocument Empty { get; } = new();

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Increases by one with every committed update.</summary>
    public long Revision { get; init; }

    /// <summary>Global values by key name.</summary>
    public ImmutableSortedDictionary<string, JsonElement> Global { get; init; } = SettingsMaps.EmptyValues;

    /// <summary>Game Profiles by profile id (AD-18 <c>Canonicalize</c>).</summary>
    public ImmutableSortedDictionary<string, GameProfile> Profiles { get; init; } = SettingsMaps.EmptyProfiles;
}

/// <summary>A Game Profile: its attributes, read live (AD-5), and its overrides of overridable keys.</summary>
public sealed record GameProfile
{
    public required string Name { get; init; }

    public bool Ignored { get; init; }

    public bool RecordingsEnabled { get; init; }

    /// <summary>
    /// The profile's folder under the Output Folder (AD-21). Assigned once by the engine when it
    /// first commits the profile, and never changed after; clients can't set it. Null until then.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Folder { get; init; }

    /// <summary>Override values by key name; only overridable keys take effect.</summary>
    public ImmutableSortedDictionary<string, JsonElement> Overrides { get; init; } = SettingsMaps.EmptyValues;
}

/// <summary>The empty, ordinally keyed maps of a document.</summary>
public static class SettingsMaps
{
    public static ImmutableSortedDictionary<string, JsonElement> EmptyValues { get; } =
        ImmutableSortedDictionary.Create<string, JsonElement>(StringComparer.Ordinal);

    public static ImmutableSortedDictionary<string, GameProfile> EmptyProfiles { get; } =
        ImmutableSortedDictionary.Create<string, GameProfile>(StringComparer.Ordinal);

    /// <summary>The document with every map keyed ordinally, as a freshly deserialized one may not be.</summary>
    public static SettingsDocument WithOrdinalKeys(SettingsDocument document) => document with
    {
        Global = document.Global.WithComparers(StringComparer.Ordinal),
        Profiles = document.Profiles
            .Select(p => KeyValuePair.Create(p.Key, p.Value with { Overrides = p.Value.Overrides.WithComparers(StringComparer.Ordinal) }))
            .ToImmutableSortedDictionary(StringComparer.Ordinal),
    };
}

/// <summary>Which layer a settings op changes.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SettingsScope>))]
public enum SettingsScope
{
    Global,
    Profile,
}

/// <summary>What a settings op does to its key.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SettingsOpKind>))]
public enum SettingsOpKind
{
    Set,
    Remove,
}

/// <summary>
/// <c>settings.update</c> (AD-4): ops applied all-or-nothing, only when the document is still at
/// <see cref="ExpectedRevision"/>.
/// </summary>
public sealed record SettingsUpdate(long ExpectedRevision, IReadOnlyList<SettingsOp> Ops);

/// <summary>
/// One op. A profile-scope op names either an overridable key or a profile attribute
/// (<see cref="ProfileAttributes"/>). A <c>set</c> value replaces the key whole.
/// </summary>
public sealed record SettingsOp(
    SettingsScope Scope,
    string? ProfileId,
    string Key,
    SettingsOpKind Op,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Value = default)
{
    public static SettingsOp SetGlobal(string key, JsonElement value) => new(SettingsScope.Global, null, key, SettingsOpKind.Set, value);

    public static SettingsOp RemoveGlobal(string key) => new(SettingsScope.Global, null, key, SettingsOpKind.Remove);

    public static SettingsOp SetProfile(string profileId, string key, JsonElement value) => new(SettingsScope.Profile, profileId, key, SettingsOpKind.Set, value);

    public static SettingsOp RemoveProfile(string profileId, string key) => new(SettingsScope.Profile, profileId, key, SettingsOpKind.Remove);
}

/// <summary>
/// Every key resolved for one Game Profile (or Global, for no profile), with the revision of the
/// document it came from. Sessions snapshot this when they start (AD-5).
/// </summary>
public sealed record EffectiveSettings
{
    public required string? ProfileId { get; init; }
    public required long SettingsRevision { get; init; }
    public required VideoSettings Video { get; init; }
    public required TrackLayout TrackLayout { get; init; }
    public required int BufferWindow { get; init; }
    public required ReplayStorage ReplayStorage { get; init; }
    public required string? RingPath { get; init; }
    public required int SaveCooldown { get; init; }
    public required string? OutputFolder { get; init; }
    public required HotkeySettings Hotkeys { get; init; }
    public required RecordHotkeyMode RecordHotkeyMode { get; init; }
    public required string? MonitorRecordingMonitor { get; init; }

    /// <summary>The screens a Monitor Recording composites (story 11.2); null reads <see cref="MonitorRecordingMonitor"/>.</summary>
    public MonitorRecordingScreens? MonitorRecordingScreens { get; init; }
    public required FeedbackSounds FeedbackSounds { get; init; }
    public required string? FeedbackSoundDevice { get; init; }
    public required CleanupSettings Cleanup { get; init; }
    public required IReadOnlyList<string> ManualGames { get; init; }
    public required bool StartWithWindows { get; init; }
    public required OverlayDotMode OverlayDot { get; init; }
    public required AppTheme AppTheme { get; init; }

    /// <summary>Whether a newly detected game is captured (story 13.1); defaults to true.</summary>
    public bool CaptureNewGames { get; init; } = true;
}
