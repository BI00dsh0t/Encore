namespace Encore.Core.Library;

/// <summary>The Library's rules for paths: an item's key under the Output Folder, and which names and files are its own.</summary>
internal static class LibraryPaths
{
    /// <summary>The extension of every Library item.</summary>
    public const string Extension = ".mp4";

    /// <summary>What a Clip is written to before its rename (the writer's <c>ClipWriter.PartExtension</c>).</summary>
    public const string PartExtension = ".rbpart";

    // Device names Windows reserves, whatever the extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Whether a name can be a file's base name: not empty, no forbidden character, no trailing dot, not reserved.</summary>
    public static bool ValidName(string? name)
    {
        if (string.IsNullOrEmpty(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.')
            || name.EndsWith(' '))
        {
            return false;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return !ReservedNames.Contains(dot < 0 ? name : name[..dot]);
    }

    public static string FullFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    public static bool IsMp4(string path) => string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the path, or a folder above it up to the given folder, is a junction or link; true when unreadable.</summary>
    public static bool ThroughReparsePoint(string folder, string path)
    {
        try
        {
            // The Output Folder itself may be a junction; the file and each folder below it may not.
            for (var at = path; at is not null && at.Length > folder.Length; at = Path.GetDirectoryName(at))
            {
                if (File.GetAttributes(at).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Can't tell, so Cleanup doesn't touch it.
            return true;
        }
    }

    /// <summary>The key of a path under a folder; false for a path outside it, or the folder itself.</summary>
    public static bool TryKey(string folder, string path, out string key)
    {
        key = Path.GetRelativePath(folder, path);
        return key != "."
            && key != ".."
            && !Path.IsPathRooted(key)
            && !key.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
