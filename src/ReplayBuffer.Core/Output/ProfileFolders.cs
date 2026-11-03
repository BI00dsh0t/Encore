using System.Collections.Immutable;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Core.Output;

/// <summary>
/// Game Profile folders (AD-21): each profile's folder under the Output Folder, assigned once and
/// never moved by a rename or by other profiles.
/// </summary>
public static class ProfileFolders
{
    /// <summary>The folder Monitor Recordings go in; no Game Profile gets it.</summary>
    public const string MonitorRecordings = "Monitor Recordings";

    private const string Fallback = "Game";

    /// <summary>
    /// Gives each profile without a folder, in id order, its sanitized name, or that name with
    /// <c> (2)</c>, <c> (3)</c>… so it is unique, case-insensitively, among every profile's folder
    /// and <see cref="MonitorRecordings"/>. Folders already set are left alone.
    /// </summary>
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

        foreach (var (id, profile) in profiles.Where(p => p.Value.Folder is null).ToList())
        {
            var name = PathNames.Sanitize(profile.Name, Fallback);
            var folder = name;
            for (var n = 2; taken.Contains(folder); n++)
            {
                folder = $"{name} ({n})";
            }

            taken.Add(folder);
            profiles[id] = profile with { Folder = folder };
        }
    }

    /// <summary>
    /// The folder of profile <paramref name="profileId"/>: its own, or the one the next commit of
    /// <paramref name="document"/> will assign it. Null when the document has no such profile.
    /// </summary>
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
