using Microsoft.Extensions.Logging;
using ReplayBuffer.Core.Library;

namespace ReplayBuffer.Engine.Sessions;

/// <summary>
/// The Recordings the last engine run left open (FR-12, AD-16), at start: each file is trimmed back to
/// its last whole fragment, or deleted when it holds none, and never resumed.
/// </summary>
internal static class InterruptedRecordings
{
    /// <summary>
    /// Trims or deletes each of <paramref name="paths"/>, and returns the <c>recording-interrupted</c>
    /// message for each, in order, naming its path. A failure is logged, never thrown.
    /// </summary>
    public static IReadOnlyList<string> Recover(IReadOnlyList<string> paths, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        var messages = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            messages.Add($"The Recording {path} was cut short: ReplayBuffer's background service stopped while it was being written. {RecoverOne(path, log)}");
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
            return "It was kept up to its last whole fragment, and not resumed.";
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
