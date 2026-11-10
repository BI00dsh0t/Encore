namespace Encore.Core.Library;

/// <summary>The paths of the actor's own renames and deletes: the watcher's echo of each is ignored for a few seconds. Actor thread.</summary>
internal sealed class OwnFileOperations(TimeProvider time, Action<string, long> postExpired)
{
    // How long the watcher's echo of the actor's own file operation is ignored.
    private static readonly TimeSpan RegistrationTime = TimeSpan.FromSeconds(5);

    // Each registered path, by absolute path, to its registration's id and expiry timer.
    private readonly Dictionary<string, (long Id, ITimer Timer)> _registered = new(StringComparer.OrdinalIgnoreCase);
    private long _registrations;

    /// <summary>Whether the path is one of the actor's own operations, not yet expired.</summary>
    public bool Covers(string path) => _registered.ContainsKey(path);

    /// <summary>Registers a path before the actor changes its file; a path registered again starts its time over.</summary>
    public void Register(string path)
    {
        if (_registered.Remove(path, out var previous))
        {
            previous.Timer.Dispose();
        }

        var id = ++_registrations;
        var timer = time.CreateTimer(_ => postExpired(path, id), null, RegistrationTime, Timeout.InfiniteTimeSpan);
        _registered[path] = (id, timer);
    }

    /// <summary>A registration's time is up: true when it was still the path's current one; the path is then covered no more.</summary>
    public bool Expire(string path, long id)
    {
        if (!_registered.TryGetValue(path, out var registration) || registration.Id != id)
        {
            return false;
        }

        _registered.Remove(path);
        registration.Timer.Dispose();
        return true;
    }

    /// <summary>Forgets every registration and stops its timer.</summary>
    public void Clear()
    {
        foreach (var registration in _registered.Values)
        {
            registration.Timer.Dispose();
        }

        _registered.Clear();
    }
}
