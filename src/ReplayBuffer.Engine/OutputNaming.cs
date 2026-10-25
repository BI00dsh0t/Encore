using System.Globalization;
using System.Text;

namespace ReplayBuffer.Engine;

/// <summary>
/// Debug-capture file names, <c>&lt;title&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;.mp4</c>. A tracer
/// shortcut: real writers get their paths from OutputPathAllocator (AD-21).
/// </summary>
public static class OutputNaming
{
    private const int MaxTitleLength = 100;
    private const string Fallback = "capture";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A window title made safe as a file name: characters Windows doesn't allow become <c>_</c>.</summary>
    public static string Sanitize(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            builder.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length > MaxTitleLength)
        {
            name = name[..MaxTitleLength].TrimEnd('.', ' ');
        }

        if (name.Length == 0 || name.All(c => c == '_'))
        {
            return Fallback;
        }

        return ReservedNames.Contains(name) ? "_" + name : name;
    }

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
