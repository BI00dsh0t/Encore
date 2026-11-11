using System.Text.Json;
using Microsoft.Extensions.Logging;
using Encore.Contracts.Settings;
using Encore.Core.Settings;
using Encore.Platform.Windows;

namespace Encore.Engine.Settings;

/// <summary>Creates a Game Profile for a game that has none yet, named from its executable.</summary>
internal static class GameProfiles
{
    private static readonly SettingsJsonContext Json = SettingsJsonContext.Default;

    /// <summary>Creates a Game Profile when the document lacks one, named from the executable. Rules: docs/code-notes.md.</summary>
    public static void EnsureProfile(
        SettingsStore store,
        string profileId,
        string fileName,
        string? source,
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
            if (!SettingsRetry.Update(store, current => CreationUpdate(current, profileId, value, source, out ignored)))
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
    private static SettingsUpdate? CreationUpdate(
        SettingsDocument current, string profileId, JsonElement name, string? source, out bool ignored)
    {
        ignored = false;
        if (current.Profiles.ContainsKey(profileId))
        {
            return null;
        }

        ignored = !CapturedWhenNew(current, profileId);
        List<SettingsOp> ops = [SettingsOp.SetProfile(profileId, ProfileAttributes.Name, name)];
        if (source is not null && GameSources.All.Contains(source))
        {
            ops.Add(SettingsOp.SetProfile(profileId, ProfileAttributes.Source, JsonSerializer.SerializeToElement(source, Json.String)));
        }

        if (ignored)
        {
            ops.Add(SettingsOp.SetProfile(profileId, ProfileAttributes.Ignored, JsonSerializer.SerializeToElement(true, Json.Boolean)));
        }

        return new SettingsUpdate(current.Revision, ops);
    }

    /// <summary>Whether the program was added by hand (<c>manualGames</c>), at this path or as another version of its Store app.</summary>
    public static bool IsManual(SettingsDocument document, string profileId) =>
        SettingsResolver.Resolve(document, null).ManualGames
            .Select(GamePaths.CanonicalOrLowercase)
            .Any(path => IsProgram(path, profileId));

    // A new game is captured when captureNewGames is on, or when it was added by hand (manualGames).
    private static bool CapturedWhenNew(SettingsDocument document, string profileId) =>
        SettingsResolver.Resolve(document, null).CaptureNewGames || IsManual(document, profileId);

    // The same program: the same path, or another version of the same Store app.
    private static bool IsProgram(string path, string profileId) =>
        string.Equals(path, profileId, StringComparison.Ordinal) || StorePackages.SameProgram(path, profileId);
}
