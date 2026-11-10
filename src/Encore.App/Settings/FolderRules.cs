using System.IO;

namespace Encore.App.Settings;

/// <summary>The rule the Clips folder and the cache folder share: neither may hold the other, and both must be writable.</summary>
internal static class FolderRules
{
    public const string CacheInsideClips = "The cache folder can't be inside the Clips folder.";
    public const string CantWrite = "Encore can't write to that folder.";
    public const string ClipsContainsCache = "The Clips folder can't contain the cache folder.";

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it, ignoring case.</summary>
    public static bool IsInside(string path, string folder) =>
        WithSeparator(Path.GetFullPath(path)).StartsWith(WithSeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);

    // A drive root keeps its separator, so add one only where it's missing.
    private static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
