using System.Text.Json;
using System.Text.Json.Serialization;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.Contracts.Settings;

/// <summary>Writes an enum as its camelCase name and refuses integers.</summary>
public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>The video codec of the <c>video</c> key.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<VideoCodec>))]
public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
}

/// <summary>The encoder rate control of the <c>video</c> key.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RateControl>))]
public enum RateControl
{
    Cbr,
    Vbr,
}

/// <summary>The <c>video</c> key: codec, output resolution, fps, bitrate and rate control.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoSettings(VideoCodec Codec, int Width, int Height, int Fps, int BitrateKbps, RateControl RateControl)
{
    public const int MaxFps = 120;
    public const int MaxDimension = 8192;
}

/// <summary>Whether a layout device is an Output Device (loopback) or an Input Device (capture).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AudioFlow>))]
public enum AudioFlow
{
    Output,
    Input,
}

/// <summary>A device in a Track Layout. <see cref="Key"/> is a layout-local GUID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LayoutDevice(Guid Key, string EndpointId, string FriendlyName, string ParentName, AudioFlow Flow, bool IsMic);

/// <summary>A Track in a Track Layout. <see cref="Id"/> is stable across renames and copies.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Track(Guid Id, string Name, IReadOnlyList<Guid> DeviceKeys, bool MuteAffected);

/// <summary>The <c>trackLayout</c> key (AD-4): one inline value, devices and the Tracks that mix them.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TrackLayout(IReadOnlyList<LayoutDevice> Devices, IReadOnlyList<Track> Tracks);

/// <summary>Where the Replay Buffer ring lives.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ReplayStorage>))]
public enum ReplayStorage
{
    Memory,
    Disk,
}

/// <summary><c>MOD_*</c> flags of <c>RegisterHotKey</c>.</summary>
public static class HotkeyModifiers
{
    public const int Alt = 0x0001;
    public const int Control = 0x0002;
    public const int Shift = 0x0004;
    public const int Win = 0x0008;
    public const int NoRepeat = 0x4000;

    /// <summary>Every flag a binding may carry.</summary>
    public const int All = Alt | Control | Shift | Win | NoRepeat;
}

/// <summary>One Hotkey: a Win32 virtual-key code and <see cref="HotkeyModifiers"/> flags.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HotkeyBinding(int Vk, int Modifiers);

