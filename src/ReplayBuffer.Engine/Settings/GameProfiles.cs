using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Settings;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine.Settings;

/// <summary>Game Profile auto-creation (story 3.4).</summary>
internal static class GameProfiles
{
    /// <summary>
    /// Creates the Game Profile <paramref name="profileId"/> when the document lacks it: one
    /// <c>name</c> set through <see cref="SettingsStore.Update"/> (AD-4), named from the executable's
    /// version resource, else <paramref name="fileName"/> without its extension. With
    /// <c>captureNewGames</c> off, the same update sets <c>ignored</c>, unless the game is in
    /// <c>manualGames</c> (story 13.1), and <paramref name="notCaptured"/> gets the game's name once the
    /// write commits. Everything else is the store's default (Recordings off, no overrides, a folder
    /// assigned). An existing profile is never touched. A conflict with another write is retried while
    /// the profile is still missing; any other failure is logged.
    /// </summary>
    public static void EnsureProfile(SettingsStore store, string profileId, string fileName, ILogger log, Action<string>? notCaptured = null)
    {
        if (store.Current.Profiles.ContainsKey(profileId))
        {
            return;
        }

        var name = GamePaths.ProductName(profileId, fileName);
        var value = JsonSerializer.SerializeToElement(name, SettingsJsonContext.Default.String);
        var ignored = false;
        try
        {
            var created = SettingsRetry.Update(store, current =>
            {
                if (current.Profiles.ContainsKey(profileId))
                {
                    return null;
                }

                ignored = !CapturedWhenNew(current, profileId);
                SettingsOp[] ops = ignored
                    ? [SettingsOp.SetProfile(profileId, ProfileAttributes.Name, value), SettingsOp.SetProfile(profileId, ProfileAttributes.Ignored, JsonSerializer.SerializeToElement(true, SettingsJsonContext.Default.Boolean))]
                    : [SettingsOp.SetProfile(profileId, ProfileAttributes.Name, value)];
                return new SettingsUpdate(current.Revision, ops);
            });
            if (created)
            {
                log.LogInformation("Created the Game Profile {Name} at {ProfileId}{Ignored}", name, profileId, ignored ? ", not captured (captureNewGames is off)" : "");
                if (ignored)
                {
                    notCaptured?.Invoke(name);
                }
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "The Game Profile of {ProfileId} couldn't be created", profileId);
        }
    }

    // A new game is captured when captureNewGames is on, or when it was added by hand (manualGames).
    private static bool CapturedWhenNew(SettingsDocument document, string profileId)
    {
        var global = SettingsResolver.Resolve(document, null);
        return global.CaptureNewGames
            || global.ManualGames.Any(path => string.Equals(GamePaths.CanonicalOrLowercase(path), profileId, StringComparison.Ordinal));
    }
}
