using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Library;

/// <summary>The game a Library item belongs to, as the key the list groups and filters by.</summary>
internal static class LibraryGameKeys
{
    /// <summary>Core's <c>ProfileFolders.MonitorRecordings</c>; the App doesn't reference Core.</summary>
    private const string MonitorRecordings = "Monitor Recordings";

    private const string OtherKey = "other";

    private static readonly string MonitorRecordingsKey = FolderKey(MonitorRecordings);

    /// <summary>The item's game, as group key and header name: its profile's name, else its top-level folder's, else "Other".</summary>
    public static (string Key, string Name) Of(LibraryItem item, SettingsDocument settings)
    {
        if (item.ProfileId is { } id && settings.Profiles.TryGetValue(id, out var profile))
        {
            return (ProfileKey(id), profile.Name);
        }

        var separator = item.Path.IndexOfAny(['\\', '/']);
        if (separator <= 0)
        {
            return (OtherKey, "Other");
        }

        var folder = item.Path[..separator];
        foreach (var (profileId, candidate) in settings.Profiles)
        {
            if (string.Equals(candidate.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                return (ProfileKey(profileId), candidate.Name);
            }
        }

        return (FolderKey(folder), folder);
    }

    /// <summary>The order of the groups: games by name first, then Monitor Recordings, then Other.</summary>
    public static int Rank(string key)
    {
        if (key == OtherKey)
        {
            return 2;
        }

        return key == MonitorRecordingsKey ? 1 : 0;
    }

    private static string ProfileKey(string id) => "profile:" + id;

    private static string FolderKey(string folder) => "folder:" + folder.ToUpperInvariant();
}
