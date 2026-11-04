using System.Globalization;
using ReplayBuffer.Core.Output;

namespace ReplayBuffer.Engine;

/// <summary>
/// Debug-capture file names, <c>&lt;title&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;.mp4</c>, for the tracer's
/// <c>capture</c> command and a serve session without a Replay Buffer (<c>--debug-capture</c> and
/// test instances). Clips and Recordings get their paths from OutputPathAllocator (AD-21).
/// </summary>
public static class OutputNaming
{
    /// <summary>A window title made safe as a file name: characters Windows doesn't allow become <c>_</c>.</summary>
    public static string Sanitize(string title) => PathNames.Sanitize(title, "capture");

    /// <summary>
    /// The first free path in <paramref name="folder"/> for a capture of <paramref name="title"/>
    /// started at <paramref name="localStart"/>, adding <c>_2</c>, <c>_3</c>… on a collision.
    /// </summary>
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
