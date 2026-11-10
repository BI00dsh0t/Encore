namespace Encore.Core.Library;

/// <summary>The paths watcher events named, checked against disk once the events settle. Actor thread only.</summary>
internal sealed class PendingChecks
{
    // How long watcher events gather before their paths are checked, and the retry of a busy file.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _timer;
    private bool _armed;

    /// <summary>Whether the whole folder is to be scanned again, in place of single paths.</summary>
    public bool RescanPending { get; private set; }

    /// <summary>Starts the settle timer; until then nothing is armed.</summary>
    public void Start(TimeProvider time, Action postSettled) =>
        _timer = time.CreateTimer(_ => postSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public bool Contains(string path) => _paths.Contains(path);

    public void Add(string path)
    {
        _paths.Add(path);
        Arm();
    }

    public void AddRescan()
    {
        RescanPending = true;
        Arm();
    }

    /// <summary>The settle timer fired: the next path arms it again.</summary>
    public void Settled() => _armed = false;

    /// <summary>The paths to check now; none are left pending.</summary>
    public List<string> TakePaths()
    {
        var paths = _paths.ToList();
        _paths.Clear();
        return paths;
    }

    /// <summary>Drops every path and the rescan: the whole folder is about to be read.</summary>
    public void Clear()
    {
        RescanPending = false;
        _paths.Clear();
    }

    public void Stop() => _timer?.Dispose();

    // Not re-armed by later events, so a path that keeps changing is still checked every 250 ms.
    private void Arm()
    {
        if (!_armed && _timer is not null)
        {
            _armed = true;
            _timer.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }
}
