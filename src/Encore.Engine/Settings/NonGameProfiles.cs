using System.Text.Json;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Engine.Ipc;
using Encore.Platform.Windows;

namespace Encore.Engine.Settings;

/// <summary>The one-time check at start: captured Game Profiles of programs that are never games become Not captured.</summary>
internal static class NonGameProfiles
{
    /// <summary>
    /// Runs once per install, remembered in <c>runtime.json</c>: profiles under the Windows folder or on the never-games list
    /// stop being captured, announced once. Other profiles, and any it couldn't change, are left alone.
    /// </summary>
    public static void CheckOnce(SettingsStore store, RuntimeStateStore runtime, Warnings warnings, ILogger log)
    {
        if (runtime.NonGamesChecked())
        {
            return;
        }

        List<string> names = [];
        try
        {
            SettingsRetry.Update(store, current => IgnoreNonGames(current, names));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "The Game Profiles couldn't be checked for programs that aren't games; trying again at the next start");
            return;
        }

        runtime.MarkNonGamesChecked();
        if (names.Count == 0)
        {
            return;
        }

        log.LogInformation("Stopped capturing {Count} Game Profile(s) that aren't games: {Names}", names.Count, string.Join(", ", names));
        warnings.Raise(ErrorCodes.NewGameNotCaptured, WarningSeverity.Info, "games", Notice(names), overlay: false);
    }

    /// <summary>The notice: "Encore stopped capturing A and B; they aren't games. Turn them back on in Games."</summary>
    internal static string Notice(IReadOnlyList<string> names)
    {
        var listed = names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        var pronoun = names.Count == 1 ? "it isn't a game. Turn it" : "they aren't games. Turn them";
        return $"Encore stopped capturing {listed}; {pronoun} back on in Games.";
    }

    // The update that sets ignored on each captured profile of a never-game; null when there is none.
    private static SettingsUpdate? IgnoreNonGames(SettingsDocument current, List<string> names)
    {
        names.Clear();
        var ignored = JsonSerializer.SerializeToElement(true, SettingsJsonContext.Default.Boolean);
        List<SettingsOp> ops = [];
        foreach (var (id, profile) in current.Profiles)
        {
            if (!profile.Ignored && NeverGames.Contains(id))
            {
                ops.Add(SettingsOp.SetProfile(id, ProfileAttributes.Ignored, ignored));
                names.Add(profile.Name);
            }
        }

        return ops.Count == 0 ? null : new SettingsUpdate(current.Revision, ops);
    }
}
