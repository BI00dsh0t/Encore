using System.Text;

namespace ReplayBuffer.Core.Output;

/// <summary>The one file- and folder-name sanitizer for output paths (AD-21).</summary>
public static class PathNames
{
    private const int MaxLength = 100;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// <paramref name="name"/> made safe as one Windows path segment: invalid and control characters
    /// become <c>_</c>, surrounding spaces and trailing dots go, it is cut to 100 characters, and a
    /// reserved device name is prefixed <c>_</c>. A name with nothing left is <paramref name="fallback"/>.
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

        return ReservedNames.Contains(safe) ? "_" + safe : safe;
    }
}
