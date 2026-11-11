using Microsoft.Win32;

namespace Encore.Platform.Windows.Stores;

/// <summary>A store that records each installed game as a registry key with its folder: GOG and Ubisoft Connect.</summary>
public sealed class RegistryLibraries(GameStore store, string gamesKey, string folderValue, IRegistryReader registry) : IStoreReader
{
    public GameStore Store => store;

    /// <summary>GOG Galaxy's games, each with its <c>path</c>.</summary>
    public static RegistryLibraries Gog(IRegistryReader registry) =>
        new(GameStore.Gog, @"SOFTWARE\WOW6432Node\GOG.com\Games", "path", registry);

    /// <summary>Ubisoft Connect's games, each with its <c>InstallDir</c>.</summary>
    public static RegistryLibraries Ubisoft(IRegistryReader registry) =>
        new(GameStore.Ubisoft, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", "InstallDir", registry);

    public IReadOnlyList<string> Folders(ICollection<string> unreadable)
    {
        List<string> folders = [];
        foreach (var game in registry.SubKeys(RegistryHive.LocalMachine, gamesKey))
        {
            if (registry.Value(RegistryHive.LocalMachine, $@"{gamesKey}\{game}", folderValue) is { Length: > 0 } folder)
            {
                folders.Add(folder.Replace('/', '\\'));
            }
        }

        return folders;
    }
}
