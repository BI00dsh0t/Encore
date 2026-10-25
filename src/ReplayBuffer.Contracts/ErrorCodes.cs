namespace ReplayBuffer.Contracts;

/// <summary>
/// The one registry of error and warning codes. Every code is kebab-case and appears here.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The command line, or a request's shape, is malformed.</summary>
    public const string InvalidArguments = "invalid-arguments";

    /// <summary>No top-level window matches the requested title.</summary>
    public const string WindowNotFound = "window-not-found";

    /// <summary>More than one top-level window matches the requested title.</summary>
    public const string WindowAmbiguous = "window-ambiguous";

    /// <summary>The FFmpeg DLLs are not beside the executable.</summary>
    public const string FFmpegMissing = "ffmpeg-missing";

    /// <summary>No audio endpoint matches the requested name.</summary>
    public const string AudioDeviceNotFound = "audio-device-not-found";

    /// <summary>More than one audio endpoint matches the requested name.</summary>
    public const string AudioDeviceAmbiguous = "audio-device-ambiguous";

    /// <summary>An audio endpoint could not be opened or failed while capturing.</summary>
    public const string AudioDeviceFailed = "audio-device-failed";

    /// <summary>Window capture could not start or delivered no frames.</summary>
    public const string CaptureFailed = "capture-failed";

    /// <summary>An encoder or the muxer failed.</summary>
    public const string EncodeFailed = "encode-failed";

    /// <summary>A settings update's <c>expectedRevision</c> is not the current revision.</summary>
    public const string SettingsConflict = "settings-conflict";

    /// <summary>A Game Profile override names a Global-only key.</summary>
    public const string KeyNotOverridable = "key-not-overridable";

    /// <summary>A settings update names a key that is not in the catalog.</summary>
    public const string SettingUnknownKey = "setting-unknown-key";

    /// <summary>A settings value does not fit its key's type or range.</summary>
    public const string SettingInvalidValue = "setting-invalid-value";

    /// <summary>A settings update names a Game Profile that does not exist.</summary>
    public const string ProfileNotFound = "profile-not-found";

    /// <summary>The settings file could not be written; nothing changed.</summary>
    public const string SettingsWriteFailed = "settings-write-failed";

    /// <summary>The settings file could not be read and was moved aside.</summary>
    public const string SettingsUnreadable = "settings-unreadable";

    /// <summary>Every code in the registry.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        InvalidArguments,
        WindowNotFound,
        WindowAmbiguous,
        FFmpegMissing,
        AudioDeviceNotFound,
        AudioDeviceAmbiguous,
        AudioDeviceFailed,
        CaptureFailed,
        EncodeFailed,
        SettingsConflict,
        KeyNotOverridable,
        SettingUnknownKey,
        SettingInvalidValue,
        ProfileNotFound,
        SettingsWriteFailed,
        SettingsUnreadable,
    ];
}
