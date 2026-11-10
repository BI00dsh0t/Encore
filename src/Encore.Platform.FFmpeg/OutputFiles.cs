using Microsoft.Extensions.Logging;

namespace Encore.Platform.FFmpeg;

/// <summary>Removing what a failed write left on the disk.</summary>
internal static class OutputFiles
{
    /// <summary>Deletes a file; a failure is logged as a warning, never thrown.</summary>
    public static void TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete {Path}: {Message}", path, e.Message);
        }
    }
}
