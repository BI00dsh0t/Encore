namespace Encore.Platform.Windows;

/// <summary>How registering one Hotkey went.</summary>
public enum HotkeyRegistrationOutcome
{
    Registered,

    /// <summary>Another process or thread holds the combination: <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>.</summary>
    Taken,

    Failed,
}
