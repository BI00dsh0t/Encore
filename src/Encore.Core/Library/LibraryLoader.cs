using Encore.Contracts.Messages;

namespace Encore.Core.Library;

/// <summary>Builds the Library at start, before its actor runs, from the index file and the files on disk.</summary>
public static class LibraryLoader
{
    /// <summary>The Library at start: clears a crash's leftovers, reads the index or its backup, then lists the files on disk.</summary>
    public static LibraryLoad Load(string outputFolder, string libraryFile, Func<string, string?> profileForFolder, Action<Exception> failed)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);
        ArgumentException.ThrowIfNullOrEmpty(libraryFile);
        ArgumentNullException.ThrowIfNull(profileForFolder);
        ArgumentNullException.ThrowIfNull(failed);

        var folder = LibraryPaths.FullFolder(outputFolder);
        var (saved, corrupt) = LibraryIndexFile.Read(libraryFile, folder, failed);
        var items = new List<LibraryItem>();
        if (!Directory.Exists(folder))
        {
            return new LibraryLoad(items, corrupt);
        }

        DeleteLeftovers(folder, failed);
        foreach (var path in LibraryFiles.Files(folder, LibraryPaths.Extension, failed))
        {
            if (!LibraryPaths.TryKey(folder, path, out var key))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(path);

                // An empty file is a reservation a write never filled (it ended with the engine).
                if (file.Length == 0)
                {
                    continue;
                }

                // A file the index doesn't know at this size must be a complete MP4; an old entry has no size.
                saved.TryGetValue(key, out var entry);
                var known = entry is not null && (entry.Size is null || entry.Size == file.Length);
                if (!known && !LibraryFiles.CompleteNow(path))
                {
                    continue;
                }

                // A cached length holds only while the file has the size and modified time it was read at.
                var length = entry is { LengthSeconds: { } seconds, Size: { } size, Modified: { } modified }
                    && size == file.Length && modified == file.LastWriteTimeUtc
                        ? seconds
                        : (double?)null;
                items.Add(entry is not null
                    ? new LibraryItem(key, entry.ProfileId, file.Length, entry.Date, false, length, entry.Favourite, LibraryFiles.Utc(file.LastWriteTimeUtc), entry.Kind ?? LibraryFiles.ScannedKind(key))
                    : LibraryFiles.Scanned(file, key, profileForFolder));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Gone or unreadable since the enumeration; the actor's start rescan checks it again.
            }
        }

        return new LibraryLoad(items, corrupt);
    }

    /// <summary>A crash's leftovers: each <c>*.rbpart</c>, and the empty reservation beside one. Never a user's <c>.mp4</c>.</summary>
    private static void DeleteLeftovers(string folder, Action<Exception> failed)
    {
        foreach (var part in LibraryFiles.Files(folder, LibraryPaths.PartExtension, failed))
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed(e);
            }

            try
            {
                var reservation = new FileInfo(part[..^LibraryPaths.PartExtension.Length]);
                if (LibraryPaths.IsMp4(reservation.FullName) && reservation.Exists && reservation.Length == 0)
                {
                    reservation.Delete();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed(e);
            }
        }
    }
}
