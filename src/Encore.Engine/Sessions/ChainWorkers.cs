using Microsoft.Extensions.Logging;

namespace Encore.Engine.Sessions;

/// <summary>The background threads a session's chain work runs on. Not thread-safe: the session calls it under its chain lock.</summary>
internal sealed class ChainWorkers(ILogger log)
{
    private readonly List<Thread> _threads = [];

    /// <summary>Starts a named background thread; a failure of its work is logged, not thrown.</summary>
    public void Start(string name, Action work)
    {
        _threads.RemoveAll(t => !t.IsAlive);
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                log.LogError(e, "{Name} failed", name);
            }
        })
        {
            Name = name,
            IsBackground = true,
        };
        _threads.Add(thread);
        thread.Start();
    }

    /// <summary>Hands over every thread still listed, for the caller to join outside the lock, and forgets them.</summary>
    public List<Thread> TakeAll()
    {
        List<Thread> taken = [.. _threads];
        _threads.Clear();
        return taken;
    }
}
