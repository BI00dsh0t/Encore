using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Typed reads and ops over a settings document's values; a profile's value is its override, else the Global one.</summary>
internal static class SettingsValues
{
    /// <summary>The Global value of a key: the document's, or the key's default when it is missing or doesn't fit.</summary>
    public static T Global<T>(SettingsDocument document, SettingKey<T> key) =>
        document.Global.TryGetValue(key.Name, out var raw) && key.TryRead(raw, out var value) ? value : key.Default;

    /// <summary>Whether profile <paramref name="profileId"/> has a valid override of <paramref name="key"/>.</summary>
    public static bool HasOverride<T>(SettingsDocument document, SettingKey<T> key, string profileId) =>
        TryOverride(document, key, profileId, out _);

    /// <summary>The value of a key for a profile, or the Global value when the profile is null or has no valid override.</summary>
    public static T Resolve<T>(SettingsDocument document, SettingKey<T> key, string? profileId) =>
        profileId is not null && TryOverride(document, key, profileId, out var value) ? value : Global(document, key);

    public static SettingsOp SetGlobal<T>(SettingKey<T> key, T value) => SettingsOp.SetGlobal(key.Name, key.ToJson(value));

    /// <summary>Sets <paramref name="key"/> Globally when <paramref name="profileId"/> is null, else as that profile's override.</summary>
    public static SettingsOp Set<T>(SettingKey<T> key, T value, string? profileId) => profileId is null
        ? SetGlobal(key, value)
        : SettingsOp.SetProfile(profileId, key.Name, key.ToJson(value));

    /// <summary>The op for "use the main setting": ticked removes the game's override, unticked copies the Global value.</summary>
    public static SettingsOp UseGlobal<T>(SettingKey<T> key, string profileId, bool useGlobal, T global) => useGlobal
        ? SettingsOp.RemoveProfile(profileId, key.Name)
        : Set(key, global, profileId);

    private static bool TryOverride<T>(SettingsDocument document, SettingKey<T> key, string profileId, out T value)
    {
        if (document.Profiles.TryGetValue(profileId, out var profile)
            && profile.Overrides.TryGetValue(key.Name, out var raw)
            && key.TryRead(raw, out var read))
        {
            value = read;
            return true;
        }

        value = key.Default;
        return false;
    }
}
