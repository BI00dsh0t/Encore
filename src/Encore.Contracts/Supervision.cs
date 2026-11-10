namespace Encore.Contracts;

/// <summary>
/// The timings of two-way supervision (AD-16), kept once for both processes: the engine's
/// heartbeat, when the App counts the engine as hung, and the restart backoff both sides use.
/// </summary>
public static class Supervision
{
    /// <summary>How often the engine publishes <c>engine.heartbeat</c>.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(2);

    /// <summary>How many heartbeats in a row the engine may miss before the App counts it as hung.</summary>
    public const int MissedHeartbeats = 3;

    /// <summary>How long with no event at all counts as hung: 3 missed heartbeats, 6 s.</summary>
    public static TimeSpan SilenceLimit { get; } = HeartbeatInterval * MissedHeartbeats;

    /// <summary>How long a command may wait for its reply before the engine counts as hung.</summary>
    public static TimeSpan ReplyLimit { get; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the App checks the engine against <see cref="SilenceLimit"/> and <see cref="ReplyLimit"/>.</summary>
    public static TimeSpan CheckInterval { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The delay before each restart in a row; the last repeats.</summary>
    public static IReadOnlyList<TimeSpan> BackoffDelays { get; } =
        [.. new[] { 0, 1, 2, 4, 8, 16, 30 }.Select(seconds => TimeSpan.FromSeconds(seconds))];

    /// <summary>How long with no restart starts the backoff again from its first delay.</summary>
    public static TimeSpan BackoffReset { get; } = TimeSpan.FromSeconds(60);

    /// <summary>How many restarts within <see cref="CrashLoopWindow"/> the supervisors allow; one more is a crash loop.</summary>
    public const int CrashLoopRestarts = 3;

    /// <summary>The span <see cref="CrashLoopRestarts"/> counts over; it slides with each restart.</summary>
    public static TimeSpan CrashLoopWindow { get; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The crash-loop rule of both supervisors (AD-16): a restart is allowed while fewer than
/// <see cref="Supervision.CrashLoopRestarts"/> restarts fall within the last
/// <see cref="Supervision.CrashLoopWindow"/>; the next one is a crash loop. Not thread-safe.
/// </summary>
public sealed class CrashLoopGuard(TimeProvider time)
{
    private readonly Queue<DateTimeOffset> _restarts = new();

    /// <summary>Records a restart and returns true, or returns false for a crash loop and records nothing.</summary>
    public bool TryRestart()
    {
        var now = time.GetUtcNow();
        while (_restarts.TryPeek(out var oldest) && now - oldest >= Supervision.CrashLoopWindow)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= Supervision.CrashLoopRestarts)
        {
            return false;
        }

        _restarts.Enqueue(now);
        return true;
    }

    /// <summary>Forgets every restart, after the user restarts by hand.</summary>
    public void Reset() => _restarts.Clear();
}

/// <summary>
/// The delays between restarts in a row (AD-16): 0, 1, 2, 4, 8, 16, then 30 s, back to the start
/// once <see cref="Supervision.BackoffReset"/> passes with no restart. Not thread-safe.
/// </summary>
public sealed class RestartBackoff(TimeProvider time)
{
    private int _restarts;
    private DateTimeOffset? _lastRestart;

    /// <summary>The delay before the next restart, which counts as happening once it has passed.</summary>
    public TimeSpan Next()
    {
        var now = time.GetUtcNow();
        if (_lastRestart is { } last && now - last >= Supervision.BackoffReset)
        {
            _restarts = 0;
        }

        var delays = Supervision.BackoffDelays;
        var delay = delays[Math.Min(_restarts, delays.Count - 1)];
        _restarts++;
        _lastRestart = now + delay;
        return delay;
    }
}

/// <summary>
/// One copy of a process per instance (AD-16): a named mutex, <c>Local\Encore.{process}.v1.{instance}</c>,
/// held for the process's whole life. A mutex left abandoned by a process that died counts as acquired.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string Engine = "Engine";
    public const string App = "App";

    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstance(Mutex mutex, string name)
    {
        _mutex = mutex;
        Name = name;
    }

    /// <summary>The mutex's name.</summary>
    public string Name { get; }

    public static string MutexName(string process, string instance) => $@"Local\Encore.{process}.v1.{instance}";

    /// <summary>Takes the mutex for <paramref name="process"/> in <paramref name="instance"/>, or returns <c>null</c> when another copy holds it.</summary>
    public static SingleInstance? TryAcquire(string process, string instance)
    {
        ArgumentException.ThrowIfNullOrEmpty(process);
        ArgumentException.ThrowIfNullOrEmpty(instance);

        var name = MutexName(process, instance);
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // WAIT_ABANDONED: the last holder died holding it, and this thread now owns it.
        }

        return new SingleInstance(mutex, name);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from a thread other than the owner's: closing the handle frees it when the owner ends.
        }

        _mutex.Dispose();
    }
}
