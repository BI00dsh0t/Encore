using System.Text.Json;

namespace Encore.Platform.Windows.Stores;

/// <summary>Epic's games: the <c>InstallLocation</c> of each install manifest in the launcher's data folder.</summary>
public sealed class EpicLibraries(string? manifestsFolder = null) : IStoreReader
{
    /// <summary>The Epic Games Launcher's manifests folder.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public GameStore Store => GameStore.Epic;

    public IReadOnlyList<string> Folders(ICollection<string> unreadable)
    {
        var folder = manifestsFolder ?? DefaultFolder;
        if (!Directory.Exists(folder))
        {
            return [];
        }

        List<string> folders = [];
        foreach (var manifest in Directory.EnumerateFiles(folder, "*.item"))
        {
            if (InstallLocation(manifest) is { } location)
            {
                folders.Add(location);
            }
            else
            {
                unreadable.Add(manifest);
            }
        }

        return folders;
    }

    // Null when the manifest can't be read or has no install location.
    private static string? InstallLocation(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("InstallLocation", out var location)
                && location.ValueKind == JsonValueKind.String
                && location.GetString() is { Length: > 0 } path
                    ? path
                    : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
