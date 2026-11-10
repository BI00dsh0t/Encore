using System.Globalization;
using Encore.Core.Output;

namespace Encore.Engine;

/// <summary>Debug-capture file names, title then date and time; Clips and Recordings are named by OutputPathAllocator.</summary>
public static class OutputNaming
{
    /// <summary>A window title made safe as a file name: characters Windows doesn't allow become <c>_</c>.</summary>
    public static string Sanitize(string title) => PathNames.Sanitize(title, "capture");

    /// <summary>The first free path in a folder for a capture of this title, adding <c>_2</c>, <c>_3</c>… on a collision.</summary>
    public static string Allocate(string folder, string title, DateTime localStart, Func<string, bool> exists)
    {
        var stem = $"{Sanitize(title)}_{localStart.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(folder, stem + ".mp4");
        for (var n = 2; exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem}_{n}.mp4");
        }

        return path;
    }
}
