namespace Encore.Engine.Settings;

/// <summary>Replaces a file whole: writes a temp file beside it, flushes it to disk, then moves it over the file.</summary>
internal static class AtomicFile
{
    /// <summary>Writes path through its temp file; on an I/O failure the temp file is deleted and the error rethrown.</summary>
    public static void Replace(string path, Action<Stream> write, Action? beforeReplace = null)
    {
        var temporary = TemporaryPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            beforeReplace?.Invoke();
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Deletes the temp file a write that died part way left.</summary>
    public static void DeleteLeftover(string path) => TryDelete(TemporaryPath(path));

    private static string TemporaryPath(string path) => path + ".tmp";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp file that can't be deleted is overwritten by the next write.
        }
    }
}
