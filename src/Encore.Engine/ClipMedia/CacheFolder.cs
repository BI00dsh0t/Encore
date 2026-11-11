namespace Encore.Engine.ClipMedia;

/// <summary>The folder a cache is prepared into, which the App creates empty and owns.</summary>
internal static class CacheFolder
{
    /// <summary>The destination, refused when it is missing, absent or not empty; kind names the cache in the message.</summary>
    public static string RequireEmpty(string? destination, string kind)
    {
        var folder = destination ?? throw new IOException($"A {kind} cache folder is required.");
        if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new IOException($"The {kind} cache needs an empty owned folder.");
        }

        return folder;
    }
}
