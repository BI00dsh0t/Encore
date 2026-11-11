namespace Encore.Platform.Windows;

/// <summary>Store apps, whose folder names their version: a key that stays the same when the app updates.</summary>
public static class StorePackages
{
    private const string Folder = @"\windowsapps\";

    /// <summary>
    /// For a path under a <c>WindowsApps</c> package folder, the path with the folder's version, architecture and
    /// resource id taken out, keeping the package name, publisher and the program; null for any other path.
    /// </summary>
    public static string? Key(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var at = path.IndexOf(Folder, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + Folder.Length;
        var end = path.IndexOf('\\', start);
        if (end < 0)
        {
            return null;
        }

        // A package full name is Name_Version_Architecture_ResourceId_PublisherId; a name has no underscore.
        var parts = path[start..end].Split('_');
        if (parts.Length != 5 || parts[0].Length == 0 || parts[4].Length == 0)
        {
            return null;
        }

        return (path[..start] + parts[0] + "__" + parts[4] + path[end..]).ToLowerInvariant();
    }

    /// <summary>Whether two paths are the same program of the same Store app, whatever versions they name.</summary>
    public static bool SameProgram(string first, string second) =>
        Key(first) is { } key && string.Equals(key, Key(second), StringComparison.Ordinal);
}
