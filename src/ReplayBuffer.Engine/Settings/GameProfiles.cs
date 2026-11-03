using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine.Settings;

/// <summary>Game Profile auto-creation (story 3.4).</summary>
internal static class GameProfiles
{
    private const int Tries = 3;

    /// <summary>
    /// Creates the Game Profile <paramref name="profileId"/> when the document lacks it: one
    /// <c>name</c> set through <see cref="SettingsStore.Update"/> (AD-4), named from the executable's
    /// version resource, else <paramref name="fileName"/> without its extension. Everything else is the
    /// store's default (not Ignored, Recordings off, no overrides, a folder assigned). A conflict with
    /// another write is retried while the profile is still missing; any other failure is logged.
    /// </summary>
    public static void EnsureProfile(SettingsStore store, string profileId, string fileName, ILogger log)
    {
        string? name = null;
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            var current = store.Current;
            if (current.Profiles.ContainsKey(profileId))
            {
                return;
            }

            name ??= GamePaths.ProductName(profileId, fileName);
            var value = JsonSerializer.SerializeToElement(name, SettingsJsonContext.Default.String);
            try
            {
                store.Update(new SettingsUpdate(current.Revision, [SettingsOp.SetProfile(profileId, ProfileAttributes.Name, value)]));
                log.LogInformation("Created the Game Profile {Name} at {ProfileId}", name, profileId);
                return;
            }
            catch (ErrorCodeException e) when (e.Code == ErrorCodes.SettingsConflict && attempt < Tries)
            {
                // Another write landed first: read it and try again.
            }
            catch (Exception e)
            {
                log.LogWarning(e, "The Game Profile of {ProfileId} couldn't be created", profileId);
                return;
            }
        }
    }
}
