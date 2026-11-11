using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Encore.Platform.Windows.Stores;

/// <summary>Every store's game folders, re-read at most every 5 s; a store that fails keeps its last list. Not thread-safe.</summary>
public sealed class StoreLibraries
{
    /// <summary>The least time between two reads.</summary>
    public static readonly TimeSpan RereadInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IStoreReader> _readers;
    private readonly ILogger _log;
    private readonly Dictionary<GameStore, IReadOnlyList<StoreFolder>> _lastGood = [];
    private readonly HashSet<GameStore> _logged = [];
    private IReadOnlyList<StoreFolder> _folders = [];
    private long? _readAt;

    public StoreLibraries(ILogger log, IReadOnlyList<IStoreReader>? readers = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _readers = readers ?? Real(WindowsRegistry.Instance);
    }

    /// <summary>The readers of Steam, the Xbox app, Epic, GOG and Ubisoft Connect on this PC.</summary>
    public static IReadOnlyList<IStoreReader> Real(IRegistryReader registry) =>
    [
        new SteamLibraries(registry),
        new XboxLibraries(),
        new EpicLibraries(),
        RegistryLibraries.Gog(registry),
        RegistryLibraries.Ubisoft(registry),
    ];

    /// <summary>The store folders; the same list instance until a reread changes them.</summary>
    public IReadOnlyList<StoreFolder> Folders()
    {
        if (_readAt is { } readAt && Stopwatch.GetElapsedTime(readAt) < RereadInterval)
        {
            return _folders;
        }

        _readAt = Stopwatch.GetTimestamp();
        var folders = _readers.SelectMany(Read).ToList();
        if (!folders.SequenceEqual(_folders))
        {
            _folders = folders;
        }

        return _folders;
    }

    /// <summary>The store whose folder holds the path, the deepest folder first; null when none does.</summary>
    public static GameStore? StoreOf(IReadOnlyList<StoreFolder> folders, string path)
    {
        ArgumentNullException.ThrowIfNull(folders);
        StoreFolder? found = null;
        foreach (var folder in folders)
        {
            if (path.StartsWith(folder.Path, StringComparison.OrdinalIgnoreCase) && folder.Path.Length > (found?.Path.Length ?? 0))
            {
                found = folder;
            }
        }

        return found?.Store;
    }

    /// <summary>A store folder as matched against canonical paths: canonical, or lowercase, with a trailing <c>\</c>.</summary>
    public static string Normalize(string folder)
    {
        var full = GamePaths.CanonicalOrLowercase(Path.GetFullPath(folder.Trim()));
        return full.EndsWith('\\') ? full : full + '\\';
    }

    // One reader's folders; when it fails, its last good list, logged once per store.
    private IReadOnlyList<StoreFolder> Read(IStoreReader reader)
    {
        var unreadable = new List<string>();
        try
        {
            var folders = reader.Folders(unreadable)
                .Where(folder => !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder.Trim()))
                .Select(folder => new StoreFolder(Normalize(folder), reader.Store))
                .Distinct()
                .ToList();
            _lastGood[reader.Store] = folders;
            if (unreadable.Count > 0 && _logged.Add(reader.Store))
            {
                _log.LogWarning("{Store}'s game list couldn't be read in full ({Files}); its other games are still found",
                    reader.Store, string.Join(", ", unreadable));
            }

            return folders;
        }
        catch (Exception e)
        {
            if (_logged.Add(reader.Store))
            {
                _log.LogWarning(e, "{Store}'s game folders couldn't be read; keeping the last ones read", reader.Store);
            }

            return _lastGood.GetValueOrDefault(reader.Store, []);
        }
    }
}
