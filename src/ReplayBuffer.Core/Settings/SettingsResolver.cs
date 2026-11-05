using System.Text.Json;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Core.Settings;

/// <summary>The one settings <c>Resolve</c> (AD-4).</summary>
public static class SettingsResolver
{
    /// <summary>
    /// Resolves every key for a Game Profile, or for Global when <paramref name="profileId"/> is null
    /// or names no profile. Each key is the profile's valid override (overridable keys only), else the
    /// valid Global value, else the catalog default. Pure: reads only <paramref name="document"/>.
    /// </summary>
    public static EffectiveSettings Resolve(SettingsDocument document, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(document);

        GameProfile? profile = null;
        if (profileId is not null)
        {
            document.Profiles.TryGetValue(profileId, out profile);
        }

        T Get<T>(SettingKey<T> key)
        {
            if (key.Overridable && profile is not null && TryRead(profile.Overrides, key, out var overridden))
            {
                return overridden;
            }

            return TryRead(document.Global, key, out var global) ? global : key.Default;
        }

        return new EffectiveSettings
        {
            ProfileId = profileId,
            SettingsRevision = document.Revision,
            Video = Get(Keys.Video),
            TrackLayout = Get(Keys.TrackLayout),
            BufferWindow = Get(Keys.BufferWindow),
            ReplayStorage = Get(Keys.ReplayStorage),
            RingPath = Get(Keys.RingPath),
            SaveCooldown = Get(Keys.SaveCooldown),
            OutputFolder = Get(Keys.OutputFolder),
            Hotkeys = Get(Keys.Hotkeys),
            RecordHotkeyMode = Get(Keys.RecordHotkeyMode),
            MonitorRecordingMonitor = Get(Keys.MonitorRecordingMonitor),
            MonitorRecordingScreens = Get(Keys.MonitorRecordingScreens),
            FeedbackSounds = Get(Keys.FeedbackSounds),
            FeedbackSoundDevice = Get(Keys.FeedbackSoundDevice),
            Cleanup = Get(Keys.Cleanup),
            ManualGames = Get(Keys.ManualGames),
            StartWithWindows = Get(Keys.StartWithWindows),
            OverlayDot = Get(Keys.OverlayDot),
            AppTheme = Get(Keys.AppTheme),
            CaptureNewGames = Get(Keys.CaptureNewGames),
        };
    }

    private static bool TryRead<T>(IReadOnlyDictionary<string, JsonElement> values, SettingKey<T> key, out T value)
    {
        if (values.TryGetValue(key.Name, out var raw) && key.TryRead(raw, out var read))
        {
            value = read;
            return true;
        }

        value = default!;
        return false;
    }
}
