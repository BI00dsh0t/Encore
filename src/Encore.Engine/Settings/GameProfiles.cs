using System.Text.Json;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Settings;
using Encore.Core.Settings;
using Encore.Platform.Windows;

namespace Encore.Engine.Settings;

/// <summary>Game Profile auto-creation.</summary>
internal static class GameProfiles
{
    /// <summary>Creates a Game Profile when the document lacks one, named from the executable. Rules: docs/code-notes.md.</summary>
    public static void EnsureProfile(
        SettingsStore store,
        string profileId,
        string fileName,
        ILogger log,
        Action<string>? notCaptured = null)
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
            if (!SettingsRetry.Update(store, current => CreationUpdate(current, profileId, value, out ignored)))
            {
                return;
            }

            log.LogInformation(
                "Created the Game Profile {Name} at {ProfileId}{Ignored}",
                name,
                profileId,
                ignored ? ", not captured (captureNewGames is off)" : "");
            if (ignored)
            {
                notCaptured?.Invoke(name);
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "The Game Profile of {ProfileId} couldn't be created", profileId);
        }
    }

    /// <summary>The ops that create the profile, ignored when new games aren't captured; null when another write made it.</summary>
    private static SettingsUpdate? CreationUpdate(SettingsDocument current, string profileId, JsonElement name, out bool ignored)
    {
        ignored = false;
        if (current.Profiles.ContainsKey(profileId))
        {
            return null;
        }

        ignored = !CapturedWhenNew(current, profileId);
        var named = SettingsOp.SetProfile(profileId, ProfileAttributes.Name, name);
        if (!ignored)
        {
            return new SettingsUpdate(current.Revision, [named]);
        }

        var ignoredValue = JsonSerializer.SerializeToElement(true, SettingsJsonContext.Default.Boolean);
        return new SettingsUpdate(current.Revision, [named, SettingsOp.SetProfile(profileId, ProfileAttributes.Ignored, ignoredValue)]);
    }

    // A new game is captured when captureNewGames is on, or when it was added by hand (manualGames).
    private static bool CapturedWhenNew(SettingsDocument document, string profileId)
    {
        var global = SettingsResolver.Resolve(document, null);
        return global.CaptureNewGames
            || global.ManualGames.Any(path => string.Equals(GamePaths.CanonicalOrLowercase(path), profileId, StringComparison.Ordinal));
    }
}
