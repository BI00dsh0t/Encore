using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Encore.App.Settings;

/// <summary>What the folder pickers ask of the disk and the shell: free space, whether a folder is writable, Explorer.</summary>
internal static class FolderProbe
{
    /// <summary>"212 GB", "1.8 TB": decimal units, one decimal under 10 and from TB.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e12:0.0} TB"),
        >= 10_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0} GB"),
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB"),
    };

    /// <summary>The free bytes of the drive holding a path, or null when unknown.</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenInExplorer(string folder)
    {
        using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" });
    }

    /// <summary>Whether Encore can write to a folder; a missing one counts when its nearest existing parent is writable.</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = NearestExisting(folder);
            if (probe is null)
            {
                return false;
            }

            var file = Path.Combine(probe, ".encore-write-check-" + Guid.NewGuid().ToString("N"));
            using (File.Create(file, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The folder itself or its nearest parent that exists; null when none does.
    private static string? NearestExisting(string folder)
    {
        string? probe = folder;
        while (probe is not null && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        return probe;
    }
}
