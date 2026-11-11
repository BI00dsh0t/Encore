using System.Collections.Immutable;
using Encore.Contracts.Settings;

namespace Encore.Core.Output;

/// <summary>Game Profile folders: each profile's folder under the Output Folder, assigned once and never moved.</summary>
public static class ProfileFolders
{
    /// <summary>The folder Monitor Recordings go in; no Game Profile gets it.</summary>
    public const string MonitorRecordings = "Monitor Recordings";

    /// <summary>Gives each profile without a folder its sanitized name, made unique with <c> (2)</c>, <c> (3)</c>…</summary>
    public static void AssignMissing(ImmutableSortedDictionary<string, GameProfile>.Builder profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MonitorRecordings };
        foreach (var profile in profiles.Values)
        {
            if (profile.Folder is not null)
            {
                taken.Add(profile.Folder);
            }
        }

        foreach (var (id, profile) in profiles.Where(pair => pair.Value.Folder is null).ToList())
        {
            var name = PathNames.Sanitize(profile.Name, PathNames.GameFallback);
            var folder = name;
            for (var number = 2; taken.Contains(folder); number++)
            {
                folder = $"{name} ({number})";
            }

            taken.Add(folder);
            profiles[id] = profile with { Folder = folder };
        }
    }

    /// <summary>The profile's folder: its own, or the one the next commit will assign it; null for an unknown profile.</summary>
    public static string? FolderOf(SettingsDocument document, string profileId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(profileId);

        if (!document.Profiles.TryGetValue(profileId, out var profile))
        {
            return null;
        }

        if (profile.Folder is not null)
        {
            return profile.Folder;
        }

        var profiles = document.Profiles.ToBuilder();
        AssignMissing(profiles);
        return profiles[profileId].Folder;
    }
}
