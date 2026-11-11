using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Encore.Platform.Windows.Stores;

/// <summary>Steam's game folders: <c>steamapps\common</c> in Steam's own folder and in every library it lists.</summary>
public sealed partial class SteamLibraries(IRegistryReader registry) : IStoreReader
{
    public GameStore Store => GameStore.Steam;

    public IReadOnlyList<string> Folders(ICollection<string> unreadable)
    {
        if (registry.Value(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath") is not { Length: > 0 } steamPath)
        {
            return [];
        }

        var steam = steamPath.Replace('/', '\\');
        List<string> libraries = [steam];
        var listFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            libraries.AddRange(LibraryPaths(File.ReadAllText(listFile)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            unreadable.Add(listFile);
        }

        return [.. libraries.Select(library => Path.Combine(library, "steamapps", "common"))];
    }

    /// <summary>The library folders in <c>libraryfolders.vdf</c>: each <c>"path"</c> value, its <c>\\</c> made <c>\</c>.</summary>
    public static IReadOnlyList<string> LibraryPaths(string libraryFolders) =>
        [.. PathValue().Matches(libraryFolders).Select(match => match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal))];

    [GeneratedRegex("\"path\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathValue();
}
