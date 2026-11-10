using System.Text.Json;

namespace Encore.Core.Library;

/// <summary>The index file, <c>library.json</c>: reading it, or its backup, at start. docs/code-notes.md.</summary>
internal static class LibraryIndexFile
{
    public const string BakExtension = ".bak";

    private const string CorruptExtension = ".corrupt";

    private enum ReadResult
    {
        Missing,
        Read,
        Bad,
    }

    /// <summary>The saved entries from <c>library.json</c>, else its backup; corrupt when neither parses and one exists.</summary>
    public static (Dictionary<string, LibraryFileEntry> Entries, bool Corrupt) Read(string libraryFile, string folder, Action<Exception> failed)
    {
        var entries = new Dictionary<string, LibraryFileEntry>(StringComparer.OrdinalIgnoreCase);
        var main = TryRead(libraryFile, failed, out var document);
        var corrupt = false;
        if (main == ReadResult.Bad)
        {
            try
            {
                File.Move(libraryFile, libraryFile + CorruptExtension, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed(e);
            }
        }

        if (main != ReadResult.Read)
        {
            var bak = TryRead(libraryFile + BakExtension, failed, out document);
            corrupt = bak != ReadResult.Read && (main == ReadResult.Bad || bak == ReadResult.Bad);
        }

        // A rebuild not yet acknowledged keeps Cleanup blocked across a restart.
        corrupt |= document?.CleanupBlocked == true;
        if (document is null || (document.OutputFolder is { } saved && !SameFolder(saved, folder)))
        {
            return (entries, corrupt);
        }

        // The deserializer doesn't check list elements, so a hand-edited null is skipped.
        foreach (var entry in document.Items.Where(e => e is not null))
        {
            entries[entry.Path] = entry;
        }

        return (entries, corrupt);
    }

    /// <summary>Writes the index atomically: a temp file is flushed, then swapped in, keeping a backup.</summary>
    public static void Write(string libraryFile, LibraryFile document)
    {
        var temp = libraryFile + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(libraryFile))!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    JsonSerializer.Serialize(writer, document, LibraryFileJsonContext.Default.LibraryFile);
                }

                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(libraryFile))
            {
                File.Replace(temp, libraryFile, libraryFile + BakExtension, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, libraryFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    // The next save overwrites a temp file that couldn't be deleted.
    private static void DeleteQuietly(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool SameFolder(string saved, string folder)
    {
        try
        {
            return string.Equals(LibraryPaths.FullFolder(saved), folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ReadResult TryRead(string path, Action<Exception> failed, out LibraryFile? document)
    {
        document = null;
        try
        {
            if (!File.Exists(path))
            {
                return ReadResult.Missing;
            }

            document = JsonSerializer.Deserialize(File.ReadAllBytes(path), LibraryFileJsonContext.Default.LibraryFile)
                ?? throw new JsonException($"{Path.GetFileName(path)} is null.");
            return ReadResult.Read;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            failed(e);
            document = null;
            return ReadResult.Bad;
        }
    }
}
