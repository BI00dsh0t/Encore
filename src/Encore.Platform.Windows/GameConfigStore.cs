using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Encore.Platform.Windows;

/// <summary>Windows' own list of games, read from the registry; a failed read keeps the last good list. Not thread-safe.</summary>
public sealed class GameConfigStore(ILogger log)
{
    /// <summary>The least time between two reads of the registry.</summary>
    public static readonly TimeSpan RereadInterval = TimeSpan.FromSeconds(5);

    private const string ChildrenKey = @"System\GameConfigStore\Children";

    private readonly ILogger _log = log ?? throw new ArgumentNullException(nameof(log));

    // Raw path to its canonical form, or to its lowercase raw form when it can't be made canonical.
    private readonly Dictionary<string, string> _mapped = new(StringComparer.Ordinal);

    private IReadOnlySet<string> _paths = new HashSet<string>(StringComparer.Ordinal);
    private long? _readAt;
    private bool _logged;

    /// <summary>The listed games' paths; the same set instance until the list changes.</summary>
    public IReadOnlySet<string> Paths()
    {
        if (_readAt is { } readAt && Stopwatch.GetElapsedTime(readAt) < RereadInterval)
        {
            return _paths;
        }

        _readAt = Stopwatch.GetTimestamp();
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(ChildrenKey, writable: false);
            if (children is null)
            {
                if (FirstFailure())
                {
                    _log.LogWarning("Windows' game list (GameConfigStore) is missing; only fullscreen and hand-added games are detected");
                }

                return _paths;
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in children.GetSubKeyNames())
            {
                using var child = children.OpenSubKey(name, writable: false);
                if (child?.GetValue("Type") is int type && type == 1
                    && child.GetValue("MatchedExeFullPath") is string path && !string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(Map(path));
                }
            }

            if (!paths.SetEquals(_paths))
            {
                _paths = paths;
            }
        }
        catch (Exception e)
        {
            if (FirstFailure())
            {
                _log.LogWarning(e, "Windows' game list (GameConfigStore) couldn't be read; keeping the last one read");
            }
        }

        return _paths;
    }

    private string Map(string raw)
    {
        if (_mapped.TryGetValue(raw, out var mapped))
        {
            return mapped;
        }

        // Canonical, or in lowercase when it is not on disk (an uninstalled game).
        mapped = GamePaths.CanonicalOrLowercase(raw);
        _mapped[raw] = mapped;
        return mapped;
    }

    // Logged once per engine run.
    private bool FirstFailure()
    {
        var first = !_logged;
        _logged = true;
        return first;
    }
}
