using Microsoft.Extensions.Logging;
using Encore.Core.Library;

namespace Encore.Engine.Sessions;

/// <summary>At start, trims each Recording the last run left open to its last whole fragment; they are never resumed.</summary>
internal static class InterruptedRecordings
{
    /// <summary>Trims or deletes each path and returns its <c>recording-interrupted</c> message; a failure is logged.</summary>
    public static IReadOnlyList<string> Recover(IReadOnlyList<string> paths, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        var messages = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            messages.Add($"The Recording {path} was cut short: Encore's background service stopped while it was being written. {RecoverOne(path, log)}");
        }

        return messages;
    }

    private static string RecoverOne(string path, ILogger log)
    {
        int fragments;
        try
        {
            if (!File.Exists(path))
            {
                log.LogWarning("The interrupted Recording {Path} is gone", path);
                return "Its file is gone.";
            }

            fragments = Mp4File.TrimToLastFragment(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.LogWarning("The interrupted Recording {Path} could not be trimmed: {Message}", path, e.Message);
            return "Its file couldn't be trimmed, and may not play to its end.";
        }

        if (fragments > 0)
        {
            log.LogWarning("The interrupted Recording {Path} was trimmed to its last whole fragment ({Fragments} kept)", path, fragments);
            return "It was kept up to the last part that was fully written, and not resumed.";
        }

        try
        {
            File.Delete(path);
            log.LogWarning("The interrupted Recording {Path} held no whole fragment and was deleted", path);
            return "Nothing of it was written, so its file was removed.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.LogWarning("The interrupted Recording {Path} held no whole fragment and could not be deleted: {Message}", path, e.Message);
            return "Nothing of it was written, and its empty file couldn't be removed.";
        }
    }
}