/// <summary>
/// The <c>hotkeys</c> key (AD-11): one binding, or null for unbound, per <c>HotkeyAction</c>. The
/// Monitor Record start and stop bindings came later (6.9): a document without them reads them as unbound.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HotkeySettings(
    HotkeyBinding? Save,
    HotkeyBinding? MicMute,
    HotkeyBinding? RecordToggle,
    HotkeyBinding? RecordStart,
    HotkeyBinding? RecordStop,
    HotkeyBinding? MonitorRecordToggle,
    HotkeyBinding? MonitorRecordStart = null,
    HotkeyBinding? MonitorRecordStop = null)
{
    /// <summary>The binding of <paramref name="action"/>, or null when it is unbound.</summary>
    public HotkeyBinding? For(HotkeyAction action) => action switch
    {
        HotkeyAction.Save => Save,
        HotkeyAction.MicMute => MicMute,
        HotkeyAction.RecordToggle => RecordToggle,
        HotkeyAction.RecordStart => RecordStart,
        HotkeyAction.RecordStop => RecordStop,
        HotkeyAction.MonitorRecordToggle => MonitorRecordToggle,
        HotkeyAction.MonitorRecordStart => MonitorRecordStart,
        HotkeyAction.MonitorRecordStop => MonitorRecordStop,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}

/// <summary>An action a Hotkey triggers, one per <see cref="HotkeySettings"/> field, in registration order (AD-11).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<HotkeyAction>))]
public enum HotkeyAction
{
    Save,
    MicMute,
    RecordToggle,
    RecordStart,
    RecordStop,
    MonitorRecordToggle,
    MonitorRecordStart,
    MonitorRecordStop,
}

/// <summary>Whether Record is one toggle Hotkey or separate start and stop Hotkeys.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RecordHotkeyMode>))]
public enum RecordHotkeyMode
{
    Toggle,
    Separate,
}

/// <summary>The <c>feedbackSounds</c> key: which Feedback Sounds play.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FeedbackSounds(bool ClipSaved, bool SaveFailed, bool RecordingStarted, bool RecordingStopped, bool DiskFull)
{
    /// <summary>Whether <paramref name="sound"/> is switched on; <c>false</c> for a value the enum doesn't name.</summary>
    public bool IsOn(FeedbackSound sound) => sound switch
    {
        FeedbackSound.ClipSaved => ClipSaved,
        FeedbackSound.SaveFailed => SaveFailed,
        FeedbackSound.RecordingStarted => RecordingStarted,
        FeedbackSound.RecordingStopped => RecordingStopped,
        FeedbackSound.DiskFull => DiskFull,
        _ => false,
    };

    /// <summary>A copy with <paramref name="sound"/> switched to <paramref name="on"/>; unchanged for a value the enum doesn't name.</summary>
    public FeedbackSounds With(FeedbackSound sound, bool on) => sound switch
    {
        FeedbackSound.ClipSaved => this with { ClipSaved = on },
        FeedbackSound.SaveFailed => this with { SaveFailed = on },
        FeedbackSound.RecordingStarted => this with { RecordingStarted = on },
        FeedbackSound.RecordingStopped => this with { RecordingStopped = on },
        FeedbackSound.DiskFull => this with { DiskFull = on },
        _ => this,
    };
}

/// <summary>The <c>monitorRecordingScreens</c> key's mode (AD-25).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<MonitorScreensMode>))]
public enum MonitorScreensMode
{
    /// <summary>Every monitor attached when a Monitor Recording starts.</summary>
    All,

    /// <summary>The monitors in <see cref="MonitorRecordingScreens.Monitors"/> that are attached.</summary>
    Selected,
}

/// <summary>
/// The <c>monitorRecordingScreens</c> key (AD-25, story 11.2): which screens a Monitor Recording
/// composites. <c>{ "mode": "all" }</c>, or <c>{ "mode": "selected", "monitors": [ ... ] }</c> with at
/// least one monitor id: DisplayConfig's monitor device path (<c>DisplayMonitor.MonitorId</c>), stable
/// across reboots, or a GDI device name carried over from <c>monitorRecordingMonitor</c>. While the key
/// is null, the old <c>monitorRecordingMonitor</c> is read as before.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonitorRecordingScreens(MonitorScreensMode Mode, IReadOnlyList<string>? Monitors = null)
{
    /// <summary>Every attached monitor.</summary>
    public static MonitorRecordingScreens AllScreens { get; } = new(MonitorScreensMode.All);

    /// <summary>The monitors <paramref name="monitorIds"/> names.</summary>
    public static MonitorRecordingScreens Selected(params IReadOnlyList<string> monitorIds) => new(MonitorScreensMode.Selected, monitorIds);
}

/// <summary>The <c>cleanup</c> key: each limit null means that limit is off.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CleanupSettings(long? MaxBytes, long? MaxAgeSeconds);

/// <summary>
/// The <c>overlayDot</c> key: when the Overlay's dot shows. A topmost window over a borderless game keeps
/// the game in independent flip only while the game's monitor has a free hardware overlay plane (MPO), so
/// <see cref="Auto"/> shows the dot always where the monitor reports overlay planes and for a few seconds
/// after each change where it doesn't.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<OverlayDotMode>))]
public enum OverlayDotMode
{
    /// <summary>Always where the Target Game's monitor reports overlay planes; otherwise on each change.</summary>
    Auto,

    /// <summary>Always, whatever it costs (FR-6 as written).</summary>
    Always,

    /// <summary>For a few seconds after each change of the dot or the mic mute.</summary>
    OnChange,
}

/// <summary>The <c>appTheme</c> key: the App's look (AD-23).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AppTheme>))]
public enum AppTheme
{
    /// <summary>.NET WPF's Fluent theme, following the Windows mode and accent colour, with Mica on the Main App.</summary>
    Fluent,

    /// <summary>The fixed near-black palette with the teal accent, whatever the Windows mode.</summary>
    GamingDark,
}

/// <summary>When a changed key takes effect.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AppliesAt>))]
public enum AppliesAt
{
    /// <summary>As soon as the change commits.</summary>
    Immediate,

    /// <summary>At the next Reset or the next Recording.</summary>
    NextSession,
}
