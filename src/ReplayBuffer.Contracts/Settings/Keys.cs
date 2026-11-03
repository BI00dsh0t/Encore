using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ReplayBuffer.Contracts.Settings;

/// <summary>One key of the closed catalog (AD-4): its name, value type, default, overridability and when it applies.</summary>
public abstract class SettingKey
{
    private protected SettingKey(string name, Type valueType, bool overridable, AppliesAt appliesAt)
    {
        Name = name;
        ValueType = valueType;
        Overridable = overridable;
        AppliesAt = appliesAt;
    }

    public string Name { get; }

    public Type ValueType { get; }

    /// <summary>Whether a Game Profile may override it.</summary>
    public bool Overridable { get; }

    public AppliesAt AppliesAt { get; }

    /// <summary>The default, as JSON.</summary>
    public abstract JsonElement DefaultJson { get; }

    /// <summary>
    /// Checks a raw value against the key's type and range. On success, <paramref name="normalized"/>
    /// is the value re-serialized in canonical form.
    /// </summary>
    public abstract bool TryNormalize(JsonElement value, out JsonElement normalized);

    public bool IsValid(JsonElement value) => TryNormalize(value, out _);

    public override string ToString() => Name;
}

/// <summary>A catalog key whose value is a <typeparamref name="T"/>, read through its generated <see cref="JsonTypeInfo{T}"/>.</summary>
public sealed class SettingKey<T> : SettingKey
{
    private readonly Func<T, bool> _isValid;
    private readonly bool _allowsNull;

    internal SettingKey(
        string name,
        JsonTypeInfo<T> typeInfo,
        T defaultValue,
        bool overridable,
        AppliesAt appliesAt,
        Func<T, bool>? isValid = null,
        bool allowsNull = false)
        : base(name, typeof(T), overridable, appliesAt)
    {
        TypeInfo = typeInfo;
        Default = defaultValue;
        _isValid = isValid ?? (_ => true);
        _allowsNull = allowsNull;
        DefaultJson = ToJson(defaultValue);
    }

    public JsonTypeInfo<T> TypeInfo { get; }

    public T Default { get; }

    public override JsonElement DefaultJson { get; }

    /// <summary>Reads a raw value; false if it doesn't fit the key's type or range.</summary>
    public bool TryRead(JsonElement value, [MaybeNullWhen(false)] out T result)
    {
        result = default;
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        T? read;
        try
        {
            read = value.Deserialize(TypeInfo);
        }
        catch (JsonException)
        {
            return false;
        }

        if (read is null ? !_allowsNull : !_isValid(read))
        {
            return false;
        }

        result = read!;
        return true;
    }

    public JsonElement ToJson(T value) => JsonSerializer.SerializeToElement(value, TypeInfo);

    public override bool TryNormalize(JsonElement value, out JsonElement normalized)
    {
        if (TryRead(value, out var read))
        {
            normalized = ToJson(read);
            return true;
        }

        normalized = default;
        return false;
    }
}

/// <summary>The Game Profile attributes a profile-scope op may set. They are not catalog keys.</summary>
public static class ProfileAttributes
{
    public const string Name = "name";
    public const string Ignored = "ignored";
    public const string RecordingsEnabled = "recordingsEnabled";

    public static IReadOnlyList<string> All { get; } = [Name, Ignored, RecordingsEnabled];
}

/// <summary>The closed settings key catalog (AD-4, AD-22). A key missing from the document takes its default.</summary>
public static class Keys
{
    /// <summary>The id of the default layout's one "Everything" Track.</summary>
    public static readonly Guid EverythingTrackId = new("8c010267-eff4-4ac3-9b71-6c0c54f08c75");

    private const int VkF8 = 0x77;
    private const int VkF9 = 0x78;
    private const int VkF10 = 0x79;

    private static readonly SettingsJsonContext Json = SettingsJsonContext.Default;

    public static SettingKey<VideoSettings> Video { get; } = new(
        "video", Json.VideoSettings,
        new VideoSettings(VideoCodec.H264, 1920, 1080, 60, 40_000, RateControl.Cbr),
        overridable: true, AppliesAt.NextSession, IsValidVideo);

    public static SettingKey<TrackLayout> TrackLayout { get; } = new(
        "trackLayout", Json.TrackLayout,
        new TrackLayout([], [new Track(EverythingTrackId, "Everything", [], MuteAffected: true)]),
        overridable: true, AppliesAt.NextSession, IsValidTrackLayout);

    /// <summary>Seconds of the Replay Buffer.</summary>
    public static SettingKey<int> BufferWindow { get; } = new(
        "bufferWindow", Json.Int32, 300, overridable: true, AppliesAt.NextSession, v => v >= 1);

    public static SettingKey<ReplayStorage> ReplayStorage { get; } = new(
        "replayStorage", Json.ReplayStorage, Settings.ReplayStorage.Memory, overridable: false, AppliesAt.NextSession, Enum.IsDefined);

    /// <summary>The Disk-mode ring folder; null is <c>%LOCALAPPDATA%\&lt;instance&gt;\ring</c>.</summary>
    public static SettingKey<string?> RingPath { get; } = new(
        "ringPath", Json.String, null, overridable: false, AppliesAt.NextSession, IsFullPath, allowsNull: true);

    /// <summary>Seconds after a save before the next save is accepted.</summary>
    public static SettingKey<int> SaveCooldown { get; } = new(
        "saveCooldown", Json.Int32, 5, overridable: false, AppliesAt.Immediate, v => v >= 0);

    /// <summary>The Output Folder; null is an instance-named folder under Videos.</summary>
    public static SettingKey<string?> OutputFolder { get; } = new(
        "outputFolder", Json.String, null, overridable: false, AppliesAt.Immediate, IsFullPath, allowsNull: true);

