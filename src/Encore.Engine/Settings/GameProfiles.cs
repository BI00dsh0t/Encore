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
