using Encore.Contracts.Settings;
using Encore.Platform.Windows;

namespace Encore.Engine.Settings;

/// <summary>Finds the Game Profile a program belongs to, so a Store app keeps its profile when an update moves it.</summary>
internal static class ProfileMatch
{
    /// <summary>
    /// The id of the Game Profile for a program's canonical path: the path itself when a profile has it, else the
    /// profile of an older version of the same Store app, else the path, for a new profile.
    /// </summary>
    public static string Resolve(SettingsDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (document.Profiles.ContainsKey(path) || StorePackages.Key(path) is not { } key)
        {
            return path;
        }

        foreach (var id in document.Profiles.Keys)
        {
            if (string.Equals(StorePackages.Key(id), key, StringComparison.Ordinal))
            {
                return id;
            }
        }

        return path;
    }
}
