using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Encore.Platform.Windows.Stores;

namespace Encore.Platform.Windows.Tests;

public sealed class StoreLibrariesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-stores-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();

    public StoreLibrariesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void SteamListsItsOwnFolderAndEveryLibraryAndABadListKeepsItsOwn()
    {
        var steam = Path.Combine(_folder, "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        _registry.Values[(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")] = steam.Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            """
            "libraryfolders"
            {
                "0" { "path"        "D:\\SteamLibrary" }
                "1" { "path"        "E:\\Games\\Steam" }
            }
            """);
        var reader = new SteamLibraries(_registry);

        Assert.Equal(
            [Path.Combine(steam, "steamapps", "common"), @"D:\SteamLibrary\steamapps\common", @"E:\Games\Steam\steamapps\common"],
            reader.Folders([]));

        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "not a library list {{{");
        Assert.Equal([Path.Combine(steam, "steamapps", "common")], reader.Folders([]));
    }

    [Fact]
    public void XboxReadsEachDrivesGamingRootAndNamesABadOne()
    {
        var good = Directory.CreateDirectory(Path.Combine(_folder, "C")).FullName;
        var bad = Directory.CreateDirectory(Path.Combine(_folder, "D")).FullName;
        File.WriteAllBytes(Path.Combine(good, ".GamingRoot"), [.. "RGBX"u8, 1, 0, 0, 0, .. Encoding.Unicode.GetBytes("XboxGames\0")]);
        File.WriteAllBytes(Path.Combine(bad, ".GamingRoot"), [1, 2, 3]);
        var unreadable = new List<string>();

        var folders = new XboxLibraries([good, bad]).Folders(unreadable);

        Assert.Equal([Path.Combine(good, "XboxGames")], folders);
        Assert.Equal([Path.Combine(bad, ".GamingRoot")], unreadable);
    }

    [Fact]
    public void EpicReadsEachManifestsInstallLocationAndNamesABadOne()
    {
        File.WriteAllText(Path.Combine(_folder, "a.item"), """{ "DisplayName": "Game", "InstallLocation": "C:\\Epic\\Game" }""");
        File.WriteAllText(Path.Combine(_folder, "b.item"), "{ broken");
        var unreadable = new List<string>();

        var folders = new EpicLibraries(_folder).Folders(unreadable);

        Assert.Equal([@"C:\Epic\Game"], folders);
        Assert.Equal([Path.Combine(_folder, "b.item")], unreadable);
    }

    [Fact]
    public void GogAndUbisoftReadEachGamesFolderFromTheRegistry()
    {
        const string gog = @"SOFTWARE\WOW6432Node\GOG.com\Games";
        const string ubisoft = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
        _registry.SubKeys[(RegistryHive.LocalMachine, gog)] = ["1207658924", "broken"];
        _registry.Values[(RegistryHive.LocalMachine, gog + @"\1207658924", "path")] = @"C:\GOG Games\Game";
        _registry.SubKeys[(RegistryHive.LocalMachine, ubisoft)] = ["635"];
        _registry.Values[(RegistryHive.LocalMachine, ubisoft + @"\635", "InstallDir")] = "C:/Ubisoft/Game/";

        Assert.Equal([@"C:\GOG Games\Game"], RegistryLibraries.Gog(_registry).Folders([]));
        Assert.Equal([@"C:\Ubisoft\Game\"], RegistryLibraries.Ubisoft(_registry).Folders([]));
    }

    [Fact]
    public void AFailingStoreIsSkippedAndTheDeepestFolderNamesTheStore()
    {
        var steam = Path.Combine(_folder, "steamapps", "common");
        var nested = Path.Combine(steam, "Tools", "Xbox");
        var libraries = new StoreLibraries(
            NullLogger.Instance,
            [new Throwing(), new Fixed(GameStore.Steam, steam), new Fixed(GameStore.Xbox, nested)]);

        var folders = libraries.Folders();

        Assert.Equal([GameStore.Steam, GameStore.Xbox], folders.Select(folder => folder.Store));
        Assert.All(folders, folder => Assert.EndsWith(@"\", folder.Path, StringComparison.Ordinal));
        Assert.Same(folders, libraries.Folders());
        Assert.Equal(GameStore.Steam, StoreLibraries.StoreOf(folders, Path.Combine(steam, "Game", "game.exe").ToLowerInvariant()));
        Assert.Equal(GameStore.Xbox, StoreLibraries.StoreOf(folders, Path.Combine(nested, "game.exe").ToLowerInvariant()));
        Assert.Null(StoreLibraries.StoreOf(folders, Path.Combine(_folder, "game.exe").ToLowerInvariant()));
    }

    private sealed class FakeRegistry : IRegistryReader
    {
        public Dictionary<(RegistryHive, string, string), string> Values { get; } = [];

        public Dictionary<(RegistryHive, string), IReadOnlyList<string>> SubKeys { get; } = [];

        public string? Value(RegistryHive hive, string key, string name) => Values.GetValueOrDefault((hive, key, name));

        IReadOnlyList<string> IRegistryReader.SubKeys(RegistryHive hive, string key) => SubKeys.GetValueOrDefault((hive, key), []);
    }

    private sealed class Fixed(GameStore store, string folder) : IStoreReader
    {
        public GameStore Store => store;

        public IReadOnlyList<string> Folders(ICollection<string> unreadable) => [folder];
    }

    private sealed class Throwing : IStoreReader
    {
        public GameStore Store => GameStore.Epic;

        public IReadOnlyList<string> Folders(ICollection<string> unreadable) => throw new IOException("no manifests");
    }
}
