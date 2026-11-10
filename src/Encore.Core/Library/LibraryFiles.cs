using Encore.Contracts.Messages;
using Encore.Core.Output;

namespace Encore.Core.Library;

/// <summary>What the Library reads from disk: the files under a folder, and what an item found there looks like.</summary>
internal static class LibraryFiles
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,

        // Hidden and system files are listed; a junction or symbolic link is neither followed nor listed.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>A file found on disk: the profile of its top-level folder, and its creation time.</summary>
    public static LibraryItem Scanned(FileInfo file, string key, Func<string, string?> profileForFolder)
    {
        var separator = key.IndexOf(Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var profileId = separator > 0 ? profileForFolder(key[..separator]) : null;
        return new LibraryItem(key, profileId, file.Length, new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero), false, Modified: Utc(file.LastWriteTimeUtc), Kind: ScannedKind(key));
    }

    /// <summary>A scanned file's kind: a Monitor Recording when it is in that folder; otherwise unknown.</summary>
    public static string? ScannedKind(string key) =>
        key.StartsWith(ProfileFolders.MonitorRecordings + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? LibraryItemKinds.MonitorRecording
            : null;

    /// <summary>A file's <see cref="FileSystemInfo.LastWriteTimeUtc"/> as an item's <see cref="LibraryItem.Modified"/>.</summary>
    public static DateTimeOffset Utc(DateTime lastWriteUtc) => new(lastWriteUtc, TimeSpan.Zero);

    /// <summary>Whether the file is a complete MP4 now; a busy or unreadable file isn't.</summary>
    public static bool CompleteNow(string path)
    {
        try
        {
            return Mp4File.IsComplete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The files under a folder with an extension; a failed enumeration is reported, keeping what it found.</summary>
    public static List<string> Files(string folder, string extension, Action<Exception> failed)
    {
        var files = new List<string>();
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*" + extension, Recursive))
                {
                    // The pattern alone would also match a longer extension on some file systems.
                    if (string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed(e);
        }

        return files;
    }
}
