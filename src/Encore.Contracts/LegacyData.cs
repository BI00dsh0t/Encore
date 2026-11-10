namespace Encore.Contracts;

/// <summary>
/// The app was called ReplayBuffer before it was rebranded to Encore (2026-11-10). On the first start of
/// an Encore instance, its data folders are copied once from the matching ReplayBuffer ones, so settings,
/// the Library index and the App's view state carry over. The old folders are never moved or deleted.
/// </summary>
public static class LegacyData
{
    /// <summary>The cache (ring) folder is rebuilt, never copied: it is large and holds nothing worth keeping.</summary>
    private const string SkippedFolder = "ring";

    /// <summary>The ReplayBuffer name an instance had, or <c>null</c> for a test instance.</summary>
    public static string? LegacyName(Instance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Name switch
        {
            Instance.InstalledName => "ReplayBuffer",
            Instance.DevName => "ReplayBuffer-Dev",
            _ => null,
        };
    }

    /// <summary>
    /// Copies the instance's old ReplayBuffer roaming and local data folders to its Encore ones, each only
    /// when the Encore folder doesn't exist yet and the old one does. The copy goes to a temporary folder
    /// first and is renamed into place, so a second process starting at the same moment sees either
    /// nothing or the whole copy. Failures are swallowed: a missed copy only means starting fresh.
    /// </summary>
    /// <returns>The folders copied, for the caller's log.</returns>
    public static IReadOnlyList<string> CopyIfFirstStart(Instance instance)
    {
        var legacy = LegacyName(instance);
        if (legacy is null)
        {
            return [];
        }

        var copied = new List<string>();
        foreach (var target in new[] { instance.RoamingDataFolder, instance.LocalDataFolder })
        {
            var parent = Path.GetDirectoryName(target);
            if (parent is null)
            {
                continue;
            }

            var source = Path.Combine(parent, legacy);
            try
            {
                if (Directory.Exists(target) || !Directory.Exists(source))
                {
                    continue;
                }

                var staging = target + ".migrating-" + Guid.NewGuid().ToString("N");
                CopyTree(source, staging);
                try
                {
                    Directory.Move(staging, target);
                    copied.Add(target);
                }
                catch (IOException)
                {
                    // Another process put the folder in place first.
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Start fresh rather than fail to start.
            }
        }

        return copied;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(folder);
            if (string.Equals(name, SkippedFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyTree(folder, Path.Combine(destination, name));
        }
    }
}