    public static SettingKey<HotkeySettings> Hotkeys { get; } = new(
        "hotkeys", Json.HotkeySettings,
        new HotkeySettings(
            Save: new HotkeyBinding(VkF10, HotkeyModifiers.Alt),
            MicMute: new HotkeyBinding(VkF8, HotkeyModifiers.Alt),
            RecordToggle: new HotkeyBinding(VkF9, HotkeyModifiers.Alt),
            RecordStart: null,
            RecordStop: null,
            MonitorRecordToggle: new HotkeyBinding(VkF9, HotkeyModifiers.Alt | HotkeyModifiers.Shift)),
        overridable: false, AppliesAt.Immediate, IsValidHotkeys);

    public static SettingKey<RecordHotkeyMode> RecordHotkeyMode { get; } = new(
        "recordHotkeyMode", Json.RecordHotkeyMode, Settings.RecordHotkeyMode.Toggle, overridable: false, AppliesAt.Immediate, Enum.IsDefined);

    /// <summary>The monitor device name for Monitor Recording; null is the primary monitor.</summary>
    public static SettingKey<string?> MonitorRecordingMonitor { get; } = new(
        "monitorRecordingMonitor", Json.String, null, overridable: false, AppliesAt.NextSession, IsNotBlank, allowsNull: true);

    public static SettingKey<FeedbackSounds> FeedbackSounds { get; } = new(
        "feedbackSounds", Json.FeedbackSounds,
        new FeedbackSounds(ClipSaved: true, SaveFailed: true, RecordingStarted: true, RecordingStopped: true, DiskFull: true),
        overridable: false, AppliesAt.Immediate);

    /// <summary>The endpoint id Feedback Sounds play on; null is the default communications endpoint.</summary>
    public static SettingKey<string?> FeedbackSoundDevice { get; } = new(
        "feedbackSoundDevice", Json.String, null, overridable: false, AppliesAt.Immediate, IsNotBlank, allowsNull: true);

    public static SettingKey<CleanupSettings> Cleanup { get; } = new(
        "cleanup", Json.CleanupSettings, new CleanupSettings(null, null), overridable: false, AppliesAt.Immediate,
        v => v.MaxBytes is null or >= 1 && v.MaxAgeSeconds is null or >= 1);

    /// <summary>Canonical executable paths detected as games in addition to the automatic ones.</summary>
    public static SettingKey<IReadOnlyList<string>> ManualGames { get; } = new(
        "manualGames", Json.IReadOnlyListString, [], overridable: false, AppliesAt.Immediate, IsValidManualGames);

    public static SettingKey<bool> StartWithWindows { get; } = new(
        "startWithWindows", Json.Boolean, false, overridable: false, AppliesAt.Immediate);

    /// <summary>Every key, in catalog order.</summary>
    public static IReadOnlyList<SettingKey> All { get; } =
    [
        Video,
        TrackLayout,
        BufferWindow,
        ReplayStorage,
        RingPath,
        SaveCooldown,
        OutputFolder,
        Hotkeys,
        RecordHotkeyMode,
        MonitorRecordingMonitor,
        FeedbackSounds,
        FeedbackSoundDevice,
        Cleanup,
        ManualGames,
        StartWithWindows,
    ];

    private static readonly Dictionary<string, SettingKey> ByName = All.ToDictionary(k => k.Name, StringComparer.Ordinal);

    /// <summary>Looks a key up by its exact name.</summary>
    public static bool TryGet(string name, [NotNullWhen(true)] out SettingKey? key) => ByName.TryGetValue(name, out key);

    private static bool IsValidVideo(VideoSettings v) =>
        Enum.IsDefined(v.Codec)
        && Enum.IsDefined(v.RateControl)
        && IsDimension(v.Width)
        && IsDimension(v.Height)
        && v.Fps is >= 1 and <= VideoSettings.MaxFps
        && v.BitrateKbps >= 1;

    // Encoders take 4:2:0 frames, so both sides are even.
    private static bool IsDimension(int value) => value is >= 2 and <= VideoSettings.MaxDimension && value % 2 == 0;

    private static bool IsValidTrackLayout(TrackLayout layout)
    {
        var deviceKeys = new HashSet<Guid>();
        foreach (var device in layout.Devices)
        {
            if (device is null || device.Key == Guid.Empty || !deviceKeys.Add(device.Key)
                || string.IsNullOrWhiteSpace(device.EndpointId) || !Enum.IsDefined(device.Flow))
            {
                return false;
            }
        }

        var trackIds = new HashSet<Guid>();
        foreach (var track in layout.Tracks)
        {
            if (track is null || track.Id == Guid.Empty || !trackIds.Add(track.Id)
                || string.IsNullOrWhiteSpace(track.Name)
                || track.DeviceKeys.Distinct().Count() != track.DeviceKeys.Count
                || !track.DeviceKeys.All(deviceKeys.Contains))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHotkeys(HotkeySettings hotkeys) =>
        new[] { hotkeys.Save, hotkeys.MicMute, hotkeys.RecordToggle, hotkeys.RecordStart, hotkeys.RecordStop, hotkeys.MonitorRecordToggle }
            .All(b => b is null || (b.Vk is >= 1 and <= 254 && (b.Modifiers & ~HotkeyModifiers.All) == 0));

    private static bool IsValidManualGames(IReadOnlyList<string> paths) =>
        paths.All(p => p is not null && IsFullPath(p))
        && paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Count;

    private static bool IsFullPath(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool IsNotBlank(string? value) => !string.IsNullOrWhiteSpace(value);
}
