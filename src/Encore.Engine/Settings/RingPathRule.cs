using Encore.Contracts.Settings;
using Encore.Core.Settings;

namespace Encore.Engine.Settings;

/// <summary>
/// AD-17's rule, enforced by the engine as the one settings writer (AD-4, story 10.9): the cache folder
/// (<c>ringPath</c>, or its default) and the Clips folder (<c>outputFolder</c>, or its default) never
/// nest, in either direction. Pure.
/// </summary>
internal static class RingPathRule
{
    /// <summary>The refusal's text; the App shows its own words for each folder picker.</summary>
    public const string Refusal = "The cache folder and the Clips folder can't be inside each other.";

    /// <summary>Whether <paramref name="document"/>'s cache and Clips folders nest, either way.</summary>
    public static bool Nests(SettingsDocument document, string defaultCache, string defaultClips)
    {
        ArgumentNullException.ThrowIfNull(document);
        var resolved = SettingsResolver.Resolve(document, null);
        var cache = resolved.RingPath is { Length: > 0 } ring ? ring : defaultCache;
        var clips = resolved.OutputFolder is { Length: > 0 } output ? output : defaultClips;
        return IsInside(cache, clips) || IsInside(clips, cache);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it, ignoring case.</summary>
    public static bool IsInside(string path, string folder)
    {
        static string WithSeparator(string p) => Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;

        try
        {
            return WithSeparator(Path.GetFullPath(path)).StartsWith(WithSeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
