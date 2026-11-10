using Microsoft.Extensions.Logging;

namespace Encore.Platform.FFmpeg;

/// <summary>What is left of a Recording's file after the disk filled during it.</summary>
internal static class RecordingFile
{
    /// <summary>Cuts the file back to its header and whole fragments, or deletes it when there are none.</summary>
    public static void KeepWholeFragments(string path, long bytes, ILogger logger)
    {
        if (bytes == 0)
        {
            logger.LogWarning("The disk filled before the Recording {Path} had a whole fragment; it is deleted", path);
            OutputFiles.TryDelete(path, logger);
            return;
        }

        try
        {
            // Shrinking a file needs no free space.
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                file.SetLength(bytes);
            }

            logger.LogWarning("The disk filled during the Recording {Path}; it keeps its {Bytes} bytes of whole fragments", path, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not cut the Recording {Path} back to its whole fragments: {Message}", path, e.Message);
        }
    }
}
