using System.Text;

namespace ReplayBuffer.Core.Output;

/// <summary>The one file- and folder-name sanitizer for output paths (AD-21).</summary>
public static class PathNames
{
    /// <summary>The name of a game folder or file whose name sanitizes to nothing.</summary>
    internal const string GameFallback = "Game";

    private const int MaxLength = 100;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// <paramref name="name"/> made safe as one Windows path segment: invalid and control characters
    /// become <c>_</c>, surrounding spaces and trailing dots go, it is cut to 100 characters, and a
    /// name whose part before the first <c>.</c> is a reserved device name is prefixed <c>_</c>. A name with nothing left is <paramref name="fallback"/>.
    /// </summary>
    public static string Sanitize(string name, string fallback)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fallback);

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var safe = builder.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length > MaxLength)
        {
            safe = safe[..MaxLength].TrimEnd('.', ' ');
        }

        if (safe.Length == 0 || safe.All(c => c == '_'))
        {
            return fallback;
        }

        var dot = safe.IndexOf('.');
        var stem = (dot < 0 ? safe : safe[..dot]).TrimEnd(' ');
        return ReservedNames.Contains(stem) ? "_" + safe : safe;
    }
}
